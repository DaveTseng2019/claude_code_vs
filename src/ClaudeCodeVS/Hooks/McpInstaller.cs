using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Registers the Phase 2 debug PULL channel as a project MCP server. Writes the embedded stdio shim into
/// the workspace's .claude/ folder and upserts an entry into the workspace .mcp.json so the CLI launches
/// it as an MCP server. The shim then discovers the live VS bridge and proxies JSON-RPC to POST /mcp,
/// where the real vs_* debug tools run (in-proc, EnvDTE-backed). Called from Launch alongside the hook
/// installer. Best-effort; idempotent; never throws into the launch path.
///
/// Note: a project-scoped .mcp.json makes the CLI prompt once to trust the server. That's expected.
/// </summary>
internal static class McpInstaller
{
    private const string ShimScript = "vs-mcp-shim.ps1";

    // The two pull-channel MCP servers, both backed by the same stdio shim (different -Route → different
    // bridge endpoint). vs-debug stays byte-identical to its original entry (no extra args → shim default
    // /mcp); vs-semantic adds -Route /mcp-semantic for the Roslyn code-navigation tools.
    private static readonly (string Name, string[] ExtraArgs)[] Servers =
    {
        ("vs-debug", new string[0]),
        ("vs-semantic", new[] { "-Route", "/mcp-semantic" }),
    };

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the shim from the embedded copy, so updates ship with the extension -
            //    unless the user took ownership by removing the marker line (issue #17).
            ManagedScripts.WriteIfManaged(Path.Combine(claudeDir, ShimScript), ReadEmbeddedScript(ShimScript));

            // 2) Upsert the server entry into <workspace>/.mcp.json, preserving any other servers. The
            //    relative -File path resolves against the CLI's cwd (the workspace root), matching where
            //    the shim was written. We always (re)write OUR entry so command/args updates ship, but
            //    leave the rest of the file untouched.
            var mcpPath = Path.Combine(workspaceRoot, ".mcp.json");
            JObject root;
            if (File.Exists(mcpPath))
            {
                try { root = JObject.Parse(File.ReadAllText(mcpPath)); }
                catch (Exception e)
                {
                    Log.Warn($"mcp: couldn't parse {mcpPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            // Only assign when creating: re-assigning an already-parented JToken makes Json.NET CLONE it
            // into the parent, detaching this local reference - upserts would then never reach the file
            // (same bug as the hook installer; matters for users upgrading with an existing .mcp.json).
            if (root["mcpServers"] is not JObject servers)
            {
                servers = new JObject();
                root["mcpServers"] = servers;
            }

            bool changed = false;
            foreach (var (name, extraArgs) in Servers)
            {
                var argv = new JArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $".claude/{ShimScript}");
                foreach (var a in extraArgs) argv.Add(a);
                var desired = new JObject
                {
                    ["type"] = "stdio",
                    ["command"] = "powershell",
                    ["args"] = argv,
                };

                if (JToken.DeepEquals(servers[name], desired)) continue;
                servers[name] = desired;
                changed = true;
            }

            if (!changed)
            {
                Log.Info($"mcp: 'vs-debug' + 'vs-semantic' already registered in {mcpPath}; nothing to change");
                return;
            }

            File.WriteAllText(mcpPath, root.ToString(Formatting.Indented));
            Log.Info($"mcp: registered 'vs-debug' + 'vs-semantic' MCP servers in {mcpPath} (pull channels)");
        }
        catch (Exception e)
        {
            Log.Warn($"mcp install failed: {e.Message}");
        }
    }

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(McpInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded shim script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
