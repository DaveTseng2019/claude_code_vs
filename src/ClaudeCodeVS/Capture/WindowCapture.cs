using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCodeVs.Capture;

/// <summary>
/// GDI window/screen capture behind the vs_capture_* tools. PrintWindow with PW_RENDERFULLCONTENT is
/// the primary path (renders occluded windows and DWM/GPU-composed content); a screen BitBlt of the
/// window's rect is the fallback for windows that PrintWindow to black (some hardware-accelerated
/// browsers). Pure Win32 + System.Drawing - callable from any thread; the target resolution that needs
/// EnvDTE (the debuggee PID) happens in the tool on the UI thread BEFORE calling in here. devenv is
/// per-monitor-DPI aware, so all rects here are physical pixels and line up with CopyFromScreen.
/// </summary>
internal static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x2;

    // Smaller than this is never a real capture target - it's a taskbar-preview proxy (browsers create
    // a tiny titled HWND per tab, ~136x39, that stays "visible" while the real window is minimized),
    // a tooltip, or shell chrome. Filtering by size is what keeps title matching honest.
    private const int MinCaptureWidth = 200;
    private const int MinCaptureHeight = 120;

    // ---- window capture ------------------------------------------------------------------------

    public static byte[] CaptureWindow(IntPtr hwnd, out int width, out int height)
    {
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("no window handle");
        if (IsIconic(hwnd)) throw new InvalidOperationException("the window is minimized - restore it and retry");
        if (!GetWindowRect(hwnd, out RECT rc)) throw new InvalidOperationException("could not read the window rect");
        width = rc.Right - rc.Left;
        height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("the window has an empty rect");

        using (var bmp = new Bitmap(width, height))
        {
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (ok && !LooksBlank(bmp)) return ToPng(bmp);
        }

        // Fallback for windows that PrintWindow to a solid frame (GPU-rendered browsers) or that were
        // mid-restore when we shot them: bring the window forward (best-effort - Windows may refuse a
        // background process the foreground), give the restore/compose animation a beat to finish,
        // re-read the rect (a restore changes it), then copy the window's SCREEN region. Region copy
        // includes anything still overlapping the window - inherent to this path.
        SetForegroundWindow(hwnd);
        System.Threading.Thread.Sleep(350);
        if (!GetWindowRect(hwnd, out rc)) throw new InvalidOperationException("could not read the window rect");
        width = rc.Right - rc.Left;
        height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("the window has an empty rect");
        using (var bmp2 = new Bitmap(width, height))
        {
            using (var g2 = Graphics.FromImage(bmp2))
                g2.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(width, height));
            return ToPng(bmp2);
        }
    }

    // ---- screen capture ------------------------------------------------------------------------

    public static byte[] CaptureScreen(int? monitorIndex, bool all, out int width, out int height, out string what)
    {
        var monitors = GetMonitors();
        RECT rc;
        if (all && monitors.Count > 1)
        {
            rc = new RECT
            {
                Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
                Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
            };
            rc.Right = rc.Left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
            rc.Bottom = rc.Top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
            what = $"all {monitors.Count} monitors";
        }
        else if (monitorIndex is int i)
        {
            if (i < 0 || i >= monitors.Count)
                throw new InvalidOperationException($"monitor {i} not found - {monitors.Count} monitor(s), 0-based");
            rc = monitors[i].Rect;
            what = $"monitor {i}" + (monitors[i].Primary ? " (primary)" : "");
        }
        else
        {
            var primary = monitors.Find(m => m.Primary);
            rc = primary.Rect;
            what = "primary monitor";
        }

        width = rc.Right - rc.Left;
        height = rc.Bottom - rc.Top;
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(width, height));
        return ToPng(bmp);
    }

    public static int MonitorCount() => GetMonitors().Count;

    // ---- window finding ------------------------------------------------------------------------

    /// <summary>The process's main window, with an EnumWindows fallback (MainWindowHandle can be zero
    /// for apps whose "main" window isn't marked, e.g. some hosted/UWP shells). Zero = no visible window.</summary>
    public static IntPtr FindMainWindow(int pid)
    {
        try
        {
            var main = System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle;
            if (main != IntPtr.Zero) return main;
        }
        catch { /* exited or access denied - try the enumeration */ }

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, lp) =>
        {
            if (!IsEligible(hwnd, out RECT rc, out _)) return true;
            if (IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != (uint)pid) return true;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w < MinCaptureWidth || h < MinCaptureHeight) return true;
            long area = (long)w * h;
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    internal struct TitleMatch
    {
        public IntPtr Hwnd;          // zero = no capturable match
        public string Title;         // the matched window's full title
        public string MinimizedTitle; // set when the ONLY title match is a minimized window
    }

    /// <summary>
    /// Largest capturable window whose title contains <paramref name="titleSubstring"/> (case-insensitive).
    /// Largest-wins + the minimum-size floor keep taskbar-preview proxy HWNDs (a browser's per-tab ~136x39
    /// title carrier) and tooltips from masquerading as the real window. When the only match is a
    /// MINIMIZED window, that is reported distinctly so the caller can say "restore it" instead of
    /// capturing a proxy or claiming the window doesn't exist.
    /// </summary>
    public static TitleMatch FindWindowByTitle(string titleSubstring)
    {
        IntPtr best = IntPtr.Zero;
        string bestTitle = "", minimizedTitle = "";
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsEligible(hwnd, out RECT rc, out string title)) return true;
            if (title.IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (IsIconic(hwnd)) { minimizedTitle = title; return true; } // minimized rects are bogus (-32000)
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w < MinCaptureWidth || h < MinCaptureHeight) return true; // preview proxies, tooltips
            long area = (long)w * h;
            if (area > bestArea) { bestArea = area; best = hwnd; bestTitle = title; }
            return true;
        }, IntPtr.Zero);
        return new TitleMatch
        {
            Hwnd = best,
            Title = bestTitle,
            MinimizedTitle = best == IntPtr.Zero ? minimizedTitle : "",
        };
    }

    /// <summary>
    /// Capturable top-level window titles (capped) for the "no match" error. Uses the SAME eligibility
    /// as <see cref="FindWindowByTitle"/> - anything listed here is matchable, so the model can trust the
    /// list when it retries. Minimized windows appear with a "(minimized)" suffix: addressable after a
    /// restore, but not capturable as-is.
    /// </summary>
    public static List<string> ListWindowTitles(int max = 20)
    {
        var titles = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            if (titles.Count >= max) return false;
            if (!IsEligible(hwnd, out RECT rc, out string title)) return true;
            if (IsIconic(hwnd))
            {
                titles.Add(title + " (minimized)");
                return true;
            }
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w < MinCaptureWidth || h < MinCaptureHeight) return true;
            titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    /// <summary>The shared floor for "is this a real window at all": visible, not DWM-cloaked (hidden
    /// UWP/shell ghosts like "Windows Input Experience" enumerate as visible), and titled.</summary>
    private static bool IsEligible(IntPtr hwnd, out RECT rc, out string title)
    {
        rc = default;
        title = "";
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return false;
        title = GetTitle(hwnd);
        if (title.Length == 0) return false;
        return GetWindowRect(hwnd, out rc);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    // ---- internals -----------------------------------------------------------------------------

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Sample a coarse grid; a single-colour frame means PrintWindow gave us nothing real.</summary>
    private static bool LooksBlank(Bitmap bmp)
    {
        var first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        for (int x = 1; x < 10; x++)
            for (int y = 1; y < 10; y++)
                if (bmp.GetPixel(bmp.Width * x / 10, bmp.Height * y / 10) != first)
                    return false;
        return true;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal struct MonitorEntry
    {
        public RECT Rect;
        public bool Primary;
    }

    private static List<MonitorEntry> GetMonitors()
    {
        var list = new List<MonitorEntry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref info))
                list.Add(new MonitorEntry { Rect = info.rcMonitor, Primary = (info.dwFlags & 1) != 0 });
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) throw new InvalidOperationException("no monitors found");
        if (!list.Exists(m => m.Primary)) { var m0 = list[0]; m0.Primary = true; list[0] = m0; }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags; // bit 0 = MONITORINFOF_PRIMARY
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
