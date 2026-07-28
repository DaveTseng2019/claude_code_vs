using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Attachments;

/// <summary>One staged attachment: what's on disk and what we @-mention to the CLI.</summary>
internal sealed class AttachmentItem
{
    public string FullPath = "";    // absolute path on disk (the original, or our staged copy)
    public string MentionPath = ""; // what at_mentioned carries: workspace-relative forward-slash, else absolute
    public string FileName = "";
    public bool IsImage;
    public bool WasCopied;          // true = the file is OURS (a staged copy/paste) - safe to delete on remove
    public bool Sent;               // at_mentioned delivered at least once while the CLI was connected
    public long? EstTokens;         // what reading this will roughly cost: (w*h)/750 for images, bytes/4 for text; null = unknown (PDF, undecodable)
    public bool NeedsTool;          // not a format Read parses (xlsx/mp4/zip/...) - Claude gets the path and reaches for a script/tool
}

/// <summary>
/// The attachment tray behind the panel's drop/paste target: stages files (screenshots become PNGs,
/// out-of-workspace files are copied into the workspace) and pushes each to the CLI's composer as an
/// <c>at_mentioned</c> notification - the message behind the official plugins' "insert @File reference"
/// shortcut (spike-verified 2026-07-16: an at-mentioned image path is a REAL image attachment; the
/// model sees pixels; relative and absolute paths both resolve; insert-not-submit).
///
/// Staging lives in &lt;workspace&gt;\.claude\attachments\ with a self-ignoring .gitignore: inside the
/// workspace so the CLI's Read/Grep need no out-of-project permission, outside git so nothing lands in
/// the repo. Files already under the workspace are referenced in place (never copied, never deleted).
/// Static + Attach(server), mirroring <see cref="ClaudeCodeVs.Editor.SelectionService"/>: the WPF panel
/// and BridgeHost are composed separately and need the same instance.
/// </summary>
internal static class AttachmentService
{
    private const int MaxItems = 20;
    private const long MaxImageBytes = 5 * 1024 * 1024;  // the vision API's per-image cap (over it: attach anyway, note it needs a downscale)
    private const long MaxCopyBytes = 50 * 1024 * 1024;  // over it: don't duplicate into the workspace, @-mention in place instead
    private static readonly TimeSpan PruneAge = TimeSpan.FromDays(7);

    // What Claude's Read parses DIRECTLY: PNG/JPEG/GIF/WebP (no BMP - we transcode those) + PDF + text.
    // Everything else (xlsx, mp4, zip, ...) still stages and mentions - one uniform framework - it's just
    // labeled: the model gets the path and reaches for a script/tool (PowerShell, ffmpeg, ...) instead of Read.
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    private static readonly object Gate = new();
    private static readonly List<AttachmentItem> Items = new();
    private static IdeWebSocketServer? _server;

    /// <summary>Fired when the staged list changes (panel re-renders its chips; marshal to the dispatcher).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<AttachmentItem> Snapshot()
    {
        lock (Gate) return Items.ToArray();
    }

    /// <summary>Wire the tray to the live WS server. Unsent items flush when the CLI (re)connects.</summary>
    public static void Attach(IdeWebSocketServer server)
    {
        _server = server;
        server.ConnectionChanged += connected => { if (connected) _ = FlushUnsentAsync(); };
        _ = Task.Run(() => PruneStale());
    }

    /// <summary>Stage dropped/pasted file paths and @-mention each. Any thread; IO stays off the UI thread.</summary>
    public static async Task StageFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try { await StageOneFileAsync(path); }
            catch (Exception e) { Log.Warn($"attach: '{Path.GetFileName(path)}' failed: {e.Message}"); }
            await Task.Delay(25); // pace multi-file at_mentioned sends (claudecode.nvim's proven spacing)
        }
    }

    /// <summary>Stage an already-PNG-encoded clipboard/drop image (the panel encodes on its own thread).</summary>
    public static async Task StageImageBytesAsync(byte[] png)
    {
        try
        {
            if (png.LongLength > MaxImageBytes)
            {
                Log.Warn($"attach: pasted image is {png.LongLength / (1024 * 1024)} MB - over the 5 MB the vision API accepts.");
                return;
            }
            var dir = EnsureStagingDir();
            var dest = UniquePath(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(dest, png);
            await AddAndSendAsync(dest, isImage: true, wasCopied: true, EstimatePngTokens(png), needsTool: false);
        }
        catch (Exception e)
        {
            Log.Warn($"attach: pasting image failed: {e.Message}");
        }
    }

    /// <summary>Re-send one chip's at_mentioned (e.g. the first send raced a busy CLI turn).</summary>
    public static Task ResendAsync(AttachmentItem item) => SendAsync(item, announce: true);

    /// <summary>
    /// Stage a capture the MODEL initiated (the vs_capture_* tools): saved into staging + a visible
    /// panel chip + a feed line, but deliberately NOT at_mentioned - the tool result carries the path
    /// (the model Reads it), so a composer chip would double-deliver. Marked Sent so the on-connect
    /// flush never mentions it either; the user can still click the chip to @-mention it themselves.
    /// </summary>
    public static AttachmentItem StageCapturePng(byte[] png, string baseName)
    {
        var dir = EnsureStagingDir();
        var dest = UniquePath(dir, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(dest, png);
        var item = new AttachmentItem
        {
            FullPath = dest,
            MentionPath = ToWorkspaceRelative(dest) ?? dest,
            FileName = Path.GetFileName(dest),
            IsImage = true,
            WasCopied = true,
            EstTokens = EstimatePngTokens(png),
            Sent = true, // never auto-mentioned: the capture tool's result carries the path
        };
        lock (Gate)
        {
            Items.Add(item);
            while (Items.Count > MaxItems) Items.RemoveAt(0);
        }
        Changed?.Invoke();
        return item;
    }

    /// <summary>Remove one chip; staged COPIES are deleted, in-place originals are never touched.</summary>
    public static void Remove(AttachmentItem item)
    {
        lock (Gate) Items.Remove(item);
        if (item.WasCopied)
            try { File.Delete(item.FullPath); } catch { /* best-effort - prune will catch it */ }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        AttachmentItem[] removed;
        lock (Gate)
        {
            removed = Items.ToArray();
            Items.Clear();
        }
        foreach (var item in removed.Where(i => i.WasCopied))
            try { File.Delete(item.FullPath); } catch { /* best-effort */ }
        Changed?.Invoke();
    }

    // -------------------------------------------------------------------------------------------

    private static async Task StageOneFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"attach: '{path}' doesn't exist (folders aren't supported - drop files).");
            return;
        }

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        var size = new FileInfo(path).Length;

        // BMP is the one format we can FIX rather than label: Read doesn't parse it, but WPF does,
        // so a vision-ready PNG copy goes into staging instead.
        if (ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) && await TryStageBmpAsPngAsync(path))
            return;

        bool isImage = ImageExt.Contains(ext);
        bool isPdf = ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        bool isText = !isImage && !isPdf && !LooksBinary(path);
        bool needsTool = !isImage && !isPdf && !isText; // xlsx/mp4/zip/... - path still helps, Read doesn't

        string full = Path.GetFullPath(path);
        bool copied = false;

        // In-workspace files are referenced in place; others are copied into staging so the CLI reads
        // them without an out-of-project permission prompt - unless they're huge, where the graceful
        // degradation is an in-place absolute mention (spike-verified to resolve) instead of a 50 MB copy.
        if (ToWorkspaceRelative(full) is null)
        {
            if (size > MaxCopyBytes)
            {
                Log.Info($"attach: '{name}' is {size / (1024 * 1024)} MB - too big to copy into the workspace; @-mentioning it in place (Claude may ask permission to read outside the workspace).");
            }
            else
            {
                var dest = UniquePath(EnsureStagingDir(), name);
                File.Copy(full, dest);
                full = dest;
                copied = true;
            }
        }

        long? est = isImage && size <= MaxImageBytes ? EstimateImageTokens(full)
                  : isText ? Math.Max(1, size / 4)
                  : null;
        await AddAndSendAsync(full, isImage, copied, est, needsTool);

        if (needsTool)
            Log.Info($"attach: '{name}' isn't a format Claude reads directly - it has the path and will use a script/tool (PowerShell, ffmpeg, ...) on it.");
        else if (isImage && size > MaxImageBytes)
            Log.Info($"attach: '{name}' is {size / (1024 * 1024)} MB - over the 5 MB vision cap, so Claude will need to downscale it before viewing.");
    }

    /// <summary>Transcode a dropped .bmp to a PNG in staging (vision-ready). False = fall through to generic handling.</summary>
    private static async Task<bool> TryStageBmpAsPngAsync(string path)
    {
        try
        {
            byte[] png;
            using (var fs = File.OpenRead(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                png = ms.ToArray();
            }
            var dest = UniquePath(EnsureStagingDir(), Path.GetFileNameWithoutExtension(path) + ".png");
            File.WriteAllBytes(dest, png);
            Log.Info($"attach: transcoded '{Path.GetFileName(path)}' to PNG (Claude's vision doesn't take BMP).");
            await AddAndSendAsync(dest, isImage: true, wasCopied: true, EstimatePngTokens(png), needsTool: false);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"attach: BMP transcode failed ({e.Message}) - attaching the original; Claude will need a tool to view it.");
            return false;
        }
    }

    private static async Task AddAndSendAsync(string fullPath, bool isImage, bool wasCopied, long? estTokens, bool needsTool)
    {
        var item = new AttachmentItem
        {
            FullPath = fullPath,
            MentionPath = ToWorkspaceRelative(fullPath) ?? fullPath, // absolute also works (spike-verified)
            FileName = Path.GetFileName(fullPath),
            IsImage = isImage,
            WasCopied = wasCopied,
            EstTokens = estTokens,
            NeedsTool = needsTool,
        };
        lock (Gate)
        {
            Items.Add(item);
            while (Items.Count > MaxItems) Items.RemoveAt(0); // bounded like every other surface
        }
        Changed?.Invoke();
        await SendAsync(item, announce: true);
    }

    private static async Task SendAsync(AttachmentItem item, bool announce)
    {
        var server = _server;
        if (server is null || !server.HasConnections)
        {
            if (announce)
                Log.Info($"attach: staged '{item.FileName}'{TokSuffix(item.EstTokens)} - will @-mention it when Claude connects.");
            Changed?.Invoke();
            return;
        }
        try
        {
            var @params = new JObject { ["filePath"] = item.MentionPath };
            await server.BroadcastNotificationAsync("at_mentioned", @params, CancellationToken.None);
            item.Sent = true;
            if (announce)
                Log.Info($"attach: @-mentioned '{item.MentionPath}'{TokSuffix(item.EstTokens)} in the Claude composer.");
        }
        catch (Exception e)
        {
            Log.Warn($"attach: at_mentioned for '{item.FileName}' failed: {e.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>On CLI (re)connect: give the MCP handshake a beat to settle, then send whatever's pending.</summary>
    private static async Task FlushUnsentAsync()
    {
        await Task.Delay(200); // claudecode.nvim's post-handshake settle delay
        foreach (var item in Snapshot().Where(i => !i.Sent))
        {
            await SendAsync(item, announce: true);
            await Task.Delay(25);
        }
    }

    // -------------------------------------------------------------------------------------------

    // ---- Token estimates: what a Read of this attachment roughly adds to the next prompt ----
    // Exact numbers don't exist anywhere (the API reports only aggregate input_tokens), so these are
    // honest arithmetic on files we hold: images use Anthropic's documented formula, tokens ≈ (w×h)/750
    // after the API downscales the long edge to ≤1568 px (caps one image around ~1.6k tokens); text is
    // ~4 bytes/token. PDFs and undecodable images get NO estimate rather than a made-up one.

    private const double ImageTokenDivisor = 750.0;
    private const double MaxImageEdge = 1568.0;
    private const long MaxImageTokens = 1600;

    private static long? EstimateImageTokens(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // DelayCreation + CacheOption.None = header-only parse; no full decode, safe off the UI thread.
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return ImageTokensFromDims(frame.PixelWidth, frame.PixelHeight);
        }
        catch { return null; } // missing codec (some WebP) or corrupt file - skip the estimate
    }

    /// <summary>Dimensions straight from the PNG IHDR (we produced these bytes; no decoder needed).</summary>
    private static long? EstimatePngTokens(byte[] png)
    {
        if (png.Length < 24) return null;
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return w > 0 && h > 0 ? ImageTokensFromDims(w, h) : null;
    }

    private static long ImageTokensFromDims(int w, int h)
    {
        double scale = Math.Min(1.0, MaxImageEdge / Math.Max(w, h));
        long tokens = (long)Math.Ceiling(w * scale * h * scale / ImageTokenDivisor);
        return Math.Min(tokens, MaxImageTokens);
    }

    /// <summary>" (≈1.4k tok)" for log lines, empty when there's no estimate.</summary>
    private static string TokSuffix(long? est)
        => est is long t ? $" (≈{(t >= 1000 ? (t / 1000.0).ToString("0.0") + "k" : t.ToString())} tok)" : "";

    /// <summary>Workspace-relative forward-slash form, or null when the path is outside the workspace.</summary>
    private static string? ToWorkspaceRelative(string fullPath)
    {
        var ws = Ui.BridgeStatus.Workspace;
        if (string.IsNullOrEmpty(ws)) return null;
        var root = ws!.TrimEnd('\\', '/');
        if (fullPath.Length <= root.Length + 1 ||
            !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            (fullPath[root.Length] != '\\' && fullPath[root.Length] != '/'))
            return null;
        return fullPath.Substring(root.Length + 1).Replace('\\', '/');
    }

    /// <summary>
    /// The staging folder: &lt;workspace&gt;\.claude\attachments (with a '*' .gitignore so neither the
    /// attachments nor the ignore file ever land in the repo), or a temp fallback with no workspace.
    /// </summary>
    private static string EnsureStagingDir()
    {
        var ws = Ui.BridgeStatus.Workspace;
        var dir = string.IsNullOrEmpty(ws)
            ? Path.Combine(Path.GetTempPath(), "claude-codevs-attach")
            : Path.Combine(ws!, ".claude", "attachments");
        Directory.CreateDirectory(dir);
        var gitignore = Path.Combine(dir, ".gitignore");
        if (!File.Exists(gitignore))
            try { File.WriteAllText(gitignore, "*\n"); } catch { /* cosmetic */ }
        return dir;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(dir, fileName);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{name}-{n}{ext}");
        return candidate;
    }

    private static bool LooksBinary(string path)
    {
        try
        {
            var buffer = new byte[8000];
            using var fs = File.OpenRead(path);
            int read = fs.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch
        {
            return true; // unreadable -> treat as unattachable
        }
    }

    /// <summary>Old pastes/copies accumulate across sessions; quietly drop staged files past the age cap.</summary>
    private static void PruneStale()
    {
        try
        {
            var ws = Ui.BridgeStatus.Workspace;
            if (string.IsNullOrEmpty(ws)) return;
            var dir = Path.Combine(ws!, ".claude", "attachments");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(file).Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(file) > PruneAge)
                        File.Delete(file);
                }
                catch { /* locked/in-use - next session */ }
            }
        }
        catch { /* best-effort housekeeping */ }
    }
}
