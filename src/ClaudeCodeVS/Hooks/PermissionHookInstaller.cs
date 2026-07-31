using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Installs the extension's hooks into a workspace's .claude/ folder: writes the embedded hook scripts
/// and merges entries into .claude/settings.json (preserving everything else; idempotent). Called from
/// the Launch command. Best-effort - never throws into the launch path.
/// - PreToolUse (Edit|Write|MultiEdit) -> the single-gate permission hook (our diff is the edit gate).
/// - Stop -> the usage hook (reports the transcript path so the panel can show token/cost stats).
/// - UserPromptSubmit -> the debug-context hook (injects live break state so Claude debugs smarter).
/// - Notification -> the notify hook (raises an in-IDE notification when Claude needs the user).
/// </summary>
internal static class PermissionHookInstaller
{
    private const string PermissionScript = "vs-permission-hook.ps1";
    private const string UsageScript = "vs-usage-hook.ps1";
    private const string DebugScript = "vs-debug-context-hook.ps1";
    private const string NotifyScript = "vs-notify-hook.ps1";
    private const int PermissionTimeoutSeconds = 86400; // 24h, so the diff can wait for an unattended user
    private const int UsageTimeoutSeconds = 10;
    private const int DebugTimeoutSeconds = 10;
    private const int NotifyTimeoutSeconds = 10;

    /// <summary>
    /// The registered hook command. Test-Path-guarded (marketplace feedback): the scripts live in the
    /// workspace's .claude/ and the path is cwd-relative, so a session started in a subfolder - or a
    /// checkout that has settings.json but not the scripts - used to spam "-File does not exist" errors
    /// on every prompt. Guarded, a missing script is a silent no-op (exit 0) and the CLI's own behavior
    /// takes over; install-on-connect (BridgeHost) is the other half, materializing the scripts for any
    /// session that reaches the bridge. Stdin flows through to the script, and its exit code/stdout are
    /// preserved, so hook semantics are unchanged when the script IS present.
    /// </summary>
    private static string Command(string script) =>
        "powershell -NoProfile -ExecutionPolicy Bypass -Command "
        + $"\"if (Test-Path '.claude/{script}') {{ & '.claude/{script}' }} else {{ exit 0 }}\"";

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the hook scripts from the embedded copies, so updates ship with the extension.
            File.WriteAllText(Path.Combine(claudeDir, PermissionScript), ReadEmbeddedScript(PermissionScript));
            File.WriteAllText(Path.Combine(claudeDir, UsageScript), ReadEmbeddedScript(UsageScript));
            File.WriteAllText(Path.Combine(claudeDir, DebugScript), ReadEmbeddedScript(DebugScript));
            File.WriteAllText(Path.Combine(claudeDir, NotifyScript), ReadEmbeddedScript(NotifyScript));

            // 2) Merge hook entries into .claude/settings.json, preserving any existing content.
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            JObject root;
            if (File.Exists(settingsPath))
            {
                try { root = JObject.Parse(File.ReadAllText(settingsPath)); }
                catch (Exception e)
                {
                    Log.Warn($"hooks: couldn't parse {settingsPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            // Only assign when creating: re-assigning an already-parented JToken makes Json.NET CLONE it
            // into the parent, detaching this local reference - every later mutation then lands on the
            // orphan and silently never reaches the file (bit us adding the Notification hook to
            // workspaces with an existing settings.json).
            if (root["hooks"] is not JObject hooks)
            {
                hooks = new JObject();
                root["hooks"] = hooks;
            }

            bool addedPre = EnsureHook(hooks, "PreToolUse", "Edit|Write|MultiEdit", PermissionScript, PermissionTimeoutSeconds);
            bool addedStop = EnsureHook(hooks, "Stop", matcher: null, UsageScript, UsageTimeoutSeconds);
            bool addedDebug = EnsureHook(hooks, "UserPromptSubmit", matcher: null, DebugScript, DebugTimeoutSeconds);
            bool addedNotify = EnsureHook(hooks, "Notification", matcher: null, NotifyScript, NotifyTimeoutSeconds);

            if (!addedPre && !addedStop && !addedDebug && !addedNotify)
            {
                Log.Info("hooks: PreToolUse + Stop + UserPromptSubmit + Notification already present; nothing to change");
                return;
            }

            File.WriteAllText(settingsPath, root.ToString(Formatting.Indented));
            Log.Info($"hooks: updated {settingsPath} (PreToolUse {(addedPre ? "added/updated" : "present")}, Stop {(addedStop ? "added/updated" : "present")}, UserPromptSubmit {(addedDebug ? "added/updated" : "present")}, Notification {(addedNotify ? "added/updated" : "present")})");
        }
        catch (Exception e)
        {
            Log.Warn($"hook install failed: {e.Message}");
        }
    }

    /// <summary>
    /// Add a hook for <paramref name="eventName"/> pointing at <paramref name="script"/>, or MIGRATE an
    /// existing entry whose command drifted from the current form (e.g. the pre-1.14.4 unguarded
    /// "-File .claude/x.ps1" shape). Returns true if it changed settings.
    /// </summary>
    private static bool EnsureHook(JObject hooks, string eventName, string? matcher, string script, int timeoutSeconds)
    {
        // Same clone-on-reparent hazard as above: keep the existing array in place, only assign a new one.
        if (hooks[eventName] is not JArray arr)
        {
            arr = new JArray();
            hooks[eventName] = arr;
        }

        var existing = FindOurHook(arr, script);
        if (existing != null)
        {
            var want = Command(script);
            if (string.Equals((string?)existing["command"], want, StringComparison.Ordinal)) return false;
            // Migrate IN PLACE: setting a value property on the EXISTING object is safe - it's
            // re-parenting a token into a new parent that clones (the 1.11.0 lesson), not this.
            existing["command"] = want;
            return true;
        }

        var entry = new JObject
        {
            ["hooks"] = new JArray(new JObject
            {
                ["type"] = "command",
                ["command"] = Command(script),
                ["timeout"] = timeoutSeconds,
            }),
        };
        if (matcher != null)
            entry["matcher"] = matcher;
        arr.Add(entry);
        return true;
    }

    /// <summary>The inner hook object whose command references our script, or null. Match is by script
    /// name so every historical command shape (old -File form, new guarded form) is found and owned.</summary>
    private static JObject? FindOurHook(JArray eventHooks, string script)
    {
        foreach (var entry in eventHooks.OfType<JObject>())
        {
            if (entry["hooks"] is not JArray hs) continue;
            foreach (var h in hs.OfType<JObject>())
            {
                var cmd = (string?)h["command"];
                if (cmd != null && cmd.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0)
                    return h;
            }
        }
        return null;
    }

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(PermissionHookInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded hook script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
