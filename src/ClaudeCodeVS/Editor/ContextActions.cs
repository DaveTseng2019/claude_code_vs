using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// The editor context-menu ("Claude Code" flyout) action bodies. Every action follows one shape:
/// stage a self-describing instruction .txt (insert-not-submit - it lands in the CLI composer for
/// the user to send) and @-mention the code it talks about, so the model gets both the ask and the
/// ground truth. Instruction text goes to the MODEL and stays English by design (CLAUDE.md #6);
/// feed lines are diagnostics and stay English too. If no session is connected, the action launches
/// one - staged attachments deliver on connect (the official VS Code extension's behavior).
/// </summary>
internal static class ContextActions
{
    // ---- Add to Chat: mention + focus (Alt+K and the menu entry both land here) --------------------

    public static async Task AddToChatAsync()
    {
        await SelectionService.MentionCurrentAsync();
        FocusClaude(launchedJustNow: false);
    }

    // ---- Explain: selection (embedded) or whole file (mentioned) ----------------------------------

    public static async Task ExplainAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Explain: no active file.");
            return;
        }
        bool launched = EnsureSessionLaunching("Explain");
        var fileName = System.IO.Path.GetFileName(sel.FilePath);

        if (!sel.IsEmpty)
        {
            int a = sel.StartLine + 1, b = sel.EndLineInclusive + 1;
            var header = $"Explain this code from {fileName} (lines {a}-{b}):";
            await Attachments.AttachmentService.StageTextAsync(
                header + "\n\n" + sel.Text, $"explain-{fileName}-L{a}-{b}");
            FocusClaude(launched);
            return;
        }

        // No selection: whole-file explain (parity with the official extension's "Explain File").
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;
        await Attachments.AttachmentService.StageTextAsync(
            $"Explain the file {rel} (mentioned alongside this note): its purpose, structure, and how the pieces fit together.",
            $"explain-{fileName}");
        await SelectionService.MentionAsync(sel.FilePath, null, null, "Explain");
        FocusClaude(launched);
    }

    // ---- Fix Errors: selection/file + its Error List diagnostics ----------------------------------

    public static async Task FixErrorsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // Error List read is UI-thread
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Fix Errors: no active file.");
            return;
        }
        var fileName = System.IO.Path.GetFileName(sel.FilePath);

        var all = ErrorListReader.Read();
        var forFile = all.FirstOrDefault(kv => string.Equals(kv.Key, sel.FilePath, StringComparison.OrdinalIgnoreCase)).Value;
        if (forFile is null || forFile.Count == 0)
        {
            Log.Info($"Fix Errors: the Error List has no entries for {fileName}. Build the project if you expected some.");
            return;
        }

        // Prefer the selection's line range; fall back to the whole file when the range has none.
        var diags = forFile.Cast<Newtonsoft.Json.Linq.JObject>().ToList();
        bool scopedToSelection = false;
        if (!sel.IsEmpty)
        {
            var inRange = diags.Where(d =>
            {
                int line = (int?)d["range"]?["start"]?["line"] ?? -1;
                return line >= sel.StartLine && line <= sel.EndLineInclusive;
            }).ToList();
            if (inRange.Count > 0) { diags = inRange; scopedToSelection = true; }
        }

        bool launched = EnsureSessionLaunching("Fix Errors");
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Fix these compiler diagnostics in {rel}{(scopedToSelection ? $" (selection, lines {sel.StartLine + 1}-{sel.EndLineInclusive + 1})" : "")}:");
        const int cap = 30;
        foreach (var d in diags.Take(cap))
        {
            int line = (int?)d["range"]?["start"]?["line"] ?? 0;
            string sev = (int?)d["severity"] switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" };
            sb.AppendLine($"  {sev} (line {line + 1}): {(string?)d["message"]}");
        }
        if (diags.Count > cap) sb.AppendLine($"  …and {diags.Count - cap} more.");
        sb.AppendLine();
        sb.AppendLine("The file is mentioned alongside this note. Make the smallest correct fix, then verify with getDiagnostics that these are gone.");

        await Attachments.AttachmentService.StageTextAsync(sb.ToString(), $"fix-{fileName}");
        await SelectionService.MentionAsync(sel.FilePath,
            scopedToSelection ? (int?)sel.StartLine : null,
            scopedToSelection ? (int?)sel.EndLineInclusive : null, "Fix Errors");
        FocusClaude(launched);
    }

    // ---- Generate Documentation / Add Comments: the function at the caret -------------------------

    public static Task GenerateDocsAsync() => FunctionActionAsync(
        action: "Generate Documentation",
        stem: "docs",
        headerForFunction: (name, rel, a, b) =>
            $"Add documentation comments for `{name}` in {rel} (lines {a}-{b}, mentioned alongside this note). " +
            "Match the file's existing documentation style (for C#: /// XML docs - <summary>, plus <param>/<returns> where they add real information, not restatements). " +
            "Edit the file; do not change the code itself.",
        headerForSelection: (rel, a, b) =>
            $"Add documentation comments for the code in {rel} (lines {a}-{b}, mentioned alongside this note). " +
            "Match the file's existing documentation style. Edit the file; do not change the code itself.");

    public static Task AddCommentsAsync() => FunctionActionAsync(
        action: "Add Comments",
        stem: "comments",
        headerForFunction: (name, rel, a, b) =>
            $"Add explanatory comments inside `{name}` in {rel} (lines {a}-{b}, mentioned alongside this note) - " +
            "but ONLY where the code is not self-explanatory: magic numbers, non-obvious constants, invariants, and why-this-way choices. " +
            "Match the file's comment style, never narrate obvious lines, and do not change the code itself.",
        headerForSelection: (rel, a, b) =>
            $"Add explanatory comments to the code in {rel} (lines {a}-{b}, mentioned alongside this note) - " +
            "but ONLY where it is not self-explanatory: magic numbers, non-obvious constants, invariants, and why-this-way choices. " +
            "Match the file's comment style, never narrate obvious lines, and do not change the code itself.");

    private static async Task FunctionActionAsync(
        string action, string stem,
        Func<string, string, int, int, string> headerForFunction,
        Func<string, int, int, string> headerForSelection)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn($"{action}: no active file.");
            return;
        }
        var fileName = System.IO.Path.GetFileName(sel.FilePath);
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;

        // Roslyn resolves the enclosing function from the caret; hops off the UI thread itself.
        var fn = await CodeModel.RoslynReader.GetEnclosingFunctionAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);

        if (fn != null)
        {
            bool launched = EnsureSessionLaunching(action);
            int a = fn.StartLine + 1, b = fn.EndLine + 1;
            await Attachments.AttachmentService.StageTextAsync(
                headerForFunction(fn.DisplayName, rel, a, b), $"{stem}-{fileName}-L{a}-{b}");
            await SelectionService.MentionAsync(sel.FilePath, fn.StartLine, fn.EndLine, action);
            FocusClaude(launched);
            return;
        }

        if (!sel.IsEmpty)
        {
            // No Roslyn (loose file, C++, no project) but the user told us the range themselves.
            bool launched = EnsureSessionLaunching(action);
            int a = sel.StartLine + 1, b = sel.EndLineInclusive + 1;
            await Attachments.AttachmentService.StageTextAsync(
                headerForSelection(rel, a, b), $"{stem}-{fileName}-L{a}-{b}");
            await SelectionService.MentionAsync(sel.FilePath, sel.StartLine, sel.EndLineInclusive, action);
            FocusClaude(launched);
            return;
        }

        Log.Info($"{action}: place the caret inside a function (needs a loaded C#/VB project), or select the code first.");
    }

    // ---- Fix This Test: run/debug/catch the test at the caret -------------------------------------

    public static async Task FixTestAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Fix This Test: no active file.");
            return;
        }

        var fn = await CodeModel.RoslynReader.GetEnclosingFunctionAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);
        if (fn is null)
        {
            Log.Info("Fix This Test: place the caret inside a test method (needs a loaded C#/VB project).");
            return;
        }
        if (!fn.IsTest)
        {
            Log.Info($"Fix This Test: '{fn.DisplayName}' carries no test attribute ([Fact]/[Theory]/[Test]/[TestMethod]).");
            return;
        }

        bool launched = EnsureSessionLaunching("Fix This Test");
        var header =
            $"Run the test {fn.FullyQualifiedName} with the vs-debug tool vs_run_test. " +
            "If it fails, investigate and fix whichever is wrong - the code or the test: vs_debug_test stops at the throw with the exception and locals in view, " +
            "and vs_catch_flaky catches an intermittent failure red-handed (both need the 'Allow Claude to drive debugger' toggle in the Claude Code panel). " +
            "Re-run vs_run_test afterward to verify it passes. The test's source is mentioned alongside this note.";
        await Attachments.AttachmentService.StageTextAsync(header, $"fixtest-{fn.FullyQualifiedName.Split('.').Last()}");
        await SelectionService.MentionAsync(sel.FilePath, fn.StartLine, fn.EndLine, "Fix This Test");
        FocusClaude(launched);
    }

    // ---- shared ------------------------------------------------------------------------------------

    /// <summary>
    /// Official-extension parity: an action with no session running launches one instead of failing.
    /// Fire-and-forget - staged .txt attachments deliver on connect via the tray's queued mentions;
    /// range mentions pushed before the connect may need the chip's click-to-re-mention.
    /// Returns true when a launch was fired (callers then skip the explicit refocus - the fresh
    /// terminal takes focus itself).
    /// </summary>
    private static bool EnsureSessionLaunching(string action)
    {
        if (Ui.BridgeStatus.Connected) return false;
        var launch = Ui.BridgeStatus.LaunchAction;
        if (launch is null) return false;
        Log.Info($"{action}: no Claude session - launching one. Staged items deliver when it connects.");
        _ = launch();
        return true;
    }

    /// <summary>
    /// Focus the claude session's input so Enter sends what the action just pushed (the #33
    /// focus-trap, solved). Skipped when the action just launched a session - the fresh terminal
    /// takes focus on its own.
    /// </summary>
    private static void FocusClaude(bool launchedJustNow)
    {
        if (launchedJustNow) return;
        var focus = Ui.BridgeStatus.FocusClaudeAction;
        if (focus != null) _ = focus();
    }
}
