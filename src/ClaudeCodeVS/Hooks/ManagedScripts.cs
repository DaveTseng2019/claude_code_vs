using System;
using System.IO;
using ClaudeCodeVs.Protocol;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Ownership-aware script writer (issue #17: "doing local fixes is not possible as the extension
/// clobbers them when launched"). Every embedded script's FIRST LINE carries the vs:auto-managed
/// marker; the installers overwrite a script only while that line is present. A user takes ownership
/// by deleting the line - from then on their copy is never touched (logged so it isn't silent).
/// Legacy copies written before the marker existed are recognized by the old "auto-installed by the
/// Claude Code VS extension" header and treated as managed, so they still receive updates.
/// </summary>
internal static class ManagedScripts
{
    public const string Marker = "vs:auto-managed";
    private const string LegacyHeader = "auto-installed by the Claude Code VS extension";

    public static void WriteIfManaged(string path, string content)
    {
        try
        {
            if (File.Exists(path))
            {
                string firstLine;
                using (var r = new StreamReader(path))
                    firstLine = r.ReadLine() ?? "";

                bool managed = firstLine.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0
                            || firstLine.IndexOf(LegacyHeader, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!managed)
                {
                    Log.Info($"scripts: '{Path.GetFileName(path)}' is user-owned (no '{Marker}' marker line) - leaving it alone");
                    return;
                }
                if (string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                    return; // already current - skip the write
            }
            File.WriteAllText(path, content);
        }
        catch (Exception e)
        {
            Log.Warn($"scripts: writing '{Path.GetFileName(path)}' failed: {e.Message}");
        }
    }
}
