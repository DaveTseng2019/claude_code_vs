using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// Central, process-wide selection state. The MEF <see cref="TextViewListener"/> feeds it whenever a
/// caret/selection moves or an editor gains focus; the selection tools read from it and the debounced
/// <c>selection_changed</c> notification is pushed from here. Kept static because the MEF view
/// listener is composed by VS's editor - separate from our package - and both need the same instance.
/// </summary>
internal static class SelectionService
{
    private static readonly object Gate = new();
    private static SelectionInfo _current = SelectionInfo.Empty;
    private static SelectionInfo? _lastNonEmpty;

    private static IdeWebSocketServer? _server;
    private static JoinableTaskFactory? _jtf;
    private static CancellationTokenSource? _debounce;

    /// <summary>Wire the broadcast sink once the bridge server is up (called from BridgeHost).</summary>
    public static void Attach(IdeWebSocketServer server, JoinableTaskFactory jtf)
    {
        _server = server;
        _jtf = jtf;
    }

    public static JToken CurrentAsJson()
    {
        lock (Gate) return _current.ToJson();
    }

    public static JToken LatestAsJson()
    {
        lock (Gate) return (_lastNonEmpty ?? _current).ToJson();
    }

    /// <summary>The live selection snapshot, for the editor context-menu commands (Explain / Add to Chat).</summary>
    public static SelectionInfo Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>
    /// Push an at_mentioned for the current selection (file + line range), or the whole file if nothing
    /// is selected - the "Add to Chat" context-menu command. Insert-not-submit, same as every other
    /// at_mentioned in this codebase (CLAUDE.md).
    /// </summary>
    public static async Task MentionCurrentAsync()
    {
        var server = _server;
        if (server is null || !server.HasConnections)
        {
            Log.Warn("Add to Chat: Claude isn't connected.");
            return;
        }

        var info = Current;
        if (info.FilePath is null)
        {
            Log.Warn("Add to Chat: no active file.");
            return;
        }

        var mentionPath = Attachments.AttachmentService.ToWorkspaceRelative(info.FilePath) ?? info.FilePath;
        var @params = new JObject { ["filePath"] = mentionPath };
        if (!info.IsEmpty)
        {
            @params["lineStart"] = info.StartLine;
            @params["lineEnd"] = info.EndLineInclusive;
        }

        try
        {
            await server.BroadcastNotificationAsync("at_mentioned", @params, CancellationToken.None);
            var range = info.IsEmpty ? "" : $" (lines {info.StartLine + 1}-{info.EndLineInclusive + 1})";
            Log.Info($"Add to Chat: mentioned '{System.IO.Path.GetFileName(info.FilePath)}'{range}.");
        }
        catch (Exception e)
        {
            Log.Warn($"Add to Chat failed: {e.Message}");
        }
    }

    /// <summary>Record a fresh selection from a focused view and (debounced) push selection_changed.</summary>
    public static void Update(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var info = SelectionInfo.FromView(view);
        if (info is null) return;

        lock (Gate)
        {
            _current = info;
            if (!info.IsEmpty) _lastNonEmpty = info;
        }

        DebouncedBroadcast(info);
    }

    private static void DebouncedBroadcast(SelectionInfo info)
    {
        var server = _server;
        var jtf = _jtf;
        if (server is null || jtf is null || !server.HasConnections) return;

        // Coalesce rapid caret moves (CLAUDE.md gotcha: debounce or you flood the socket).
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;

        jtf.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
                await server.BroadcastNotificationAsync("selection_changed", info.ToJson(), cts.Token);
            }
            catch (OperationCanceledException) { /* superseded by a newer selection */ }
            catch (Exception e) { Log.Warn($"selection_changed push failed: {e.Message}"); }
        }).FileAndForget("claudecodevs/selectionChanged");
    }
}

/// <summary>Immutable snapshot of one selection, in LSP-shaped (0-based) coordinates.</summary>
internal sealed class SelectionInfo
{
    public static readonly SelectionInfo Empty = new("", null, 0, 0, 0, 0);

    public string Text { get; }
    public string? FilePath { get; }
    public int StartLine { get; }
    public int StartChar { get; }
    public int EndLine { get; }
    public int EndChar { get; }

    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// The last line the selection actually covers. <see cref="EndLine"/> is LSP-shaped (exclusive), so
    /// selecting whole lines parks the end at column 0 of the NEXT line - reporting that one verbatim
    /// would claim one line too many. Only for human-facing ranges and at_mentioned; the
    /// <c>selection_changed</c> / getCurrentSelection JSON keeps the exclusive LSP coordinates.
    /// </summary>
    public int EndLineInclusive => EndChar == 0 && EndLine > StartLine ? EndLine - 1 : EndLine;

    public SelectionInfo(string text, string? filePath, int startLine, int startChar, int endLine, int endChar)
    {
        Text = text;
        FilePath = filePath;
        StartLine = startLine;
        StartChar = startChar;
        EndLine = endLine;
        EndChar = endChar;
    }

    public static SelectionInfo? FromView(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (view is null || view.IsClosed) return null;

        var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
        var snapshot = span.Snapshot;

        var startLine = snapshot.GetLineFromPosition(span.Start.Position);
        var endLine = snapshot.GetLineFromPosition(span.End.Position);

        string? path = null;
        if (view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            path = doc?.FilePath;

        return new SelectionInfo(
            span.GetText(),
            path,
            startLine.LineNumber,
            span.Start.Position - startLine.Start.Position,
            endLine.LineNumber,
            span.End.Position - endLine.Start.Position);
    }

    public JToken ToJson()
    {
        return new JObject
        {
            ["success"] = true,
            ["text"] = Text,
            ["filePath"] = FilePath,
            ["fileUrl"] = FilePath is not null ? new Uri(FilePath).AbsoluteUri : null,
            ["selection"] = new JObject
            {
                ["start"] = new JObject { ["line"] = StartLine, ["character"] = StartChar },
                ["end"] = new JObject { ["line"] = EndLine, ["character"] = EndChar },
            },
        };
    }
}
