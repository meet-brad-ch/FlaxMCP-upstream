using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaxMcp.Flax;
using ModelContextProtocol;
using Xunit;

namespace FlaxMcp.Tests.Flax;

public sealed class FlaxBridgeClientTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FlaxMcpTests_" + Guid.NewGuid().ToString("N"));

    private readonly string _projectFolder = Path.Combine(
        Path.GetTempPath(),
        "FlaxProject_" + Guid.NewGuid().ToString("N")
    );

    public FlaxBridgeClientTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_projectFolder);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
        Directory.Delete(_projectFolder, recursive: true);
    }

    [Fact]
    public async Task GetStatusAsync_WithoutHandshake_ReturnsDisconnected()
    {
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlaxBridgeStatus.Disconnected, status);
    }

    [Fact]
    public async Task PingAsync_WithoutHandshake_ThrowsClientVisibleErrorWithoutProjectPath()
    {
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            client.PingAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "No Flax Editor bridge session is available for the configured project.",
            exception.Message);
        Assert.DoesNotContain(_projectFolder, exception.Message);
    }

    [Fact]
    public async Task GetStatusAsync_WithReachableBridge_ReturnsHandshakeMetadata()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        var startedUtc = new DateTime(
            2026,
            8,
            20,
            10,
            0,
            0,
            DateTimeKind.Utc
        );
        WriteHandshake(pipeName, startedUtc);
        var serverTask = ServePingAsync(pipeName, TestContext.Current.CancellationToken);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(
            new FlaxBridgeStatus(
                true,
                "1.2.3",
                12000,
                startedUtc,
                null
            ),
            status
        );
    }

    [Fact]
    public async Task GetStatusAsync_WithStaleHandshake_ReturnsDisconnected()
    {
        WriteHandshake("FlaxMcpTests-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlaxBridgeStatus.Disconnected, status);
    }

    [Fact]
    public async Task PingAsync_WithStaleHandshake_ThrowsClientVisibleError()
    {
        WriteHandshake("FlaxMcpTests-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            client.PingAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("The Flax Editor bridge", exception.Message);
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"code\":\"action_failed\",\"message\":\"Editor action failed\"}}")]
    [InlineData("{\"id\":1}")]
    public async Task GetStatusAsync_WithInvalidBridgeResponse_ReturnsDisconnected(string response)
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(FlaxBridgeStatus.Disconnected, status);
    }

    [Fact]
    public async Task PingAsync_WithMismatchedProtocol_ReportsVersions()
    {
        WriteHandshake("unused", DateTime.UtcNow, protocolVersion: 3);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception =
            await Assert.ThrowsAnyAsync<McpException>(()
                => client.PingAsync(TestContext.Current.CancellationToken)
            );

        Assert.Contains(
            $"server requires version {FlaxBridgeClient.CurrentProtocolVersion}",
            exception.Message);
        Assert.Contains("plugin reports version 3", exception.Message);
    }

    [Fact]
    public async Task PingAsync_WithStructuredError_ReportsCodeAndMessage()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"error\":{\"code\":\"action_failed\",\"message\":\"Editor action failed\"}}",
            TestContext.Current.CancellationToken
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception =
            await Assert.ThrowsAsync<FlaxBridgeRequestException>(()
                => client.PingAsync(TestContext.Current.CancellationToken)
            );
        await serverTask;

        Assert.Equal("action_failed", exception.Code);
        Assert.Contains("[action_failed]: Editor action failed", exception.Message);
    }

    [Fact]
    public async Task PingAsync_AfterHandshakeChanges_ConnectsToNewSession()
    {
        WriteHandshake("stale-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        await Assert.ThrowsAnyAsync<Exception>(() => client.PingAsync(TestContext.Current.CancellationToken));

        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServePingAsync(pipeName, TestContext.Current.CancellationToken);

        var ping = await client.PingAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.True(ping.Pong);
    }

    [Fact]
    public async Task PingAsync_WhenBridgeDisconnects_ReportsClearError()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeDisconnectAsync(pipeName, TestContext.Current.CancellationToken);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception =
            await Assert.ThrowsAsync<McpException>(() => client.PingAsync(TestContext.Current.CancellationToken)
            );
        await serverTask;

        Assert.Contains("disconnected before returning a response", exception.Message);
    }

    [Fact]
    public async Task GetSceneGraphAsync_ReturnsTypedLiveTree()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"scenes\":[{\"id\":\"scene-id\",\"typeName\":\"FlaxEngine.Scene\",\"name\":\"Main\",\"children\":[{\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.Actor\",\"name\":\"Player\",\"children\":[]}]}],\"truncated\":false}}",
            TestContext.Current.CancellationToken,
            "scene_graph"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var graph = await client.GetSceneGraphAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(7, graph.MainThreadId);
        var scene = Assert.Single(graph.Scenes);
        Assert.Equal("Main", scene.Name);
        Assert.Equal("Player", Assert.Single(scene.Children).Name);
        Assert.False(graph.Truncated);
    }

    [Fact]
    public async Task GetSelectionAsync_ReturnsTypedSelection()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"selected\":[{\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.Actor\",\"name\":\"Player\"}]}}",
            TestContext.Current.CancellationToken,
            "get_selection"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var selection = await client.GetSelectionAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(7, selection.MainThreadId);
        Assert.Equal("Player", Assert.Single(selection.Selected).Name);
    }

    [Fact]
    public async Task SetSelectionAsync_SendsActorIdsAndReturnsTypedSelection()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"selected\":[]}}",
            TestContext.Current.CancellationToken,
            "set_selection",
            "\"actorIds\":[\"actor-id\"]"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var selection = await client.SetSelectionAsync(["actor-id"], TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Empty(selection.Selected);
    }

    [Fact]
    public async Task GetActorDetailsAsync_ReturnsTypedActorDetails()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.Actor\",\"name\":\"Player\",\"parentId\":null,\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[\"Player\"],\"transform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"localTransform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"scripts\":[{\"id\":\"script-id\",\"typeName\":\"Game.Player\",\"enabled\":true,\"isEnabledInHierarchy\":true}]}}",
            TestContext.Current.CancellationToken,
            "actor_details",
            "\"actorId\":\"actor-id\""
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var details = await client.GetActorDetailsAsync("actor-id", TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("Player", details.Name);
        Assert.Equal(3, details.Transform.Translation.Z);
        Assert.Equal("Game.Player", Assert.Single(details.Scripts).TypeName);
    }

    [Fact]
    public async Task ModifyActorAsync_SendsTransformAndReturnsTypedActorDetails()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var response =
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.Actor\",\"name\":\"Player\",\"parentId\":null,\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{\"translation\":{\"x\":10,\"y\":20,\"z\":30},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"localTransform\":{\"translation\":{\"x\":10,\"y\":20,\"z\":30},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"scripts\":[]}}";
        var serverTask = ServeResponseAsync(
            pipeName,
            response,
            TestContext.Current.CancellationToken,
            "modify_actor",
            "\"translation\":{\"x\":10,\"y\":20,\"z\":30}"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        var transform = new FlaxActorTransform(
            new FlaxVector3(10, 20, 30),
            new FlaxQuaternion(0, 0, 0, 1),
            new FlaxVector3(1, 1, 1)
        );

        var details = await client.ModifyActorAsync(
            "actor-id",
            transform,
            TestContext.Current.CancellationToken
        );
        await serverTask;

        Assert.Equal(30, details.Transform.Translation.Z);
    }

    [Theory]
    [InlineData(true, "create_actor", "\"actorType\":\"EmptyActor\"")]
    [InlineData(false, "duplicate_actor", "\"actorId\":\"source-id\"")]
    public async Task ActorCreationMethods_SendTypedRequests(
        bool create,
        string method,
        string expectedRequestText)
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var response =
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"id\":\"new-id\",\"typeName\":\"FlaxEngine.EmptyActor\",\"name\":\"Room\",\"parentId\":null,\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"localTransform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"scripts\":[]}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            method, expectedRequestText);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        var transform = new FlaxActorTransform(new FlaxVector3(1, 2, 3),
            new FlaxQuaternion(0, 0, 0, 1), new FlaxVector3(1, 1, 1));

        var details = create
            ? await client.CreateActorAsync("EmptyActor", "Room", "scene-id", null, transform,
                TestContext.Current.CancellationToken)
            : await client.DuplicateActorAsync("source-id", "Room", "scene-id", null, transform,
                TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("new-id", details.Id);
    }

    [Theory]
    [InlineData(true, "rename_actor", "\"name\":\"Wall\"")]
    [InlineData(false, "reparent_actor", "\"preserveWorldTransform\":true")]
    public async Task ActorHierarchyMethods_SendTypedRequests(
        bool rename,
        string method,
        string expectedRequestText)
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var response =
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.EmptyActor\",\"name\":\"Wall\",\"parentId\":\"parent-id\",\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"localTransform\":{\"translation\":{\"x\":1,\"y\":2,\"z\":3},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"scripts\":[]}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            method, expectedRequestText);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var details = rename
            ? await client.RenameActorAsync("actor-id", "Wall", TestContext.Current.CancellationToken)
            : await client.ReparentActorAsync("actor-id", null, "parent-id", true,
                TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("actor-id", details.Id);
    }

    [Fact]
    public async Task DeleteActorAsync_SendsExplicitScopeAndReturnsDeletedIds()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"actorId\":\"actor-id\",\"deletedDescendants\":true,\"deletedActorIds\":[\"actor-id\",\"child-id\"]}}",
            TestContext.Current.CancellationToken,
            "delete_actor",
            "\"deleteDescendants\":true"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.DeleteActorAsync(
            "actor-id", true, TestContext.Current.CancellationToken);
        await serverTask;

        Assert.True(result.DeletedDescendants);
        Assert.Equal(["actor-id", "child-id"], result.DeletedActorIds);
    }

    [Theory]
    [InlineData(true, "static_model_details", null)]
    [InlineData(false, "set_static_model", "\"modelId\":\"model-id\"")]
    public async Task StaticModelMethods_SendTypedRequests(
        bool readOnly, string method, string? expectedRequestText)
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string modelId = "b43f0f8f4aaba3f3156896a5a22ba493";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"actor\":{{\"mainThreadId\":7,\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.StaticModel\",\"name\":\"Wall\",\"parentId\":null,\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{{\"translation\":{{\"x\":0,\"y\":0,\"z\":0}},\"orientation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}},\"scale\":{{\"x\":1,\"y\":1,\"z\":1}}}},\"localTransform\":{{\"translation\":{{\"x\":0,\"y\":0,\"z\":0}},\"orientation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}},\"scale\":{{\"x\":1,\"y\":1,\"z\":1}}}},\"scripts\":[]}},\"modelId\":\"{modelId}\",\"modelPath\":\"Content/Cube.flax\",\"modelIsLoaded\":true}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            method, expectedRequestText);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = readOnly
            ? await client.GetStaticModelDetailsAsync("actor-id", TestContext.Current.CancellationToken)
            : await client.SetStaticModelAsync("actor-id", "model-id", TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(modelId, result.ModelId);
        Assert.Equal("actor-id", result.Actor.Id);
    }

    [Fact]
    public async Task SetStaticModelMaterialAsync_SendsTypedRequest()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string materialId = "c5bf75264ac08f205d16eea554e3d16e";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"actor\":{{\"mainThreadId\":7,\"id\":\"actor-id\",\"typeName\":\"FlaxEngine.StaticModel\",\"name\":\"Wall\",\"parentId\":null,\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{{\"translation\":{{\"x\":0,\"y\":0,\"z\":0}},\"orientation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}},\"scale\":{{\"x\":1,\"y\":1,\"z\":1}}}},\"localTransform\":{{\"translation\":{{\"x\":0,\"y\":0,\"z\":0}},\"orientation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}},\"scale\":{{\"x\":1,\"y\":1,\"z\":1}}}},\"scripts\":[]}},\"slotIndex\":2,\"materialId\":\"{materialId}\",\"materialPath\":\"Content/Materials/Wall.flax\"}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            "set_static_model_material", $"\"slotIndex\":2,\"materialId\":\"{materialId}\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.SetStaticModelMaterialAsync(
            "actor-id", 2, materialId, TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(2, result.SlotIndex);
        Assert.Equal(materialId, result.MaterialId);
        Assert.Equal("actor-id", result.Actor.Id);
    }

    [Fact]
    public async Task CreateMaterialAsync_SendsTypedRequest()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string materialId = "30ab0a49f20d4ef7ad5252ea595ef3b0";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"materialId\":\"{materialId}\",\"materialPath\":\"Content/Materials/Test.flax\",\"baseColor\":{{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":1}},\"roughness\":0.8,\"metallic\":0.1,\"emissiveColor\":null,\"baseColorTextureId\":null,\"normalTextureId\":null,\"uvTiling\":null,\"parameters\":[]}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            "create_material", "\"relativePath\":\"Materials/Test.flax\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.CreateMaterialAsync(
            "Materials/Test.flax", new FlaxColor(0.1, 0.2, 0.3), 0.8, 0.1,
            null, null, null, null, TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(materialId, result.MaterialId);
        Assert.Equal(0.8, result.Roughness);
    }

    [Fact]
    public async Task GetMaterialDetailsAsync_SendsTypedRequest()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string materialId = "30ab0a49f20d4ef7ad5252ea595ef3b0";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"materialId\":\"{materialId}\",\"materialPath\":\"Content/Materials/Test.flax\",\"baseColor\":{{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":1}},\"roughness\":0.8,\"metallic\":0.1,\"emissiveColor\":null,\"baseColorTextureId\":null,\"normalTextureId\":null,\"uvTiling\":null,\"parameters\":[]}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            "material_details", $"\"materialId\":\"{materialId}\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.GetMaterialDetailsAsync(materialId, TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("Content/Materials/Test.flax", result.MaterialPath);
        Assert.Equal(new FlaxColor(0.1, 0.2, 0.3), result.BaseColor);
    }

    [Fact]
    public async Task CreateMaterialInstanceAsync_SendsTypedRequest()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string instanceId = "30ab0a49f20d4ef7ad5252ea595ef3b0";
        const string baseId = "c5bf75264ac08f205d16eea554e3d16e";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"materialInstanceId\":\"{instanceId}\",\"materialInstancePath\":\"Content/Materials/Test Instance.flax\",\"baseMaterialId\":\"{baseId}\",\"baseMaterialPath\":\"Content/Materials/Test.flax\",\"parameters\":[{{\"name\":\"Tint\",\"type\":\"Color\",\"isOverride\":true,\"value\":{{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":1}}}}]}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            "create_material_instance", $"\"baseMaterialId\":\"{baseId}\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        using var value = JsonDocument.Parse("""{"r":0.1,"g":0.2,"b":0.3,"a":1}""");

        var result = await client.CreateMaterialInstanceAsync(
            "Materials/Test Instance.flax", baseId,
            new Dictionary<string, JsonElement> { ["Tint"] = value.RootElement.Clone() },
            TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(instanceId, result.MaterialInstanceId);
        Assert.Equal(baseId, result.BaseMaterialId);
        Assert.True(Assert.Single(result.Parameters).IsOverride);
    }

    [Fact]
    public async Task SetMaterialInstanceParameterAsync_SendsTypedRequest()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        const string instanceId = "30ab0a49f20d4ef7ad5252ea595ef3b0";
        const string baseId = "c5bf75264ac08f205d16eea554e3d16e";
        var response =
            $"{{\"id\":1,\"result\":{{\"mainThreadId\":7,\"materialInstanceId\":\"{instanceId}\",\"materialInstancePath\":\"Content/Materials/Test Instance.flax\",\"baseMaterialId\":\"{baseId}\",\"baseMaterialPath\":\"Content/Materials/Test.flax\",\"parameters\":[{{\"name\":\"Roughness\",\"type\":\"Float\",\"isOverride\":true,\"value\":0.4}}]}}}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            "set_material_instance_parameter", "\"parameterName\":\"Roughness\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        using var value = JsonDocument.Parse("0.4");

        var result = await client.SetMaterialInstanceParameterAsync(
            instanceId, "Roughness", value.RootElement.Clone(), TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(0.4, Assert.Single(result.Parameters).Value.GetDouble());
    }

    [Theory]
    [InlineData("box_collider_details", false)]
    [InlineData("create_box_collider", true)]
    [InlineData("set_box_collider", true)]
    public async Task BoxColliderMethods_SendTypedRequests(string method, bool writesProperties)
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var response =
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"actor\":{\"mainThreadId\":7,\"id\":\"collider-id\",\"typeName\":\"FlaxEngine.BoxCollider\",\"name\":\"Collision\",\"parentId\":\"parent-id\",\"sceneId\":\"scene-id\",\"isActive\":true,\"isActiveInHierarchy\":true,\"layer\":0,\"layerName\":\"Default\",\"tags\":[],\"transform\":{\"translation\":{\"x\":0,\"y\":0,\"z\":0},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"localTransform\":{\"translation\":{\"x\":0,\"y\":0,\"z\":0},\"orientation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},\"scripts\":[]},\"size\":{\"x\":100,\"y\":20,\"z\":50},\"center\":{\"x\":0,\"y\":10,\"z\":0},\"isTrigger\":true}}";
        var serverTask = ServeResponseAsync(pipeName, response, TestContext.Current.CancellationToken,
            method, writesProperties ? "\"isTrigger\":true" : "\"actorId\":\"collider-id\"");
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);
        var size = new FlaxVector3(100, 20, 50);
        var center = new FlaxVector3(0, 10, 0);

        var result = method switch
        {
            "box_collider_details" => await client.GetBoxColliderDetailsAsync(
                "collider-id", TestContext.Current.CancellationToken),
            "create_box_collider" => await client.CreateBoxColliderAsync(
                "parent-id", "Collision", size, center, true, TestContext.Current.CancellationToken),
            _ => await client.SetBoxColliderAsync(
                "collider-id", size, center, true, TestContext.Current.CancellationToken),
        };
        await serverTask;

        Assert.Equal(size, result.Size);
        Assert.Equal(center, result.Center);
        Assert.True(result.IsTrigger);
    }

    [Fact]
    public async Task SaveAsync_ReturnsTypedSaveResult()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"saved\":true,\"savedScenes\":2}}",
            TestContext.Current.CancellationToken,
            "save"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.SaveAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(new FlaxEditorSaveResult(7, true, 2), result);
    }

    [Fact]
    public async Task SetPlayModeAsync_SendsActionAndReturnsTypedResult()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"pause\",\"isPlayMode\":true,\"isPaused\":true}}",
            TestContext.Current.CancellationToken,
            "play_mode",
            "\"action\":\"pause\""
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.SetPlayModeAsync("pause", TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(new FlaxEditorPlayModeResult(7, "pause", true, true), result);
    }

    [Fact]
    public async Task ExecuteCSharpAsync_SendsCodeAndReturnsTypedResult()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"mainThreadId\":7,\"typeName\":\"System.Int32\",\"result\":42}}",
            TestContext.Current.CancellationToken,
            "execute_csharp",
            "\"code\":\"return 42;\""
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.ExecuteCSharpAsync("return 42;", TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(7, result.MainThreadId);
        Assert.Equal("System.Int32", result.TypeName);
        Assert.Equal(42, result.Result?.GetInt32());
    }

    [Fact]
    public async Task SetPlayModeAsync_PollsUntilTheEditorReachesTheState()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeSequenceAsync(
            pipeName,
            TestContext.Current.CancellationToken,
            ("play_mode", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"start\",\"isPlayMode\":false,\"isPaused\":false}}"),
            ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":false,\"isPaused\":false}}"),
            ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":true,\"isPaused\":false}}")
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.SetPlayModeAsync("Start", TestContext.Current.CancellationToken);
        await serverTask;

        Assert.True(result.IsPlayMode);
        Assert.Equal("start", result.RequestedAction);
    }

    [Fact]
    public async Task SetPlayModeAsync_WithUnknownAction_ThrowsClientVisibleError()
    {
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<McpException>(()
            => client.SetPlayModeAsync("jump", TestContext.Current.CancellationToken));

        Assert.Contains("start, stop, pause, or resume", exception.Message);
    }

    [Fact]
    public async Task CompileAsync_WhenTheBridgeReloads_ReportsReloaded()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var serverTask = ServeSequenceAsync(
            pipeName,
            TestContext.Current.CancellationToken,
            ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":false,\"isPaused\":false}}"),
            ("compile", "{\"id\":1,\"result\":{\"isCompiling\":true,\"lastCompilationFailed\":false,\"compilationsCount\":4,\"loadedUtcTicks\":100}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":true,\"lastCompilationFailed\":false,\"compilationsCount\":4,\"loadedUtcTicks\":100}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}")
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.CompileAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("reloaded", result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Reloads);
    }

    [Fact]
    public async Task CompileAsync_WhenTheEditorCompilesAgainOnItsOwn_WaitsForTheSecondReload()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var settled300 = "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":6,\"loadedUtcTicks\":300}}";
        var serverTask = ServeSequenceAsync(
            pipeName,
            TestContext.Current.CancellationToken,
            ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":false,\"isPaused\":false}}"),
            ("compile", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":4,\"loadedUtcTicks\":100}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200,\"isSourceDirty\":true}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":true,\"lastCompilationFailed\":false,\"compilationsCount\":6,\"loadedUtcTicks\":200}}"),
            ("compile_status", settled300),
            ("compile_status", settled300),
            ("compile_status", settled300)
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.CompileAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("reloaded", result.Outcome);
        Assert.Equal(2, result.Reloads);
    }

    [Fact]
    public async Task CompileAsync_WhenTheCompileFails_ReportsTheLogsErrors()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var logs = Path.Combine(_projectFolder, "Logs");
        Directory.CreateDirectory(logs);
        var log = Path.Combine(logs, "Log_2026_10_14_10_00_00.txt");
        File.WriteAllText(log, "[ 00:00:00.500 ]: [Info] an earlier line\n", Encoding.Unicode);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = Task.Run(async () =>
        {
            await ServeSequenceAsync(
                pipeName,
                cancellationToken,
                ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":false,\"isPaused\":false}}"),
                ("compile", "{\"id\":1,\"result\":{\"isCompiling\":true,\"lastCompilationFailed\":false,\"compilationsCount\":4,\"loadedUtcTicks\":100}}")
            );
            File.AppendAllText(
                log,
                "[ 00:00:01.000 ]: [Info] C:\\Game\\Source\\A.cs(3,9,3,10): error CS1525: Invalid expression term ';'\n" +
                "[ 00:00:01.100 ]: [Error] Failed to run build tool, result: 1\n",
                Encoding.Unicode
            );
            await ServeSequenceAsync(
                pipeName,
                cancellationToken,
                ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":true,\"compilationsCount\":5,\"loadedUtcTicks\":100}}")
            );
        }, cancellationToken);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.CompileAsync(TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal("failed", result.Outcome);
        Assert.Contains(result.Errors, error => error.Code == "CS1525");
        Assert.Contains(result.BuildErrors, line => line.Contains("Failed to run build tool"));
    }

    [Fact]
    public async Task Query_InterruptedByAReload_IsSentAgain()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = ServeSequenceAsync(
            pipeName,
            cancellationToken,
            ("get_selection", "{\"id\":1,\"error\":{\"code\":\"interrupted_by_reload\",\"message\":\"reloaded\"}}"),
            ("get_selection", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"selected\":[]}}")
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var selection = await client.GetSelectionAsync(cancellationToken);
        await serverTask;

        Assert.Equal(7, selection.MainThreadId);
    }

    [Fact]
    public async Task Command_InterruptedByAReload_IsNotSentAgain()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = ServeSequenceAsync(
            pipeName,
            cancellationToken,
            ("execute_csharp", "{\"id\":1,\"error\":{\"code\":\"interrupted_by_reload\",\"message\":\"effects unknown\"}}")
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<FlaxBridgeRequestException>(() =>
            client.ExecuteCSharpAsync("return 1;", cancellationToken));
        await serverTask;

        Assert.Equal("interrupted_by_reload", exception.Code);
        Assert.Contains("effects unknown", exception.Message);
    }

    [Fact]
    public async Task CompileAsync_WhenPollingMeetsTheReload_ReportsReloaded()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = ServeSequenceAsync(
            pipeName,
            cancellationToken,
            ("play_mode_state", "{\"id\":1,\"result\":{\"mainThreadId\":7,\"requestedAction\":\"state\",\"isPlayMode\":false,\"isPaused\":false}}"),
            ("compile", "{\"id\":1,\"result\":{\"isCompiling\":true,\"lastCompilationFailed\":false,\"compilationsCount\":4,\"loadedUtcTicks\":100}}"),
            ("compile_status", "{\"id\":1,\"error\":{\"code\":\"plugin_not_attached\",\"message\":\"reloading\"}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}"),
            ("compile_status", "{\"id\":1,\"result\":{\"isCompiling\":false,\"lastCompilationFailed\":false,\"compilationsCount\":5,\"loadedUtcTicks\":200}}")
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var result = await client.CompileAsync(cancellationToken);
        await serverTask;

        Assert.Equal("reloaded", result.Outcome);
    }

    [Fact]
    public async Task PingAsync_WhenTheEditorProcessIsGone_SaysSo()
    {
        WriteHandshake("FlaxMcpTests-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow, pid: int.MaxValue);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.PingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("is not running", exception.Message);
    }

    [Fact]
    public async Task GetStatusAsync_WhenTheEditorProcessIsGone_ReportsDisconnectedWithTheReason()
    {
        WriteHandshake("FlaxMcpTests-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow, pid: int.MaxValue);
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Connected);
        Assert.Contains("is not running", status.Error);
    }

    [Fact]
    public async Task GetHostStatusAsync_SendsCollect()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"hostVersion\":\"abc\",\"hostStartedUtc\":\"2026-10-14T10:00:00Z\",\"generation\":5," +
            "\"attached\":true,\"accepting\":true,\"acceptLoopFault\":null,\"attachCount\":3,\"detachCount\":2," +
            "\"pluginContextsAlive\":1,\"pluginContextsSeen\":3,\"collected\":true,\"privateBytes\":10,\"managedBytes\":5," +
            "\"gen0Collections\":1,\"gen1Collections\":1,\"gen2Collections\":1}}",
            cancellationToken,
            "host_status",
            "\"collect\":true"
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var status = await client.GetHostStatusAsync(true, cancellationToken);
        await serverTask;

        Assert.Equal("abc", status.HostVersion);
        Assert.Equal(3, status.AttachCount);
        Assert.Equal(1, status.PluginContextsAlive);
    }

    [Fact]
    public async Task RunGameCommandAsync_WithAnUnknownName_ReportsUnknownCommand()
    {
        var pipeName = "FlaxMcpTests-" + Guid.NewGuid().ToString("N");
        WriteHandshake(pipeName, DateTime.UtcNow);
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverTask = ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"error\":{\"code\":\"unknown_command\",\"message\":\"Registered: status.\"}}",
            cancellationToken,
            "game_command",
            "\"name\":\"stauts\""
        );
        var client = new FlaxBridgeClient(_projectFolder, _tempDir);

        var exception = await Assert.ThrowsAsync<FlaxBridgeRequestException>(() =>
            client.RunGameCommandAsync("stauts", "", cancellationToken));
        await serverTask;

        Assert.Equal("unknown_command", exception.Code);
        Assert.Contains("Registered: status.", exception.Message);
    }

    private static async Task ServeSequenceAsync(
        string pipeName,
        CancellationToken cancellationToken,
        params (string Method, string Response)[] steps)
    {
        // The next instance is created before the current one closes: on Unix a pipe is a socket whose listener
        // goes away with its last instance, and a client reconnecting at once would be reset.
        var pipe = CreateSequencePipe(pipeName);
        for (var i = 0; i < steps.Length; i++)
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            var next = i + 1 < steps.Length ? CreateSequencePipe(pipeName) : null;
            await using (pipe)
            {
                await AnswerAsync(pipe, steps[i].Method, steps[i].Response, null, cancellationToken);
            }

            pipe = next!;
        }
    }

    private static NamedPipeServerStream CreateSequencePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

    private async Task ServePingAsync(string pipeName, CancellationToken cancellationToken)
    {
        await ServeResponseAsync(
            pipeName,
            "{\"id\":1,\"result\":{\"pong\":true,\"utcNow\":\"2026-08-20T10:00:01Z\"}}",
            cancellationToken
        );
    }

    private static async Task ServeResponseAsync(
        string pipeName,
        string response,
        CancellationToken cancellationToken,
        string method = "ping",
        string? expectedRequestText = null)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );
        await pipe.WaitForConnectionAsync(cancellationToken);
        await AnswerAsync(pipe, method, response, expectedRequestText, cancellationToken);
    }

    private static async Task AnswerAsync(
        NamedPipeServerStream pipe,
        string method,
        string response,
        string? expectedRequestText,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            false,
            4096,
            leaveOpen: true
        );
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        var request = await reader.ReadLineAsync(cancellationToken);
        Assert.Contains($"\"method\":\"{method}\"", request);
        if (expectedRequestText is not null)
        {
            Assert.Contains(expectedRequestText, request);
        }

        await writer.WriteLineAsync(response);
    }

    private static async Task ServeDisconnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );
        await pipe.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            false,
            4096,
            leaveOpen: true
        );
        await reader.ReadLineAsync(cancellationToken);
    }

    private void WriteHandshake(string pipeName, DateTime startedUtc,
        int protocolVersion = FlaxBridgeClient.CurrentProtocolVersion, int? pid = null)
    {
        var handshakePath = Path.Combine(_tempDir, ProjectHash(_projectFolder) + ".json");
        File.WriteAllText(
            handshakePath,
            JsonSerializer.Serialize(
                new
                {
                    pipeName,
                    protocolVersion,
                    pid = pid ?? Environment.ProcessId,
                    pluginVersion = "1.2.3",
                    engineBuild = 12000,
                    startedUtc,
                }
            )
        );
    }

    private static string ProjectHash(string projectFolder)
    {
        var normalized = Path.GetFullPath(projectFolder).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16];
    }
}
