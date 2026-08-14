using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Attachments;
using ClaudeCodeVs.Capture;
using ClaudeCodeVs.Protocol;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Screen-capture tools on the /mcp pull surface: the model takes its own screenshots instead of asking
/// the user to paste one. Every capture is gated behind <see cref="Ui.BridgeStatus.AllowScreenCapture"/>
/// (the panel's "Allow screen capture" toggle, default OFF - what Claude can SEE of the desktop is a
/// safety decision), feed-logged, and staged as a visible attachment chip. Results return the staged
/// PNG's PATH for the model to Read - deliberately not MCP image blocks, which Claude Code counts as
/// text at ~10-20x the tokens of a native image (upstream #31208 - CLOSED as stale by a bot, never
/// fixed; verify a real fix shipped before returning image blocks).
/// </summary>
internal abstract class CaptureToolBase : IIdeTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JToken Schema { get; }

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        if (!Ui.BridgeStatus.AllowScreenCapture)
        {
            Log.Info($"{Name} -> refused (screen capture disabled)");
            return new JObject
            {
                ["error"] = "screen capture is disabled",
                ["hint"] = "Enable 'Allow screen capture' in the Claude Code panel in Visual Studio, then retry.",
            };
        }
        try
        {
            return await RunAsync(args, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"{Name} failed: {e.Message}");
            return new JObject { ["error"] = e.Message };
        }
    }

    protected abstract Task<JObject> RunAsync(JToken args, CancellationToken ct);

    /// <summary>Stage the PNG as a (non-mentioned) attachment chip and build the standard result.</summary>
    protected static JObject StageAndReport(byte[] png, string baseName, string what, int width, int height)
    {
        var item = AttachmentService.StageCapturePng(png, baseName);
        var est = item.EstTokens is long t ? $" (≈{(t >= 1000 ? (t / 1000.0).ToString("0.0") + "k" : t.ToString())} tok)" : "";
        Log.Info($"capture: {what} {width}x{height} -> '{item.MentionPath}'{est}");
        var result = new JObject
        {
            ["ok"] = true,
            ["captured"] = what,
            ["path"] = item.MentionPath,
            ["width"] = width,
            ["height"] = height,
            ["note"] = "Use the Read tool on 'path' to view the capture.",
        };
        if (item.EstTokens is long est2) result["estTokensWhenRead"] = est2;
        return result;
    }

    protected static JObject Prop(string type, string description)
        => new JObject { ["type"] = type, ["description"] = description };
}

/// <summary>
/// vs_capture_window - screenshot the debugged app's window (default), the VS window, or any window
/// addressed by a title substring (the way to capture a browser showing the site under debug).
/// </summary>
internal sealed class VsCaptureWindowTool : CaptureToolBase
{
    public override string Name => "vs_capture_window";

    public override string Description =>
        "Capture a screenshot of a window as a PNG attachment and return its file path (then use Read on "
        + "that path to view it). target 'debuggee' (default) captures the window of the process being "
        + "debugged; 'ide' captures the Visual Studio window; 'window' captures the largest visible window "
        + "whose title contains 'title' (e.g. the browser tab showing your web app - a web debuggee has no "
        + "window of its own). Requires 'Allow screen capture' enabled in the Claude Code panel.";

    public override JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["target"] = Prop("string", "'debuggee' (default) | 'ide' | 'window' (requires title)."),
            ["title"] = Prop("string", "For target 'window': case-insensitive substring of the window title to capture."),
            ["pid"] = Prop("integer", "For target 'debuggee' in a multi-process session: which debugged process id to capture."),
        },
    };

    protected override async Task<JObject> RunAsync(JToken args, CancellationToken ct)
    {
        string target = ((string?)args?["target"] ?? "debuggee").Trim().ToLowerInvariant();
        IntPtr hwnd;
        string what;

        switch (target)
        {
            case "ide":
                hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                what = "the Visual Studio window";
                break;

            case "window":
            {
                var title = (string?)args?["title"];
                if (string.IsNullOrWhiteSpace(title))
                    return new JObject { ["error"] = "target 'window' needs a 'title' substring" };
                var match = WindowCapture.FindWindowByTitle(title!);
                if (match.Hwnd == IntPtr.Zero)
                {
                    if (!string.IsNullOrEmpty(match.MinimizedTitle))
                    {
                        return new JObject
                        {
                            ["error"] = $"the window '{match.MinimizedTitle}' matches but is minimized - "
                                      + "ask the user to restore it (or restore it via a shell command), then retry",
                        };
                    }
                    return new JObject
                    {
                        ["error"] = $"no capturable window whose title contains '{title}'",
                        ["visibleWindows"] = new JArray(WindowCapture.ListWindowTitles()),
                    };
                }
                hwnd = match.Hwnd;
                what = $"window '{match.Title}'";
                break;
            }

            case "debuggee":
            {
                // EnvDTE is UI-thread-bound (convention #1): resolve the debugged PIDs there, then hop
                // back off for the GDI capture so a big encode never sits on the UI thread.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
                var pids = new System.Collections.Generic.List<int>();
                try
                {
                    if (dbg?.DebuggedProcesses != null)
                        foreach (EnvDTE.Process p in dbg.DebuggedProcesses)
                            try { pids.Add(p.ProcessID); } catch { }
                }
                catch { }
                await TaskScheduler.Default;

                if (pids.Count == 0)
                    return new JObject { ["error"] = "no debug session - start or attach one, or use target 'window' / 'ide'" };

                int? wanted = (int?)args?["pid"];
                if (wanted is int wp && !pids.Contains(wp))
                    return new JObject { ["error"] = $"pid {wp} is not being debugged", ["debuggedPids"] = new JArray(pids) };

                hwnd = IntPtr.Zero;
                int used = 0;
                foreach (var pid in wanted is int w ? new[] { w } : pids.ToArray())
                {
                    hwnd = WindowCapture.FindMainWindow(pid);
                    if (hwnd != IntPtr.Zero) { used = pid; break; }
                }
                if (hwnd == IntPtr.Zero)
                {
                    return new JObject
                    {
                        ["error"] = "the debugged process has no visible window (a web app's UI lives in the "
                                  + "browser - use target 'window' with the browser window's title)",
                        ["debuggedPids"] = new JArray(pids),
                    };
                }
                what = $"debuggee window (pid {used})";
                break;
            }

            default:
                return new JObject { ["error"] = $"unknown target '{target}' - use 'debuggee', 'ide', or 'window'" };
        }

        var png = WindowCapture.CaptureWindow(hwnd, out int width, out int height);
        return StageAndReport(png, $"capture-{target}", what, width, height);
    }
}

/// <summary>vs_capture_screen - full-screen capture: one monitor (default primary) or all of them.</summary>
internal sealed class VsCaptureScreenTool : CaptureToolBase
{
    public override string Name => "vs_capture_screen";

    public override string Description =>
        "Capture the screen as a PNG attachment and return its file path (then use Read on that path to "
        + "view it). Defaults to the primary monitor; pass 'monitor' (0-based) for a specific one or "
        + "all:true for every monitor in one image. Captures EVERYTHING visible, so prefer "
        + "vs_capture_window when a single window is enough. Requires 'Allow screen capture' enabled in "
        + "the Claude Code panel.";

    public override JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["monitor"] = Prop("integer", "0-based monitor index; omit for the primary monitor."),
            ["all"] = Prop("boolean", "true = capture the whole virtual screen (all monitors) in one image."),
        },
    };

    protected override Task<JObject> RunAsync(JToken args, CancellationToken ct)
    {
        int? monitor = (int?)args?["monitor"];
        bool all = (bool?)args?["all"] == true;
        var png = WindowCapture.CaptureScreen(monitor, all, out int width, out int height, out string what);
        return Task.FromResult(StageAndReport(png, "capture-screen", what, width, height));
    }
}
