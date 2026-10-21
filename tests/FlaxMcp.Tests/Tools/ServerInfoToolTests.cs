using System.Text.Json;
using FlaxMcp.Flax;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Tools;

public class ServerInfoToolTests
{
    [Fact]
    public void GetServerInfo_ReturnsFlaxMcpName()
    {
        var tool = new ServerInfoTool(new StubBridgeClient(FlaxBridgeStatus.Disconnected));

        var info = tool.GetServerInfo();

        Assert.Equal("FlaxMcp", info.Name);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Fact]
    public async Task GetFlaxStatusAsync_ReturnsBridgeClientStatus()
    {
        var expected = new FlaxBridgeStatus(true, "1.2.3", 12000, DateTime.UtcNow);
        var tool = new ServerInfoTool(new StubBridgeClient(expected));

        var status = await tool.GetFlaxStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, status);
    }

    private sealed class StubBridgeClient(FlaxBridgeStatus status) : IFlaxBridgeClient
    {
        public Task<FlaxBridgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(status);
        }

        public Task<FlaxBridgePing> PingAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxLiveSceneGraph> GetSceneGraphAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorSelection> SetSelectionAsync(
            IReadOnlyList<string> actorIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxBridgeScreenshot> CaptureScreenshotAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> GetActorDetailsAsync(
            string actorId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> ModifyActorAsync(
            string actorId,
            FlaxActorTransform transform,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> CreateActorAsync(string actorType, string name, string? sceneId,
            string? parentId, FlaxActorTransform transform, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> DuplicateActorAsync(string actorId, string name, string? sceneId,
            string? parentId, FlaxActorTransform transform, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> RenameActorAsync(string actorId, string name,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDetails> ReparentActorAsync(string actorId, string? sceneId, string? parentId,
            bool preserveWorldTransform, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxActorDeletionResult> DeleteActorAsync(string actorId, bool deleteDescendants,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxStaticModelDetails> GetStaticModelDetailsAsync(
            string actorId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxStaticModelDetails> SetStaticModelAsync(
            string actorId, string modelId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxStaticModelMaterialDetails> SetStaticModelMaterialAsync(
            string actorId, int slotIndex, string materialId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
            throw new NotSupportedException();
        }

        public Task<FlaxBoxColliderDetails> CreateBoxColliderAsync(
            string parentId, string name, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxBoxColliderDetails> SetBoxColliderAsync(
            string actorId, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorSaveResult> SaveAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorPlayModeResult> SetPlayModeAsync(
            string action,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxCodeExecutionResult> ExecuteCSharpAsync(
            string code,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorCompileResult> CompileAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FlaxEditorOpenSceneResult> OpenSceneAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
