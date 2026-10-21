using FlaxMcp.Flax;
using ModelContextProtocol;
using Xunit;

namespace FlaxMcp.Tests.Flax;

public sealed class FlaxJobManagerTests
{
    [Fact]
    public async Task Job_ThatSucceeds_ReportsItsResult()
    {
        var jobs = new FlaxJobManager();

        var handle = jobs.Start("compile", exclusive: false, (progress, cancellationToken) => Task.FromResult<object>("done"));
        var state = await jobs.WaitAsync(handle.JobId, TimeSpan.FromSeconds(5), null, TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", state.Status);
        Assert.Equal("done", state.Result);
        Assert.NotNull(state.EndedUtc);
        Assert.StartsWith("job-", handle.JobId);
    }

    [Fact]
    public async Task Job_ThatThrows_ReportsFailedWithTheMessage()
    {
        var jobs = new FlaxJobManager();

        var handle = jobs.Start("compile", exclusive: false, (progress, cancellationToken) => throw new InvalidOperationException("broken"));
        var state = await jobs.WaitAsync(handle.JobId, TimeSpan.FromSeconds(5), null, TestContext.Current.CancellationToken);

        Assert.Equal("failed", state.Status);
        Assert.Equal("broken", state.Error);
    }

    [Fact]
    public async Task Wait_ThatTimesOut_ReturnsRunningWithTheLatestMessage()
    {
        var jobs = new FlaxJobManager();
        var release = new TaskCompletionSource<object>();

        var handle = jobs.Start("play_mode_start", exclusive: false, (progress, cancellationToken) =>
        {
            progress.Report("loading the scene");
            return release.Task;
        });
        var state = await jobs.WaitAsync(handle.JobId, TimeSpan.FromMilliseconds(300), null, TestContext.Current.CancellationToken);
        release.SetResult("ok");

        Assert.Equal("running", state.Status);
        Assert.Equal("loading the scene", state.Message);
        Assert.Null(state.EndedUtc);
    }

    [Fact]
    public async Task Wait_ForwardsEachNewProgressMessage()
    {
        var jobs = new FlaxJobManager();
        var step = new TaskCompletionSource<object>();
        var reports = new List<ProgressNotificationValue>();

        var handle = jobs.Start("compile", exclusive: false, async (progress, cancellationToken) =>
        {
            progress.Report("compiling");
            await step.Task;
            return "done";
        });
        var waiting = jobs.WaitAsync(handle.JobId, TimeSpan.FromSeconds(5), new SyncProgress(reports), TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        step.SetResult(true);
        await waiting;

        var report = Assert.Single(reports);
        Assert.Equal("compiling", report.Message);
        Assert.Equal(1, report.Progress);
    }

    [Fact]
    public void ExclusiveJob_WhileAnotherRuns_FailsWithJobConflict()
    {
        var jobs = new FlaxJobManager();
        var never = new TaskCompletionSource<object>();
        var first = jobs.Start("compile", exclusive: true, (progress, cancellationToken) => never.Task);

        var exception = Assert.Throws<McpException>(() =>
            jobs.Start("play_mode_start", exclusive: true, (progress, cancellationToken) => Task.FromResult<object>("x")));

        Assert.Contains("job_conflict", exception.Message);
        Assert.Contains(first.JobId, exception.Message);
        never.SetResult("released");
    }

    [Fact]
    public void NonExclusiveJob_RunsBesideAnExclusiveOne()
    {
        var jobs = new FlaxJobManager();
        var never = new TaskCompletionSource<object>();
        jobs.Start("compile", exclusive: true, (progress, cancellationToken) => never.Task);

        var build = jobs.Start("build_game", exclusive: false, (progress, cancellationToken) => Task.FromResult<object>("x"));

        Assert.Equal("build_game", build.Kind);
        never.SetResult("released");
    }

    [Fact]
    public void Status_OfAnUnknownJob_Throws()
    {
        var exception = Assert.Throws<McpException>(() => new FlaxJobManager().Status("job-99"));

        Assert.Contains("job-99", exception.Message);
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool, which makes a test race).</summary>
    private sealed class SyncProgress(List<ProgressNotificationValue> reports) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}
