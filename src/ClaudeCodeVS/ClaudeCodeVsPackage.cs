using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Ui;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// VS package entry point. Auto-loads when the shell finishes initializing (so the bridge server is
/// up regardless of whether a solution is open), runs everything on a background-loadable async init,
/// and registers the "Launch Claude Code" command. See build-plan.md §2.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
// Register the extension folder as an assembly probe path. Our package assembly isn't strong-named,
// so the generated pkgdef uses "Assembly=" with no CodeBase; without a binding path, devenv's
// load-by-name (Activator.CreateInstance) can't locate ClaudeCodeVS.dll / ClaudeCodeVS.Protocol.dll
// and the package fails with "Could not load file or assembly 'ClaudeCodeVS' ...". This fixes that.
[ProvideBindingPath]
[ProvideMenuResource("Menus.ctmenu", 1)] // the compiled VSCommandTable.vsct
[ProvideToolWindow(typeof(ClaudeToolWindow))] // the dockable "Claude Code" panel
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class ClaudeCodeVsPackage : AsyncPackage
{
    private BridgeHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // UI language BEFORE anything renders: Strings.Culture follows VS's own display language
        // (Tools > Options > International Settings), read from the shell rather than the thread
        // culture - UI strings get composed on background HTTP-handler threads too. Runs before the
        // tool window can exist (VS finishes package init before creating its tool windows).
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        InitUiCulture();
        await TaskScheduler.Default; // hop back off the UI thread for server startup

        _host = new BridgeHost(this);
        await _host.StartAsync(cancellationToken);

        // Register the Tools -> "Launch Claude Code" and "Claude Code Panel" commands.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
        {
            mcs.AddCommand(new MenuCommand(OnLaunchClaude,
                new CommandID(PackageGuids.CommandSet, PackageIds.LaunchClaude)));
            mcs.AddCommand(new MenuCommand(OnShowPanel,
                new CommandID(PackageGuids.CommandSet, PackageIds.ShowPanel)));
        }
    }

    /// <summary>
    /// Point <see cref="Strings.Culture"/> at VS's display-language LCID (2052 = zh-CN resolves the
    /// zh-Hans satellite via the standard parent-chain fallback; anything untranslated falls back to
    /// English per-string). Any failure leaves Culture null = neutral English.
    /// </summary>
    private void InitUiCulture()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (GetService(typeof(SUIHostLocale)) is IUIHostLocale hostLocale
                && ErrorHandler.Succeeded(hostLocale.GetUILocale(out uint lcid))
                && lcid > 0)
            {
                Strings.Culture = new System.Globalization.CultureInfo((int)lcid);
            }
        }
        catch (Exception ex)
        {
            Protocol.Log.Warn($"UI culture detection failed, staying English: {ex.Message}");
        }
    }

    private void OnLaunchClaude(object sender, EventArgs e)
    {
        var host = _host;
        if (host is null) return;
        JoinableTaskFactory.RunAsync(() => host.LaunchClaudeAsync()).FileAndForget("claudecodevs/launch");
    }

    private void OnShowPanel(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = FindToolWindow(typeof(ClaudeToolWindow), 0, create: true);
        if (window?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class PackageGuids
{
    public const string PackageString = "d9032717-8a83-4ab5-9b63-2fe9d9a78481";
    // Must match guidClaudeCmdSet in VSCommandTable.vsct.
    public static readonly Guid CommandSet = new Guid("9495bbbb-756d-4dc4-807d-1408d50e7d33");
}

internal static class PackageIds
{
    // Must match the IDSymbol values in VSCommandTable.vsct.
    public const int LaunchClaude = 0x0100;
    public const int ShowPanel = 0x0101;
}
