using System.Text.Json;
using FlaxMcp.Flax;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public sealed class EditorObjectToolsTests
{
    [Fact]
    public async Task FindActorsAsync_ForwardsTheFilters()
    {
        var client = new StubObjectClient();

        await new EditorObjectTools(client).FindActorsAsync("Player", "StaticModel", "PlayerController", 5, TestContext.Current.CancellationToken);

        Assert.Equal("Player|StaticModel|PlayerController|5", client.Last);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task FindActorsAsync_WithALimitOutOfRange_Throws(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new EditorObjectTools(new StubObjectClient()).FindActorsAsync(null, null, null, limit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetMemberAsync_ForwardsTheJsonValue()
    {
        var client = new StubObjectClient();
        using var value = JsonDocument.Parse("{\"X\":1,\"Y\":2,\"Z\":3}");

        await new EditorObjectTools(client).SetMemberAsync("id", "Position", value.RootElement, "Mover", TestContext.Current.CancellationToken);

        Assert.Equal("id|Mover|Position|{\"X\":1,\"Y\":2,\"Z\":3}", client.Last);
    }

    [Fact]
    public async Task InvokeMethodAsync_WithoutArgs_SendsAnEmptyArray()
    {
        var client = new StubObjectClient();

        await new EditorObjectTools(client).InvokeMethodAsync("BuildAll", null, null, null, "MaterialBuilder", TestContext.Current.CancellationToken);

        Assert.Equal("MaterialBuilder|BuildAll|||[]", client.Last);
    }

    [Fact]
    public async Task GetMemberAsync_WithoutMember_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new EditorObjectTools(new StubObjectClient()).GetMemberAsync("id", " ", null, TestContext.Current.CancellationToken));
    }

    private sealed class StubObjectClient : IFlaxEditorObjectClient
    {
        public string? Last { get; private set; }

        public Task<FlaxFoundActors> FindActorsAsync(
            string? nameContains, string? actorType, string? scriptType, int limit, CancellationToken cancellationToken = default)
        {
            Last = $"{nameContains}|{actorType}|{scriptType}|{limit}";
            return Task.FromResult(new FlaxFoundActors(1, [], false));
        }

        public Task<FlaxMemberValue> GetMemberAsync(string actorId, string? scriptType, string member, CancellationToken cancellationToken = default)
        {
            Last = $"{actorId}|{scriptType}|{member}";
            return Task.FromResult(new FlaxMemberValue(1, actorId, "T", member, null, null));
        }

        public Task<FlaxMemberValue> SetMemberAsync(
            string actorId, string? scriptType, string member, JsonElement value, CancellationToken cancellationToken = default)
        {
            Last = $"{actorId}|{scriptType}|{member}|{value.GetRawText()}";
            return Task.FromResult(new FlaxMemberValue(1, actorId, "T", member, null, value, true));
        }

        public Task<FlaxMethodResult> InvokeMethodAsync(
            string? typeName, string method, string? actorId, string? scriptType, JsonElement args, CancellationToken cancellationToken = default)
        {
            Last = $"{typeName}|{method}|{actorId}|{scriptType}|{args.GetRawText()}";
            return Task.FromResult(new FlaxMethodResult(1, method, null, null));
        }

        public Task<FlaxAddedScript> AddScriptAsync(string actorId, string scriptType, int? orderInParent, CancellationToken cancellationToken = default)
        {
            Last = $"{actorId}|{scriptType}|{orderInParent}";
            return Task.FromResult(new FlaxAddedScript(1, actorId, "s", scriptType, true, 0, [scriptType]));
        }
    }
}
