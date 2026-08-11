using ClaudeCodeVs.Spike;

// ---------------------------------------------------------------------------
// Phase 0 protocol spike entry point. Three modes:
//
//   dotnet run --project spike                 interactive: serve + a/r for diffs, m/M/t for at_mention probes
//   dotnet run --project spike -- --self-test  automated: spin up server + run the protocol client
//   dotnet run --project spike -- --probe-cli  launch the real `claude -p` and watch the handshake
//
// Flags: --auto-accept-diffs (openDiff resolves itself), --workspace <path>,
//        --gen-attach-files (just write the at_mention probe files and exit).
// ---------------------------------------------------------------------------

bool selfTest = args.Contains("--self-test");
bool probeCli = args.Contains("--probe-cli");
bool autoAccept = args.Contains("--auto-accept-diffs") || selfTest || probeCli;
string workspace = GetOption(args, "--workspace") ?? Directory.GetCurrentDirectory();

// Generate the at_mention probe files (image + txt attachments) and exit - no server, no lockfile.
// Lets a human (or a build check) validate the generated PNGs render before running the live probe.
if (args.Contains("--gen-attach-files"))
{
    foreach (var f in AtMentionProbe.EnsureFiles(workspace))
        Log.Info($"generated {f}");
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Lockfile lifecycle, step 1: reap stale (dead-PID) lockfiles before we add ours. (task 0.7)
Lockfile.ReapStale();

// Pick a free port + write our lockfile. (tasks 0.2)
var lockfile = Lockfile.CreateForFreePort(new[] { workspace });

// Delete the lockfile no matter how we exit (clean exit, Ctrl+C, or process kill backstop).
AppDomain.CurrentDomain.ProcessExit += (_, _) => lockfile.Delete();

// Wire the tool registry + MCP dispatcher + WS server. (tasks 0.1, 0.3, 0.4, 0.5)
var decisions = new DiffDecisions();
var tools = new ToolRegistry(new IIdeTool[]
{
    new OpenFileTool(),
    new OpenDiffTool(decisions, autoAccept),
    new GetCurrentSelectionTool(),
    new GetDiagnosticsTool(),
});
var mcp = new McpServer(tools);
var server = new IdeWebSocketServer(lockfile.Port, lockfile.AuthToken, mcp);

var serverTask = Task.Run(() => server.RunAsync(cts.Token));

int exitCode = 0;
try
{
    if (selfTest)
    {
        await WaitUntilListeningAsync(cts.Token);
        bool ok = await TestClient.RunAsync(lockfile.Path, cts.Token);
        exitCode = ok ? 0 : 1;
        cts.Cancel();
    }
    else if (probeCli)
    {
        await WaitUntilListeningAsync(cts.Token);
        await RunCliProbeAsync(lockfile.Port, workspace, cts.Token);
        cts.Cancel();
    }
    else
    {
        RunInteractive(decisions, server, workspace, lockfile.Port, cts);
        await serverTask; // run until Ctrl+C
    }
}
catch (OperationCanceledException) { /* normal shutdown */ }
finally
{
    cts.Cancel();
    try { await serverTask; } catch { /* swallow shutdown noise */ }
    lockfile.Delete(); // lifecycle step 2: explicit delete on exit (task 0.7)
}

return exitCode;

// ---------------------------------------------------------------------------

// Block until the WS server has actually bound the port (Start() is sync, but RunAsync is on
// another task - a brief poll avoids a connect race in the automated modes).
async Task WaitUntilListeningAsync(CancellationToken ct)
{
    for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync("127.0.0.1", lockfile.Port, ct);
            return;
        }
        catch
        {
            await Task.Delay(50, ct);
        }
    }
}

void RunInteractive(DiffDecisions decisions, IdeWebSocketServer server, string workspace, int port, CancellationTokenSource cts)
{
    Log.Info("");
    Log.Info("Interactive mode. The server is up and the lockfile is written.");
    Log.Info($"To connect the real CLI in another terminal:");
    Log.Info($"    {ClaudeLauncher.ManualRunHint(port)}");
    Log.Info("When Claude proposes an edit, an openDiff will appear here.");
    Log.Info("Keys:  [a] accept diff   [r] reject diff   [q] quit");
    Log.Info("       [m] at_mention image, relative path   [M] image, absolute path   [t] .txt control");
    Log.Info("");

    if (Console.IsInputRedirected)
    {
        Log.Warn("stdin is redirected; key controls disabled. Use --auto-accept-diffs or Ctrl+C.");
        return;
    }

    _ = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).KeyChar;
            switch (key) // case-SENSITIVE: 'm' and 'M' are distinct at_mention path variants
            {
                case 'a' or 'A':
                    Log.Info(decisions.ResolveOldest(true) ? "accepted pending diff" : "no pending diff");
                    break;
                case 'r' or 'R':
                    Log.Info(decisions.ResolveOldest(false) ? "rejected pending diff" : "no pending diff");
                    break;
                case 'm' or 'M' or 't' or 'T':
                    var variant = key == 'M' ? 'M' : char.ToLowerInvariant(key);
                    _ = Task.Run(async () =>
                    {
                        try { await AtMentionProbe.SendAsync(server, workspace, variant, cts.Token); }
                        catch (Exception e) { Log.Error($"at_mention probe failed: {e.Message}"); }
                    });
                    break;
                case 'q' or 'Q':
                    cts.Cancel();
                    return;
            }
        }
    });
}

async Task RunCliProbeAsync(int port, string workingDir, CancellationToken ct)
{
    Log.Info("=== CLI probe: launching the real claude headless and watching for the handshake ===");
    var proc = ClaudeLauncher.StartHeadlessProbe(
        port,
        prompt: "Reply with the single word: connected. Do not use any tools.",
        workingDir);

    if (proc is null) return;

    // Give the CLI time to start, connect, and run the handshake; then tear down.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(90)); // 2.1.223-era cold starts alone run ~30s on Windows
    try
    {
        await proc.WaitForExitAsync(timeout.Token);
        Log.Info($"claude exited with code {proc.ExitCode}");
        // Since ~2.1.223, headless -p answers WITHOUT opening the IDE WebSocket, so "no 'client
        // connected' above" is expected here. This mode now only proves the CLI launches and runs
        // with the env set; verify the actual handshake interactively (run the spike bare, then
        // `claude` with the env line it prints, then /ide).
        Log.Info("(handshake check: if no 'client connected' logged above, that's normal for headless -p on 2.1.223+ - run the interactive mode to see the real handshake.)");
    }
    catch (OperationCanceledException)
    {
        Log.Warn("CLI probe timed out; killing claude");
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}

static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
