using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.ServiceBroker;

namespace ClaudeCodeVs.Terminal;

/// <summary>
/// Launches `claude` inside VS's own native Terminal tool window (the engine behind View > Terminal)
/// instead of an external cmd.exe console. `Microsoft.VisualStudio.Terminal.dll` (the ITerminalService
/// types) has no NuGet package, so it's loaded by reflection from the install dir at runtime - same idea
/// as Testing/TestRunner.cs's TestWindow integration, ship zero of its DLLs in the .vsix. The brokered-
/// service plumbing itself (IBrokeredServiceContainer/IServiceBroker/ServiceRpcDescriptor) IS a normal
/// compile-time reference - it's a transitive dependency of the VS SDK package already.
///
/// This surface is undocumented (no Learn page, no NuGet package) so it could change or vanish across a
/// VS update: every failure path here logs and returns false rather than throwing, so BridgeHost always
/// has the external cmd.exe console as a safety net.
/// </summary>
internal static class VsTerminalLauncher
{
    private const string AsmName = "Microsoft.VisualStudio.Terminal.dll";

    /// <summary>How long the native-terminal attempt may take before the external console takes over.</summary>
    private const int LaunchTimeoutMs = 10_000;

    public static async Task<bool> TryLaunchAsync(string? workingDirectory, int ssePort, CancellationToken ct)
    {
        try
        {
            // A stalled brokered-service acquisition (classically: ServiceHub still spinning up on a cold
            // VS start) must not leave the Launch button dead - the fallback only ever runs on *failure*,
            // not on a hang. Race the attempt against a hard timeout; if it loses, the linked token has
            // already cancelled it (so a late completion aborts instead of opening a second terminal).
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LaunchTimeoutMs);
            Task<bool> attempt = TryLaunchCoreAsync(workingDirectory, ssePort, timeoutCts.Token);
            _ = attempt.ContinueWith(t => { _ = t.Exception; timeoutCts.Dispose(); }, TaskScheduler.Default);

            // The grace over CancelAfter lets a cancellation-honoring call surface as a clean
            // OperationCanceledException inside the core first; this race only decides when the
            // call ignores its token outright.
            if (await Task.WhenAny(attempt, Task.Delay(LaunchTimeoutMs + 2_000, ct)) != attempt)
            {
                Log.Warn($"Native VS terminal: no response within {LaunchTimeoutMs / 1000}s (ServiceHub not ready?) - falling back to external console.");
                return false;
            }
            return await attempt;
        }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal launch failed ({e.GetType().Name}: {e.Message}) - falling back to external console.");
            return false;
        }
    }

    private static async Task<bool> TryLaunchCoreAsync(string? workingDirectory, int ssePort, CancellationToken ct)
    {
        try
        {
            var terminalServiceType = FindType("Microsoft.VisualStudio.Terminal.ITerminalService");
            var descriptorsType = FindType("Microsoft.VisualStudio.Terminal.TerminalServiceDescriptors");
            var windowOptionsType = FindType("Microsoft.VisualStudio.Terminal.TerminalWindowOptions");
            var profileConfigType = FindType("Microsoft.VisualStudio.Terminal.ProfileConfig");
            if (terminalServiceType == null || descriptorsType == null || profileConfigType == null)
            {
                Log.Warn("Native VS terminal: Microsoft.VisualStudio.Terminal types not found (VS edition/version mismatch) - falling back to external console.");
                return false;
            }
            // TerminalWindowOptions only exists on the 18.x (VS 2026) surface. Its absence means the
            // VS 2022 (17.x) contract, where CreateTerminalAsync takes name/profile/cwd as plain args
            // (issue #12 follow-up: same service, older shape - dumped headlessly from 17.14's DLL).

            var descriptor = descriptorsType
                .GetProperty("TerminalServiceDescriptor", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as ServiceRpcDescriptor;
            if (descriptor == null)
            {
                Log.Warn("Native VS terminal: TerminalServiceDescriptor missing - falling back to external console.");
                return false;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var containerObj = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsBrokeredServiceContainer));
            if (containerObj is not IBrokeredServiceContainer container)
            {
                Log.Warn("Native VS terminal: SVsBrokeredServiceContainer unavailable - falling back to external console.");
                return false;
            }
            IServiceBroker broker = container.GetFullAccessServiceBroker();
            // GetServiceAsync above takes NO token, and on a cold ServiceHub it can stall long past our
            // timeout - after which the fallback has already launched the external console. Explicit
            // gates from here down keep a late-running attempt from opening a SECOND terminal (seen
            // live 2026-08-06: 10s stall -> external console AND a late native tab, two sessions).
            ct.ThrowIfCancellationRequested();

            // T = ITerminalService is only known as a reflected Type, so the generic call needs
            // MakeGenericMethod. Overload-tolerant lookup: a bare GetMethod("GetProxyAsync") throws
            // AmbiguousMatchException the moment a ServiceHub.Framework servicing update adds an
            // overload - issue #12, seen on VS2022 AND VS2026 (the assembly ships to both). Select the
            // exact shape we call instead.
            var getProxyOpen = PickMethod(typeof(IServiceBroker), "GetProxyAsync", m =>
            {
                if (!m.IsGenericMethodDefinition) return false;
                var p = m.GetParameters();
                return p.Length == 3
                    && typeof(ServiceRpcDescriptor).IsAssignableFrom(p[0].ParameterType)
                    && p[1].ParameterType == typeof(ServiceActivationOptions)
                    && p[2].ParameterType == typeof(CancellationToken);
            });
            if (getProxyOpen == null)
            {
                Log.Warn("Native VS terminal: no compatible IServiceBroker.GetProxyAsync overload - falling back to external console.");
                return false;
            }
            var getProxy = getProxyOpen.MakeGenericMethod(terminalServiceType);
            object? terminalService = await UnwrapAsync(
                getProxy.Invoke(broker, new object?[] { descriptor, default(ServiceActivationOptions), ct }));
            if (terminalService == null)
            {
                Log.Warn("Native VS terminal: GetProxyAsync<ITerminalService> returned null - falling back to external console.");
                return false;
            }

            object? profile = null;
            try
            {
                // Last gate before side effects (AddCachedProfile / the create call): the proxy
                // acquisition may have ignored its token during a cold start.
                ct.ThrowIfCancellationRequested();

                // ProfileConfig's shape could drift across a VS update - select the 4-arg ctor explicitly
                // rather than trusting GetConstructors() ordering, and fail loudly into the fallback.
                var profileCtor = profileConfigType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 4);
                if (profileCtor == null)
                {
                    Log.Warn("Native VS terminal: ProfileConfig has no 4-arg constructor (VS update changed the shape) - falling back to external console.");
                    return false;
                }

                // /K keeps the window open after claude exits (parity with today's cmd.exe path); env vars are
                // baked into an inline `set` chain since TerminalWindowOptions has no EnvironmentVariables property.
                string args = $"/K set ENABLE_IDE_INTEGRATION=true&&set CLAUDE_CODE_SSE_PORT={ssePort}&&claude"
                            + (Ui.BridgeStatus.AutoAcceptEdits ? " --permission-mode acceptEdits" : ""); // run-wild at launch: see BridgeHost.LaunchClaudeAsync
                profile = profileCtor.Invoke(new object?[] { "Claude Code", "cmd.exe", args, false });

                // TerminalWindowOptions.Profile alone is ignored unless the profile is first registered with the
                // service - without this, CreateTerminalWindowAsync silently falls back to the user's default shell.
                PickMethod(terminalServiceType, "AddCachedProfile", m => m.GetParameters().Length == 1)?
                    .Invoke(terminalService, new object?[] { profile });

                // Same overload tolerance as GetProxyAsync, plus parameter-ORDER tolerance: pick the
                // 2-arg overload taking (options, token) in either order and build the call to match.
                var create = windowOptionsType == null ? null : PickMethod(terminalServiceType, "CreateTerminalWindowAsync", m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2
                        && p.Any(x => x.ParameterType == typeof(CancellationToken))
                        && p.Any(x => x.ParameterType.IsAssignableFrom(windowOptionsType));
                });

                object? guidResult;
                string surface;
                if (create != null)
                {
                    object options = Activator.CreateInstance(windowOptionsType!)!; // ctor already defaults Focus/AllowUserInput/AutoResize=true
                    windowOptionsType!.GetProperty("Name")?.SetValue(options, "Claude Code");
                    windowOptionsType.GetProperty("WorkingDirectory")?.SetValue(options, workingDirectory);
                    windowOptionsType.GetProperty("Profile")?.SetValue(options, profile);
                    windowOptionsType.GetProperty("Focus")?.SetValue(options, true);
                    windowOptionsType.GetProperty("AllowUserInput")?.SetValue(options, true);

                    object?[] callArgs = create.GetParameters()[0].ParameterType == typeof(CancellationToken)
                        ? new object?[] { ct, options }
                        : new object?[] { options, ct };
                    guidResult = await UnwrapAsync(create.Invoke(terminalService, callArgs));
                    surface = "18.x surface";
                }
                else
                {
                    // VS 2022 (17.x) legacy surface: CreateTerminalAsync(ct, name, ProfileConfig, cwd).
                    // Prefer the exact-ProfileConfig overload over its ITerminalProfile twin.
                    var createLegacy = PickMethod(terminalServiceType, "CreateTerminalAsync", m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[0].ParameterType == typeof(CancellationToken)
                            && p[1].ParameterType == typeof(string)
                            && p[2].ParameterType == profileConfigType
                            && p[3].ParameterType == typeof(string);
                    });
                    if (createLegacy == null)
                    {
                        Log.Warn("Native VS terminal: neither CreateTerminalWindowAsync (18.x) nor a compatible CreateTerminalAsync (17.x) found - falling back to external console.");
                        return false;
                    }
                    guidResult = await UnwrapAsync(createLegacy.Invoke(terminalService, new object?[] { ct, "Claude Code", profile, workingDirectory }));
                    surface = "17.x legacy surface";

                    // The old surface has no Focus option; ShowAsync is its bring-to-front. Best-effort.
                    if (guidResult is Guid g)
                    {
                        try
                        {
                            await UnwrapAsync(PickMethod(terminalServiceType, "ShowAsync",
                                    m => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Guid))?
                                .Invoke(terminalService, new object?[] { g, ct }));
                        }
                        catch { /* focus is cosmetic */ }
                    }
                }

                if (guidResult is not Guid)
                {
                    Log.Warn("Native VS terminal: terminal creation returned no guid - falling back to external console.");
                    return false;
                }

                Log.Info($"Launched Claude Code in VS's native Terminal window ({surface}, port {ssePort}, cwd '{workingDirectory ?? "(default)"}').");
                return true;
            }
            finally
            {
                // The cached profile is only needed while CreateTerminalWindowAsync resolves it (VS doesn't
                // use it for tab restore either - restored tabs come back as the default shell). Deregister
                // it immediately so "Claude Code" never accumulates in the terminal's profile dropdown.
                try
                {
                    if (profile != null)
                        PickMethod(terminalServiceType, "RemoveCachedProfile", m => m.GetParameters().Length == 1)?
                            .Invoke(terminalService, new object?[] { profile });
                }
                catch { /* best-effort cleanup; the profile cache is cosmetic */ }

                // Brokered-service proxies must be disposed - each GetProxyAsync hands out a live RPC client.
                (terminalService as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Lost the race to the timeout: the external-console fallback already took over. The gates
            // above guarantee no terminal was created on this path.
            Log.Info("Native VS terminal attempt aborted after the external-console fallback took over (no second terminal opened).");
            return false;
        }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal launch failed ({e.GetType().Name}: {e.Message}) - falling back to external console.");
            return false;
        }
    }

    /// <summary>
    /// Overload-tolerant method lookup. Type.GetMethod(name) THROWS AmbiguousMatchException the moment
    /// a VS/ServiceHub update adds an overload of a member we address by name (issue #12) - and this
    /// whole class talks to assemblies that update underneath us. Enumerate by name, filter by the
    /// call shape we intend, and among survivors prefer the fewest parameters, so a richer overload
    /// added later never breaks the existing call.
    /// </summary>
    private static MethodInfo? PickMethod(Type type, string name, Func<MethodInfo, bool>? where = null)
    {
        IEnumerable<MethodInfo> candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == name);
        if (where != null) candidates = candidates.Where(where);
        return candidates.OrderBy(m => m.GetParameters().Length).FirstOrDefault();
    }

    // ---------------- reflection plumbing (mirrors Testing/TestRunner.cs) ----------------

    private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private static Type? FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { }
        }
        try
        {
            string? ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (ide == null) return null;
            string path = Path.Combine(ide, "CommonExtensions", "Microsoft", "Terminal", AsmName);
            if (!File.Exists(path)) return null;
            if (!_loaded.TryGetValue(path, out var asm)) { asm = Assembly.LoadFrom(path); _loaded[path] = asm; }
            return asm.GetType(fullName, false);
        }
        catch { return null; }
    }

    private static async Task<object?> UnwrapAsync(object? ret)
    {
        if (ret == null) return null;
        var rt = ret.GetType();
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ret = rt.GetMethod("AsTask")!.Invoke(ret, null);
        if (ret is Task task)
        {
            await task.ConfigureAwait(true);
            var rp = task.GetType().GetProperty("Result");
            return rp != null && rp.PropertyType != typeof(void) ? rp.GetValue(task) : null;
        }
        return ret;
    }
}
