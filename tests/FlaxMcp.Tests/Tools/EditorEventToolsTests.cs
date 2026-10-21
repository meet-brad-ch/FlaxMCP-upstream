using FlaxMcp.Flax;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public sealed class EditorEventToolsTests
{
    [Fact]
    public async Task GetEventsAsync_ForwardsTheQueryInMilliseconds()
    {
        var client = new StubEventClient();
        var tool = new EditorEventTools(client);

        await tool.GetEventsAsync(5, ["log"], 20, 1.5, TestContext.Current.CancellationToken);

        Assert.Equal(5, client.AfterSeq);
        Assert.Equal(["log"], client.Kinds!);
        Assert.Equal(20, client.Max);
        Assert.Equal(1500, client.WaitMilliseconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(501, 0)]
    [InlineData(10, 2.5)]
    [InlineData(10, -1)]
    public async Task GetEventsAsync_OutOfRange_Throws(int limit, double waitSeconds)
    {
        var tool = new EditorEventTools(new StubEventClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            tool.GetEventsAsync(0, null, limit, waitSeconds, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForAsync_ForwardsKindTextAndTimeout()
    {
        var client = new StubEventClient();
        var tool = new EditorEventTools(client);

        var result = await tool.WaitForAsync("log", "[Test]", 7, 12, TestContext.Current.CancellationToken);

        Assert.True(result.Matched);
        Assert.Equal("log", client.WaitKind);
        Assert.Equal("[Test]", client.WaitText);
        Assert.Equal(7, client.WaitAfter);
        Assert.Equal(TimeSpan.FromSeconds(12), client.WaitTimeout);
    }

    [Fact]
    public async Task WaitForAsync_LongerThanTenMinutes_Throws()
    {
        var tool = new EditorEventTools(new StubEventClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            tool.WaitForAsync("log", null, null, 601, TestContext.Current.CancellationToken));
    }

    private sealed class StubEventClient : IFlaxEditorEventClient
    {
        public long AfterSeq { get; private set; }

        public IReadOnlyList<string>? Kinds { get; private set; }

        public int Max { get; private set; }

        public int WaitMilliseconds { get; private set; }

        public string? WaitKind { get; private set; }

        public string? WaitText { get; private set; }

        public long? WaitAfter { get; private set; }

        public TimeSpan WaitTimeout { get; private set; }

        public Task<FlaxEditorEventPage> GetEventsAsync(
            long afterSeq, IReadOnlyList<string>? kinds, int max, int waitMilliseconds, CancellationToken cancellationToken = default)
        {
            AfterSeq = afterSeq;
            Kinds = kinds;
            Max = max;
            WaitMilliseconds = waitMilliseconds;
            return Task.FromResult(new FlaxEditorEventPage(DateTime.UnixEpoch, [], 0, 0, false));
        }

        public Task<FlaxEditorEventWait> WaitForEventAsync(
            string kind, string? textContains, long? afterSeq, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitKind = kind;
            WaitText = textContains;
            WaitAfter = afterSeq;
            WaitTimeout = timeout;
            return Task.FromResult(new FlaxEditorEventWait(true, new FlaxEditorEvent(8, DateTime.UnixEpoch, kind, 0, textContains), 8, false, 0.1));
        }
    }
}
