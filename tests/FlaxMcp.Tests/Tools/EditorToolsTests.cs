using System.Text.Json;
using FlaxMcp.Flax;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public sealed class EditorToolsTests
{
    [Fact]
    public async Task GetSceneGraphAsync_ReturnsBridgeResult()
    {
        var expected = new FlaxLiveSceneGraph(
            12,
            [new FlaxLiveSceneNode("scene-id", "FlaxEngine.Scene", "Main", [])],
            false
        );
        var tool = new EditorTools(new StubBridgeClient(expected), new FlaxJobManager());

        var result = await tool.GetSceneGraphAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetSelectionAsync_ReturnsBridgeResult()
    {
        var expected = new FlaxEditorSelection(
            12,
            [new FlaxEditorSelectionItem("actor-id", "FlaxEngine.Actor", "Player")]
        );
        var bridge = new StubBridgeClient();
        bridge.Selection = expected;
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.GetSelectionAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task SetSelectionAsync_ForwardsActorIds()
    {
        var expected = new FlaxEditorSelection(12, []);
        var bridge = new StubBridgeClient { Selection = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.SetSelectionAsync(["actor-id"], TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(["actor-id"], bridge.ActorIds);
    }

    [Fact]
    public async Task GetActorDetailsAsync_ForwardsActorId()
    {
        var expected = CreateActorDetails();
        var bridge = new StubBridgeClient { ActorDetails = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.GetActorDetailsAsync("actor-id", TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal("actor-id", bridge.ActorId);
    }

    [Fact]
    public async Task ModifyActorAsync_ForwardsActorIdAndTransform()
    {
        var expected = CreateActorDetails();
        var bridge = new StubBridgeClient { ActorDetails = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var translation = new FlaxVector3(10, 20, 30);
        var orientation = new FlaxQuaternion(0, 0, 0, 1);
        var scale = new FlaxVector3(1, 2, 3);

        var result = await tool.ModifyActorAsync(
            "actor-id",
            translation,
            orientation,
            scale,
            TestContext.Current.CancellationToken
        );

        Assert.Same(expected, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Equal(new FlaxActorTransform(translation, orientation, scale), bridge.Transform);
    }

    [Fact]
    public async Task CreateActorAsync_ForwardsCreationRequest()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var translation = new FlaxVector3(10, 20, 30);
        var orientation = new FlaxQuaternion(0, 0, 0, 1);
        var scale = new FlaxVector3(1, 2, 3);

        var result = await tool.CreateActorAsync("EmptyActor", "Room", "scene-id", null,
            translation, orientation, scale, TestContext.Current.CancellationToken);

        Assert.Same(bridge.ActorDetails, result);
        Assert.Equal("EmptyActor", bridge.ActorType);
        Assert.Equal("Room", bridge.ActorName);
        Assert.Equal("scene-id", bridge.SceneId);
        Assert.Null(bridge.ParentId);
        Assert.Equal(new FlaxActorTransform(translation, orientation, scale), bridge.Transform);
    }

    [Fact]
    public async Task DuplicateActorAsync_ForwardsDuplicationRequest()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var translation = new FlaxVector3(10, 20, 30);
        var orientation = new FlaxQuaternion(0, 0, 0, 1);
        var scale = new FlaxVector3(1, 1, 1);

        var result = await tool.DuplicateActorAsync("source-id", "Copy", null, "parent-id",
            translation, orientation, scale, TestContext.Current.CancellationToken);

        Assert.Same(bridge.ActorDetails, result);
        Assert.Equal("source-id", bridge.ActorId);
        Assert.Equal("Copy", bridge.ActorName);
        Assert.Null(bridge.SceneId);
        Assert.Equal("parent-id", bridge.ParentId);
    }

    [Fact]
    public async Task RenameActorAsync_ForwardsRenameRequest()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.RenameActorAsync(
            "actor-id", "Wall", TestContext.Current.CancellationToken);

        Assert.Same(bridge.ActorDetails, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Equal("Wall", bridge.ActorName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReparentActorAsync_ForwardsDestinationAndTransformPolicy(bool preserveWorldTransform)
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.ReparentActorAsync(
            "actor-id", null, "parent-id", preserveWorldTransform,
            TestContext.Current.CancellationToken);

        Assert.Same(bridge.ActorDetails, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Null(bridge.SceneId);
        Assert.Equal("parent-id", bridge.ParentId);
        Assert.Equal(preserveWorldTransform, bridge.PreserveWorldTransform);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteActorAsync_ForwardsExplicitDescendantPolicy(bool deleteDescendants)
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.DeleteActorAsync(
            "actor-id", deleteDescendants, TestContext.Current.CancellationToken);

        Assert.Same(bridge.DeletionResult, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Equal(deleteDescendants, bridge.DeleteDescendants);
    }

    [Fact]
    public async Task GetStaticModelDetailsAsync_ForwardsActorId()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.GetStaticModelDetailsAsync(
            "actor-id", TestContext.Current.CancellationToken);

        Assert.Same(bridge.StaticModelDetails, result);
        Assert.Equal("actor-id", bridge.ActorId);
    }

    [Fact]
    public async Task SetStaticModelAsync_ForwardsTypedModelRequest()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.SetStaticModelAsync(
            "actor-id", "model-id", TestContext.Current.CancellationToken);

        Assert.Same(bridge.StaticModelDetails, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Equal("model-id", bridge.ModelId);
    }

    [Fact]
    public async Task SetStaticModelMaterialAsync_ForwardsTypedMaterialRequest()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.SetStaticModelMaterialAsync(
            "actor-id", 2, "material-id", TestContext.Current.CancellationToken);

        Assert.Same(bridge.StaticModelMaterialDetails, result);
        Assert.Equal("actor-id", bridge.ActorId);
        Assert.Equal(2, bridge.SlotIndex);
        Assert.Equal("material-id", bridge.MaterialId);
    }

    [Fact]
    public async Task CreateBoxColliderAsync_ForwardsTypedProperties()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var size = new FlaxVector3(100, 20, 50);
        var center = new FlaxVector3(0, 10, 0);

        var result = await tool.CreateBoxColliderAsync(
            "parent-id", "Collision", size, center, true, TestContext.Current.CancellationToken);

        Assert.Same(bridge.BoxColliderDetails, result);
        Assert.Equal("parent-id", bridge.ParentId);
        Assert.Equal("Collision", bridge.ActorName);
        Assert.Equal(size, bridge.ColliderSize);
        Assert.Equal(center, bridge.ColliderCenter);
        Assert.True(bridge.IsTrigger);
    }

    [Fact]
    public async Task SetBoxColliderAsync_ForwardsTypedProperties()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var size = new FlaxVector3(80, 30, 40);
        var center = new FlaxVector3(1, 2, 3);

        var result = await tool.SetBoxColliderAsync(
            "collider-id", size, center, false, TestContext.Current.CancellationToken);

        Assert.Same(bridge.BoxColliderDetails, result);
        Assert.Equal("collider-id", bridge.ActorId);
        Assert.Equal(size, bridge.ColliderSize);
        Assert.Equal(center, bridge.ColliderCenter);
        Assert.False(bridge.IsTrigger);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, double.NaN)]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(false, double.NaN)]
    [InlineData(false, double.NegativeInfinity)]
    public async Task BoxColliderMutation_WithInvalidVector_RejectsBeforeBridge(
        bool invalidSize, double value)
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var size = new FlaxVector3(invalidSize ? value : 1, 1, 1);
        var center = new FlaxVector3(invalidSize ? 0 : value, 0, 0);

        Task Action() => invalidSize
            ? tool.CreateBoxColliderAsync(
                "parent-id", "Collision", size, center, false, TestContext.Current.CancellationToken)
            : tool.SetBoxColliderAsync(
                "collider-id", size, center, false, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(Action);
        Assert.Null(bridge.ParentId);
        Assert.Null(bridge.ActorId);
    }

    [Fact]
    public async Task CreateBoxColliderAsync_WithMissingParent_PropagatesBridgeErrorWithoutResult()
    {
        var expected = new KeyNotFoundException("Parent actor 'missing' is not loaded in the editor.");
        var bridge = new StubBridgeClient { CreateBoxColliderException = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => tool.CreateBoxColliderAsync(
            "missing", "Collision", new FlaxVector3(1, 1, 1), new FlaxVector3(0, 0, 0), false,
            TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Null(bridge.ParentId);
    }

    [Fact]
    public async Task SaveAsync_ReturnsBridgeResult()
    {
        var expected = new FlaxEditorSaveResult(12, true, 1);
        var bridge = new StubBridgeClient { SaveResult = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task SetPlayModeAsync_ForwardsAction()
    {
        var expected = new FlaxEditorPlayModeResult(12, "pause", true, true);
        var bridge = new StubBridgeClient { PlayModeResult = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.SetPlayModeAsync("pause", null, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal("pause", bridge.PlayModeAction);
    }

    [Fact]
    public async Task CaptureScreenshotAsync_ForwardsResolvedPngPath()
    {
        var bridge = new StubBridgeClient();
        var tool = new EditorTools(bridge, new FlaxJobManager());
        var path = Path.Combine(Path.GetTempPath(), "viewport.png");

        var result = await tool.CaptureScreenshotAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(path), bridge.ScreenshotPath);
        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal(123, result.Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("viewport.jpg")]
    public async Task CaptureScreenshotAsync_WithInvalidPath_Throws(string path)
    {
        var tool = new EditorTools(new StubBridgeClient(), new FlaxJobManager());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.CaptureScreenshotAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureScreenshotAsync_WithMissingDirectory_Throws()
    {
        var tool = new EditorTools(new StubBridgeClient(), new FlaxJobManager());
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "viewport.png");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            tool.CaptureScreenshotAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_ReturnsBridgeResult()
    {
        var expected = new FlaxEditorCompileResult("reloaded", 1.2, [], [], 1);
        var bridge = new StubBridgeClient { CompileResult = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.CompileAsync(null, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetHostStatusAsync_ForwardsCollect()
    {
        var client = new StubBridgeClient();
        var tool = new EditorTools(client, new FlaxJobManager());

        var result = await tool.GetHostStatusAsync(true, TestContext.Current.CancellationToken);

        Assert.Same(client.HostStatus, result);
        Assert.True(client.HostStatusCollect);
    }

    [Fact]
    public async Task OpenSceneAsync_ForwardsPath()
    {
        var expected = new FlaxEditorOpenSceneResult(12, "abc", "Main", ["Main"]);
        var bridge = new StubBridgeClient { OpenSceneResult = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.OpenSceneAsync("Scenes/Main.scene", TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal("Scenes/Main.scene", bridge.ScenePath);
    }

    [Fact]
    public async Task ExecuteCSharpAsync_ForwardsCode()
    {
        var expected = new FlaxCodeExecutionResult(12, "System.Int32", null);
        var bridge = new StubBridgeClient { CodeExecutionResult = expected };
        var tool = new EditorTools(bridge, new FlaxJobManager());

        var result = await tool.ExecuteCSharpAsync("return 42;", TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal("return 42;", bridge.Code);
    }

    private static FlaxActorDetails CreateActorDetails()
    {
        var transform = new FlaxActorTransform(
            new FlaxVector3(1, 2, 3),
            new FlaxQuaternion(0, 0, 0, 1),
            new FlaxVector3(1, 1, 1)
        );
        return new FlaxActorDetails(
            12,
            "actor-id",
            "FlaxEngine.Actor",
            "Player",
            null,
            "scene-id",
            true,
            true,
            0,
            "Default",
            ["Player"],
            transform,
            transform,
            []
        );
    }

    internal sealed class StubBridgeClient : IFlaxBridgeClient
    {
        private readonly FlaxLiveSceneGraph _sceneGraph;

        public FlaxEditorSelection Selection { get; set; } = new(0, []);

        public IReadOnlyList<string>? ActorIds { get; private set; }

        public FlaxActorDetails ActorDetails { get; set; } = CreateActorDetails();

        public string? ActorId { get; private set; }

        public string? ActorType { get; private set; }

        public string? ActorName { get; private set; }

        public string? SceneId { get; private set; }

        public string? ParentId { get; private set; }

        public FlaxActorTransform? Transform { get; private set; }

        public bool? PreserveWorldTransform { get; private set; }

        public bool? DeleteDescendants { get; private set; }

        public FlaxActorDeletionResult DeletionResult { get; set; } =
            new(0, "actor-id", false, ["actor-id"]);

        public string? ModelId { get; private set; }

        public FlaxStaticModelDetails StaticModelDetails { get; set; } =
            new(0, CreateActorDetails(), null, null, false);

        public int? SlotIndex { get; private set; }

        public string? MaterialId { get; private set; }

        public FlaxStaticModelMaterialDetails StaticModelMaterialDetails { get; set; } =
            new(0, CreateActorDetails(), 0, "material-id", "Content/Materials/Wall.flax");

        public FlaxVector3? ColliderSize { get; private set; }

        public FlaxVector3? ColliderCenter { get; private set; }

        public bool? IsTrigger { get; private set; }

        public FlaxBoxColliderDetails BoxColliderDetails { get; set; } =
            new(0, CreateActorDetails(), new FlaxVector3(1, 1, 1), new FlaxVector3(0, 0, 0), false);

        public Exception? CreateBoxColliderException { get; set; }

        public FlaxEditorSaveResult SaveResult { get; set; } = new(0, true, 0);

        public FlaxEditorPlayModeResult PlayModeResult { get; set; } = new(0, "stop", false, false);

        public string? PlayModeAction { get; private set; }

        public string? ScreenshotPath { get; private set; }

        public FlaxCodeExecutionResult CodeExecutionResult { get; set; } = new(0, null, null);

        public string? Code { get; private set; }

        public StubBridgeClient()
            : this(new FlaxLiveSceneGraph(0, [], false))
        {
        }

        public StubBridgeClient(FlaxLiveSceneGraph sceneGraph)
        {
            _sceneGraph = sceneGraph;
        }

        public Task<FlaxBridgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxBridgePing> PingAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxLiveSceneGraph> GetSceneGraphAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_sceneGraph);
        }

        public Task<FlaxEditorSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Selection);
        }

        public Task<FlaxEditorSelection> SetSelectionAsync(
            IReadOnlyList<string> actorIds,
            CancellationToken cancellationToken = default)
        {
            ActorIds = actorIds;
            return Task.FromResult(Selection);
        }

        public Task<FlaxBridgeScreenshot> CaptureScreenshotAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            ScreenshotPath = path;
            return Task.FromResult(new FlaxBridgeScreenshot(path, 123));
        }

        public Task<FlaxActorDetails> GetActorDetailsAsync(
            string actorId,
            CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDetails> ModifyActorAsync(
            string actorId,
            FlaxActorTransform transform,
            CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            Transform = transform;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDetails> CreateActorAsync(string actorType, string name, string? sceneId,
            string? parentId, FlaxActorTransform transform, CancellationToken cancellationToken = default)
        {
            ActorType = actorType;
            ActorName = name;
            SceneId = sceneId;
            ParentId = parentId;
            Transform = transform;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDetails> DuplicateActorAsync(string actorId, string name, string? sceneId,
            string? parentId, FlaxActorTransform transform, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            ActorName = name;
            SceneId = sceneId;
            ParentId = parentId;
            Transform = transform;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDetails> RenameActorAsync(string actorId, string name,
            CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            ActorName = name;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDetails> ReparentActorAsync(string actorId, string? sceneId, string? parentId,
            bool preserveWorldTransform, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            SceneId = sceneId;
            ParentId = parentId;
            PreserveWorldTransform = preserveWorldTransform;
            return Task.FromResult(ActorDetails);
        }

        public Task<FlaxActorDeletionResult> DeleteActorAsync(string actorId, bool deleteDescendants,
            CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            DeleteDescendants = deleteDescendants;
            return Task.FromResult(DeletionResult);
        }

        public Task<FlaxStaticModelDetails> GetStaticModelDetailsAsync(
            string actorId, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            return Task.FromResult(StaticModelDetails);
        }

        public Task<FlaxStaticModelDetails> SetStaticModelAsync(
            string actorId, string modelId, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            ModelId = modelId;
            return Task.FromResult(StaticModelDetails);
        }

        public Task<FlaxStaticModelMaterialDetails> SetStaticModelMaterialAsync(
            string actorId, int slotIndex, string materialId, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            SlotIndex = slotIndex;
            MaterialId = materialId;
            return Task.FromResult(StaticModelMaterialDetails);
        }

        public Task<FlaxMaterialDetails> CreateMaterialAsync(
            string relativePath, FlaxColor baseColor, double roughness, double metallic,
            FlaxColor? emissiveColor, string? baseColorTextureId, string? normalTextureId,
            FlaxVector2? uvTiling, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxMaterialDetails> GetMaterialDetailsAsync(
            string materialId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxMaterialInstanceDetails> CreateMaterialInstanceAsync(
            string relativePath, string baseMaterialId, IReadOnlyDictionary<string, JsonElement> parameters,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxMaterialInstanceDetails> SetMaterialInstanceParameterAsync(
            string materialInstanceId, string parameterName, JsonElement value,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxBoxColliderDetails> GetBoxColliderDetailsAsync(
            string actorId, CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            return Task.FromResult(BoxColliderDetails);
        }

        public Task<FlaxBoxColliderDetails> CreateBoxColliderAsync(
            string parentId, string name, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
            CancellationToken cancellationToken = default)
        {
            if (CreateBoxColliderException is not null)
            {
                return Task.FromException<FlaxBoxColliderDetails>(CreateBoxColliderException);
            }
            ParentId = parentId;
            ActorName = name;
            ColliderSize = size;
            ColliderCenter = center;
            IsTrigger = isTrigger;
            return Task.FromResult(BoxColliderDetails);
        }

        public Task<FlaxBoxColliderDetails> SetBoxColliderAsync(
            string actorId, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
            CancellationToken cancellationToken = default)
        {
            ActorId = actorId;
            ColliderSize = size;
            ColliderCenter = center;
            IsTrigger = isTrigger;
            return Task.FromResult(BoxColliderDetails);
        }

        public Task<FlaxEditorSaveResult> SaveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SaveResult);
        }

        public Task<FlaxEditorPlayModeResult> SetPlayModeAsync(
            string action,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            PlayModeAction = action;
            return Task.FromResult(PlayModeResult);
        }

        public Task<FlaxCodeExecutionResult> ExecuteCSharpAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            Code = code;
            return Task.FromResult(CodeExecutionResult);
        }

        public FlaxEditorCompileResult CompileResult { get; set; } = new("unchanged", 0, [], [], 0);

        public FlaxEditorOpenSceneResult OpenSceneResult { get; set; } = new(0, "", "", []);

        public string? ScenePath { get; private set; }

        public Task<FlaxEditorCompileResult> CompileAsync(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
        {
            return Task.FromResult(CompileResult);
        }

        public Task<FlaxEditorOpenSceneResult> OpenSceneAsync(string path, CancellationToken cancellationToken = default)
        {
            ScenePath = path;
            return Task.FromResult(OpenSceneResult);
        }

        public FlaxBridgeHostStatus HostStatus { get; set; } =
            new("v", DateTime.UnixEpoch, 1, true, true, null, 1, 0, 1, 1, false, 0, 0, 0, 0, 0);

        public bool? HostStatusCollect { get; private set; }

        public Task<FlaxBridgeHostStatus> GetHostStatusAsync(bool collect, CancellationToken cancellationToken = default)
        {
            HostStatusCollect = collect;
            return Task.FromResult(HostStatus);
        }
    }
}
