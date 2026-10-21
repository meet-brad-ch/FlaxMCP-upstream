using FlaxMcp.Flax;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public sealed class GameToolsTests
{
    [Fact]
    public async Task ListGameCommandsAsync_ReturnsTheBridgesList()
    {
        var client = new StubGameCommandClient();
        var tool = new GameTools(client);

        var list = await tool.ListGameCommandsAsync(TestContext.Current.CancellationToken);

        Assert.Same(client.List, list);
    }

    [Fact]
    public async Task RunGameCommandAsync_ForwardsNameAndArgs()
    {
        var client = new StubGameCommandClient();
        var tool = new GameTools(client);

        var result = await tool.RunGameCommandAsync("status", "verbose", TestContext.Current.CancellationToken);

        Assert.Equal("status", client.Name);
        Assert.Equal("verbose", client.Args);
        Assert.Equal("ok", result.Result);
    }

    [Fact]
    public async Task RunGameCommandAsync_WithoutName_Throws()
    {
        var tool = new GameTools(new StubGameCommandClient());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.RunGameCommandAsync(" ", "", TestContext.Current.CancellationToken));
    }

    private sealed class StubGameCommandClient : IFlaxGameCommandClient
    {
        public FlaxGameCommandList List { get; } = new([new FlaxGameCommandInfo("status", "Prints the state.", "Game.TestDriver")]);

        public string? Name { get; private set; }

        public string? Args { get; private set; }

        public Task<FlaxGameCommandList> ListGameCommandsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(List);
        }

        public Task<FlaxGameCommandResult> RunGameCommandAsync(string name, string args, CancellationToken cancellationToken = default)
        {
            Name = name;
            Args = args;
            return Task.FromResult(new FlaxGameCommandResult(1, name, "ok", 0.5, true));
        }
    }
}
