using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Debugging;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Managed DATA breakpoint tools - watch an instance field and get every change (a data tracepoint),
/// with optional condition + stop-on-change. Backed by the Concord component shipped in this VSIX,
/// driven over file-IPC by <see cref="DataBreakpointBridge"/>. vs_set_* is gated behind
/// AllowDebuggerDrive (it can break execution); vs_get_* is a plain read.
/// </summary>
internal sealed class VsSetDataBreakpointTool : IIdeTool
{
    private readonly DataBreakpointBridge _bridge;
    public VsSetDataBreakpointTool(DataBreakpointBridge bridge) => _bridge = bridge;

    public string Name => "vs_set_data_breakpoint";

    public string Description =>
        "Watch a managed INSTANCE FIELD and get notified on every change - a data breakpoint / data "
        + "tracepoint that VS's UI can't set programmatically. Call while PAUSED where the owning object "
        + "is in scope. 'expression' MUST be owner.field (e.g. order.Total); statics, locals and struct "
        + "fields are unsupported. Each change (old->new value) is recorded - poll vs_get_data_changes "
        + "with the returned requestId for the timeline. Optional 'condition' (\"> 700\", \"== 0\", "
        + "\"!= 5\") filters; with 'stopOnChange' execution breaks on a matching change so you can inspect "
        + "locals at the mutation site. Requires 'Allow Claude to drive debugger' enabled.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["expression"] = new JObject { ["type"] = "string", ["description"] = "owner.field to watch - an instance field of a heap object, e.g. order.Total." },
            ["condition"] = new JObject { ["type"] = "string", ["description"] = "Optional. Match only when the new value satisfies a comparison: one of > >= < <= == != followed by a number (or ==/!= with a string). Empty = every change matches." },
            ["stopOnChange"] = new JObject { ["type"] = "boolean", ["description"] = "Optional (default false). When true, break execution on EACH change matching the condition so you can inspect locals at the mutation (re-breaks every time, like a normal breakpoint). Note: the stop lands one statement AFTER the write (the data breakpoint fires once the write completes)." },
        },
        ["required"] = new JArray("expression"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        // Experimental ARM64 host support (1.14.3): managed data breakpoints are an x64-only capability
        // of the .NET runtime/debugger itself - not something this extension can port around.
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            return new JObject { ["error"] = "managed data breakpoints are x64-only (a .NET runtime/debugger limitation) - unavailable on ARM64. Use vs_set_breakpoint with a condition as the nearest substitute." };

        if (!Ui.BridgeStatus.AllowDebuggerDrive)
            return new JObject
            {
                ["error"] = "debugger driving is disabled",
                ["hint"] = "Enable 'Allow Claude to drive debugger' in the Claude Code panel in Visual Studio, then retry.",
            };

        string expr = (string?)args?["expression"] ?? "";
        string cond = (string?)args?["condition"];
        bool stop = (bool?)args?["stopOnChange"] ?? false;

        var r = await _bridge.SetWatchAsync(expr, cond, stop, ct);
        Ui.BridgeStatus.RecordDebugDrive();
        Log.Info($"vs_set_data_breakpoint({expr}) -> {(r["error"] != null ? "error: " + (string?)r["error"] : (string?)r["status"])}");
        return r;
    }
}

internal sealed class VsGetDataChangesTool : IIdeTool
{
    private readonly DataBreakpointBridge _bridge;
    public VsGetDataChangesTool(DataBreakpointBridge bridge) => _bridge = bridge;

    public string Name => "vs_get_data_changes";

    public string Description =>
        "Return the change timeline for a data breakpoint armed with vs_set_data_breakpoint: every change "
        + "seen so far (oldest first) as structured {previous, current, type}, plus 'broke'/'breakCount'. "
        + "Read-only; poll it to follow a value across a run - e.g. to find which write turned it bad, then "
        + "set a normal breakpoint at that write site.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["requestId"] = new JObject { ["type"] = "string", ["description"] = "The requestId returned by vs_set_data_breakpoint." },
        },
        ["required"] = new JArray("requestId"),
    };

    public Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var r = _bridge.GetChanges((string?)args?["requestId"]);
        Ui.BridgeStatus.RecordDebugInspect();
        return Task.FromResult<object>(r);
    }
}

internal sealed class VsRemoveDataBreakpointTool : IIdeTool
{
    private readonly DataBreakpointBridge _bridge;
    public VsRemoveDataBreakpointTool(DataBreakpointBridge bridge) => _bridge = bridge;

    public string Name => "vs_remove_data_breakpoint";

    public string Description =>
        "Disarm a data breakpoint set with vs_set_data_breakpoint: stop watching/breaking and Close its "
        + "engine binding. Pass the requestId. Safe to call anytime; if the debuggee is running the "
        + "binding closes at the next stop. (Multiple data breakpoints can be armed at once - this "
        + "removes just the one you name.)";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["requestId"] = new JObject { ["type"] = "string", ["description"] = "The requestId returned by vs_set_data_breakpoint." },
        },
        ["required"] = new JArray("requestId"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var r = await _bridge.RemoveWatchAsync((string?)args?["requestId"], ct);
        Ui.BridgeStatus.RecordDebugDrive();
        Log.Info($"vs_remove_data_breakpoint -> {(r["error"] != null ? "error" : (string?)r["status"])}");
        return r;
    }
}
