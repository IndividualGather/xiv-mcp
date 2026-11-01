using System.Text.Json.Nodes;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

/// <summary>Calls that act in the game run one at a time: two at once can deadlock the game's windows.</summary>
public class ActionLaneTests
{
    private sealed class Allow : IApprovalGate
    {
        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(ApprovalDecision.ApprovedOnce);
    }

    private sealed class NoProbe : IGameProbe { public GameSnapshot? Capture() => null; }
    private sealed class NoNotifier : ISecurityNotifier { public void Flagged(ToolProvider p, string t, IReadOnlyList<SideEffect> e, bool s) { } }

    private readonly InMemoryPolicyStore store = new();
    private DateTime now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly ToolGate gate;

    public ActionLaneTests() => gate = new(store, new Allow(), new NoProbe(), new AuditLog(100), new NoNotifier(), () => now);

    private McpTool Tool(string name, bool readOnly = false, Func<Task>? body = null)
    {
        store.Core.SetTool(name, PolicyMode.Allow);
        return new()
        {
            Name = name, Description = "Does it.", ReadOnly = readOnly,
            Handler = async (_, _) => { if (body is not null) await body(); return "ran"; },
        };
    }

    private static ToolArgs Args() => new(new JsonObject());

    [Fact]
    public async Task A_second_change_while_one_runs_is_refused_and_names_the_running_one()
    {
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync(Tool("deploy_submersible", body: () => release.Task), Args(), default);
        now = now.AddSeconds(12);

        var ex = await Assert.ThrowsAsync<ToolException>(() => gate.InvokeAsync(Tool("collect_submersible"), Args(), default));
        Assert.Equal("deploy_submersible is still running (for 12 s). Tools that act in the game run one at a time, so they can't get in each " +
                     "other's way: call collect_submersible again when it has finished.", ex.Message);

        release.SetResult();
        Assert.Equal("ran", await first);
        Assert.Equal("ran", await gate.InvokeAsync(Tool("collect_submersible"), Args(), default));
    }

    [Fact]
    public async Task Reading_works_while_a_change_runs()
    {
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync(Tool("navigate_to", body: () => release.Task), Args(), default);
        Assert.Equal("ran", await gate.InvokeAsync(Tool("get_game_status", readOnly: true), Args(), default));
        release.SetResult();
        await first;
    }

    [Theory]
    [InlineData("stop_navigation")]
    [InlineData("cancel_job")]
    [InlineData("pause_job")]
    [InlineData("wait")]
    [InlineData("request_spending_approval")]
    public async Task Stopping_job_control_waiting_and_xivmcps_own_windows_are_never_held_back(string tool)
    {
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync(Tool("navigate_to", body: () => release.Task), Args(), default);
        Assert.Equal("ran", await gate.InvokeAsync(Tool(tool), Args(), default));
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task A_job_step_waits_for_its_turn_instead_of_failing()
    {
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync(Tool("navigate_to", body: () => release.Task), Args(), default);
        var step = gate.InvokeAsync(Tool("collect_submersible"), Args(), default, inJob: true);
        await Task.Delay(50);
        Assert.False(step.IsCompleted);

        release.SetResult();
        await first;
        Assert.Equal("ran", await step);
    }

    [Fact]
    public async Task A_failed_call_frees_the_lane()
    {
        await Assert.ThrowsAsync<ToolException>(() => gate.InvokeAsync(Tool("deploy_submersible", body: () => throw new ToolException("menu did not open")), Args(), default));
        Assert.Equal("ran", await gate.InvokeAsync(Tool("deploy_submersible"), Args(), default));
    }

    [Fact]
    public async Task Third_party_tools_that_act_take_their_turn_too()
    {
        var plugin = new ToolProvider("HelloMcp", "Hello MCP", ProviderTrust.ThirdParty);
        store.Get(plugin.Id).Enabled = true;
        var release = new TaskCompletionSource();
        var first = gate.InvokeAsync(Tool("navigate_to", body: () => release.Task), Args(), default);
        var theirs = new McpTool { Name = "hellomcp_wave", Description = "Waves.", Provider = plugin, ReadOnly = false, Capabilities = [Capabilities.ReadGame, Capabilities.GameUi], Handler = (_, _) => Task.FromResult<object?>("ran") };
        store.Get(plugin.Id).SetTool(theirs.Name, PolicyMode.Allow);

        var ex = await Assert.ThrowsAsync<ToolException>(() => gate.InvokeAsync(theirs, Args(), default));
        Assert.StartsWith("navigate_to is still running", ex.Message);
        release.SetResult();
        await first;
    }
}
