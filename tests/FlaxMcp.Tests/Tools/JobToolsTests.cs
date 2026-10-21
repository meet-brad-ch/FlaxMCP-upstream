using FlaxMcp.Flax;
using FlaxMcp.Tools;
using ModelContextProtocol;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public sealed class JobToolsTests
{
    [Fact]
    public async Task StartEditorJob_Compile_RunsTheCompileAsAJob()
    {
        var bridge = new EditorToolsTests.StubBridgeClient { CompileResult = new FlaxEditorCompileResult("reloaded", 1.1, [], [], 1) };
        var jobs = new FlaxJobManager();
        var tool = new JobTools(jobs, bridge, new StubScriptChecker());

        var handle = tool.StartEditorJob("compile");
        var state = await tool.WaitAsync(handle.JobId, 5, null, TestContext.Current.CancellationToken);

        Assert.Equal("compile", handle.Kind);
        Assert.Equal("succeeded", state.Status);
        Assert.Equal(bridge.CompileResult with { Check = "passed", Seconds = bridge.CompileResult.Seconds + 0.5 }, state.Result);
    }

    [Fact]
    public void StartEditorJob_WithAnUnknownKind_ListsTheKinds()
    {
        var tool = new JobTools(new FlaxJobManager(), new EditorToolsTests.StubBridgeClient(), new StubScriptChecker());

        var exception = Assert.Throws<McpException>(() => tool.StartEditorJob("bake"));

        Assert.Contains("play_start", exception.Message);
    }

    [Fact]
    public void StartEditorJob_OpenSceneWithoutPath_Throws()
    {
        var tool = new JobTools(new FlaxJobManager(), new EditorToolsTests.StubBridgeClient(), new StubScriptChecker());

        Assert.Throws<McpException>(() => tool.StartEditorJob("open_scene"));
    }

    [Fact]
    public async Task WaitAsync_LongerThanThirtyMinutes_Throws()
    {
        var tool = new JobTools(new FlaxJobManager(), new EditorToolsTests.StubBridgeClient(), new StubScriptChecker());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            tool.WaitAsync("job-1", 1801, null, TestContext.Current.CancellationToken));
    }
}
