using FlaxMcpBridge.Host;
using Xunit;

namespace FlaxMcp.Tests.Bridge;

public sealed class BridgeEventRingTests
{
    [Fact]
    public void Read_ReturnsTheEventsAfterTheSequenceInOrder()
    {
        var ring = new BridgeEventRing(8);
        ring.Append(BridgeEventKind.CompileBegin, 0, null);
        ring.Append(BridgeEventKind.Log, 2, "a");
        ring.Append(BridgeEventKind.Log, 2, "b");

        var page = ring.Read(1, 10, null);

        Assert.Equal([2L, 3L], page.Events.Select(item => item.Seq));
        Assert.Equal(["a", "b"], page.Events.Select(item => item.Text));
        Assert.Equal(3, page.LastSeq);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void Read_FiltersByKindAndStopsAtMax()
    {
        var ring = new BridgeEventRing(16);
        for (var i = 0; i < 5; i++)
        {
            ring.Append(BridgeEventKind.Log, 0, "log " + i);
            ring.Append(BridgeEventKind.ReloadEnd, 0, null);
        }

        var page = ring.Read(0, 3, [BridgeEventKind.ReloadEnd]);

        Assert.Equal(3, page.Events.Count);
        Assert.All(page.Events, item => Assert.Equal(BridgeEventKind.ReloadEnd, item.Kind));
    }

    [Fact]
    public void Read_AfterTheRingWrapped_ReportsTruncationAndTheOldestHeld()
    {
        var ring = new BridgeEventRing(4);
        for (var i = 1; i <= 10; i++)
        {
            ring.Append(BridgeEventKind.Log, 0, "line " + i);
        }

        var page = ring.Read(2, 100, null);

        Assert.True(page.Truncated);
        Assert.Equal(7, page.OldestSeq);
        Assert.Equal([7L, 8L, 9L, 10L], page.Events.Select(item => item.Seq));
    }

    [Fact]
    public void Read_OfAnEmptyRing_IsEmpty()
    {
        var page = new BridgeEventRing(4).Read(0, 10, null);

        Assert.Empty(page.Events);
        Assert.Equal(0, page.LastSeq);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void Append_WithoutWaiters_AllocatesNothing()
    {
        var ring = new BridgeEventRing(4096);
        const string text = "a log line the engine already made";
        ring.Append(BridgeEventKind.Log, 1, text);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10_000; i++)
        {
            ring.Append(BridgeEventKind.Log, 1, text);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task WaitAsync_SkipsEventsThatDoNotMatch()
    {
        var ring = new BridgeEventRing(16);
        var waiting = ring.WaitAsync(0, 10, [BridgeEventKind.PlayBegin], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        ring.Append(BridgeEventKind.Log, 0, "noise");
        ring.Append(BridgeEventKind.PlayBegin, 0, null);
        var page = await waiting;

        Assert.Single(page.Events);
        Assert.Equal(BridgeEventKind.PlayBegin, page.Events[0].Kind);
    }

    [Fact]
    public async Task WaitAsync_WithoutAMatch_ReturnsEmptyAfterTheWait()
    {
        var ring = new BridgeEventRing(16);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var page = await ring.WaitAsync(0, 10, null, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
        Assert.InRange(watch.ElapsedMilliseconds, 150, 2000);
    }
}
