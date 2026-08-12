using System;
using System.Runtime.InteropServices;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// In-IDE notifications for the two "come back to Claude" moments: the turn finished (the Stop hook's
/// /usage POST doubles as the signal) and Claude needs the user (the Notification hook's /notify - a
/// terminal permission prompt, or it went idle waiting for input). Renders ONE InfoBar on the VS main
/// window (a new notification supersedes the previous one; "finished" auto-dismisses, "needs input"
/// stays until acted on) and flashes the VS taskbar button when VS isn't the foreground app. Gated by
/// <see cref="BridgeStatus.NotifyEnabled"/> (panel toggle). Entry points are called from background
/// HTTP-handler threads; everything VS-facing marshals to the UI thread (convention #1).
/// </summary>
internal sealed class Notifier : IVsInfoBarUIEvents
{
    private const int MaxMessageChars = 300;
    private static readonly TimeSpan FinishedAutoDismiss = TimeSpan.FromSeconds(15);

    private static readonly object Gate = new();
    private static Notifier? _current; // the one visible notification (superseded on the next)

    private readonly IVsInfoBarUIElement _element;
    private uint _cookie;

    private Notifier(IVsInfoBarUIElement element) => _element = element;

    /// <summary>Claude finished a turn (Stop hook). Auto-dismisses - it's a heads-up, not a task.</summary>
    public static void TurnEnded()
        => Post(Strings.NotifyTurnEnded, autoDismiss: true, feedWorthy: false);

    /// <summary>An actionable hint (e.g. the CLI-side lever for a panel toggle). Auto-dismisses; logged to the feed.</summary>
    public static void Tip(string message)
        => Post(message, autoDismiss: true, feedWorthy: true);

    /// <summary>
    /// A sticky announcement with one hyperlink (the marquee update notice). Returns true only when
    /// the InfoBar actually rendered - the caller latches on success, so a too-early call retries on
    /// the next start rather than burning its one showing. Respects the Notify toggle.
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> AnnounceAsync(string message, string linkText, string url)
    {
        if (!BridgeStatus.NotifyEnabled) return false;
        Log.Info($"notify: {message}");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            FlashTaskbarIfBackground();
            return Show(message, autoDismiss: false, link: (linkText, url));
        }
        catch (Exception e)
        {
            Log.Warn($"announce failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Claude needs the user (Notification hook): permission prompt or idle. Stays up until dismissed.</summary>
    public static void NeedsAttention(string message)
    {
        var msg = (message ?? "").Trim();
        if (msg.Length == 0) msg = Strings.NotifyNeedsInput;
        if (msg.Length > MaxMessageChars) msg = msg.Substring(0, MaxMessageChars) + "…";
        Post(msg, autoDismiss: false, feedWorthy: true);
    }

    private static void Post(string message, bool autoDismiss, bool feedWorthy)
    {
        // Needs-input is rare and actionable -> panel feed (Info); turn-end fires every turn -> Output
        // pane only (Event), so the feed stays readable.
        if (feedWorthy) Log.Info($"notify: {message}");
        else Log.Event($"notify: {message}");
        if (!BridgeStatus.NotifyEnabled) return;

        // Intentional fire-and-forget (FileAndForget reports faults to the activity log).
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(() => ShowOnMainThreadAsync(message, autoDismiss))
            .FileAndForget("claudecodevs/notify");
#pragma warning restore VSSDK007
    }

    private static async Task ShowOnMainThreadAsync(string message, bool autoDismiss)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            FlashTaskbarIfBackground();
            Show(message, autoDismiss);
        }
        catch (Exception e) { Log.Warn($"notify failed: {e.Message}"); }
    }

    private static bool Show(string message, bool autoDismiss, (string text, string url)? link = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var shell = (IVsShell?)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
        var factory = (IVsInfoBarUIFactory?)ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory));
        if (shell is null || factory is null)
        {
            Log.Warn("notify: InfoBar services unavailable");
            return false;
        }
        if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObj))
            || hostObj is not IVsInfoBarHost host)
        {
            Log.Warn("notify: main-window InfoBar host unavailable");
            return false;
        }

        CloseCurrent(); // one notification at a time - the newest supersedes

        // The hyperlink's ActionContext carries its URL; OnActionItemClicked opens it.
        var model = link is { } l
            ? new InfoBarModel($"Claude Code:  {message}",
                new[] { new InfoBarHyperlink(l.text, l.url) },
                KnownMonikers.StatusInformation, isCloseButtonVisible: true)
            : new InfoBarModel($"Claude Code:  {message}", KnownMonikers.StatusInformation, isCloseButtonVisible: true);
        var element = factory.CreateInfoBar(model);
        var bar = new Notifier(element);
        element.Advise(bar, out bar._cookie);
        host.AddInfoBar(element);
        lock (Gate) _current = bar;

        if (!autoDismiss) return true;
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await Task.Delay(FinishedAutoDismiss);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bool stillCurrent;
            lock (Gate) stillCurrent = _current == bar;
            if (stillCurrent)
                try { element.Close(); } catch { /* window may be tearing down */ }
        }).FileAndForget("claudecodevs/notifyDismiss");
#pragma warning restore VSSDK007
        return true;
    }

    private static void CloseCurrent()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Notifier? cur;
        lock (Gate) { cur = _current; _current = null; }
        if (cur != null)
            try { cur._element.Close(); } catch { /* already gone */ } // Close -> OnClosed -> Unadvise
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        // The only action items we create are hyperlinks whose ActionContext is their URL
        // (the marquee notice's "Release notes" link).
        if (actionItem?.ActionContext is string url
            && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Warn($"open link failed: {e.Message}"); }
        }
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS raises this on the UI thread
        try { _element.Unadvise(_cookie); } catch { }
        lock (Gate) { if (_current == this) _current = null; }
    }

    /// <summary>
    /// Flash the VS taskbar button when another app is foreground - the native Windows "needs attention"
    /// affordance. Bounded (a few blinks, then the button stays highlighted); NOT flash-until-focused,
    /// which would nag through a whole terminal conversation. No-op when this VS instance is foreground.
    /// </summary>
    private static void FlashTaskbarIfBackground()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                    return; // user is already looking at this VS instance
            }
            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL, // caption + taskbar button
                uCount = 4,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { /* purely cosmetic - never let it break the notification */ }
    }

    private const uint FLASHW_ALL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
