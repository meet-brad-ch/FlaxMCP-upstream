using FlaxMcpBridge;
using Xunit;

namespace FlaxMcp.Tests.Bridge;

// The registry is static: these tests use names no other test registers, and dispose what they register.
public sealed class GameCommandsTests
{
    [Fact]
    public void Register_ListsAndFindsTheCommandUntilDisposed()
    {
        var registration = GameCommands.Register("gc-test-echo", "Echoes.", arg => "echo:" + arg);

        Assert.Contains(GameCommands.List(), info => info.Name == "gc-test-echo" && info.Description == "Echoes.");
        Assert.Equal("echo:x", GameCommands.Find("gc-test-echo")!.Handler("x"));

        registration.Dispose();
        registration.Dispose();

        Assert.Null(GameCommands.Find("gc-test-echo"));
    }

    [Fact]
    public void Register_NamesTheTypeThatWroteTheLambda()
    {
        using var registration = GameCommands.Register("gc-test-owner", "Owner.", arg => arg);

        Assert.Equal(typeof(GameCommandsTests).FullName, GameCommands.Find("gc-test-owner")!.Info.Owner);
    }

    [Fact]
    public void Register_TheSameNameTwice_Throws()
    {
        using var first = GameCommands.Register("gc-test-twice", "First.", arg => arg);

        var exception = Assert.Throws<InvalidOperationException>(() => GameCommands.Register("gc-test-twice", "Second.", arg => arg));

        Assert.Contains("already registered", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Status")]
    [InlineData("1st")]
    [InlineData("with space")]
    [InlineData("under_score")]
    public void Register_ABadName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => GameCommands.Register(name, "Bad.", arg => arg));
    }

    [Fact]
    public void Register_WithoutDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() => GameCommands.Register("gc-test-nodesc", " ", arg => arg));
    }

    [Fact]
    public void List_IsSortedByName()
    {
        using var b = GameCommands.Register("gc-test-sort-b", "B.", arg => arg);
        using var a = GameCommands.Register("gc-test-sort-a", "A.", arg => arg);

        var names = GameCommands.List().Select(info => info.Name).Where(name => name.StartsWith("gc-test-sort-", StringComparison.Ordinal)).ToArray();

        Assert.Equal(["gc-test-sort-a", "gc-test-sort-b"], names);
    }
}
