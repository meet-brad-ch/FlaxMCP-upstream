using FlaxMcpBridge.Host;
using Xunit;

namespace FlaxMcp.Tests.Bridge;

public sealed class SnippetCacheTests
{
    [Fact]
    public void Find_ReturnsWhatWasStored()
    {
        var cache = new SnippetCache(2);
        byte[] image = [1, 2, 3];

        cache.Store("a", image);

        Assert.Same(image, cache.Find("a"));
        Assert.Null(cache.Find("b"));
    }

    [Fact]
    public void Store_WhenFull_DropsTheLeastRecentlyUsed()
    {
        var cache = new SnippetCache(2);
        cache.Store("a", [1]);
        cache.Store("b", [2]);
        cache.Find("a");

        cache.Store("c", [3]);

        Assert.NotNull(cache.Find("a"));
        Assert.Null(cache.Find("b"));
        Assert.NotNull(cache.Find("c"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Store_UnderAnExistingKey_ReplacesTheImage()
    {
        var cache = new SnippetCache(2);
        cache.Store("a", [1]);
        byte[] newer = [9];

        cache.Store("a", newer);

        Assert.Same(newer, cache.Find("a"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Constructor_WithoutCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnippetCache(0));
    }
}
