using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaxMcp.Flax.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace FlaxMcp.Flax;

public interface IFlaxBridgeClient
{
    Task<FlaxBridgeStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<FlaxBridgePing> PingAsync(CancellationToken cancellationToken = default);

    Task<FlaxLiveSceneGraph> GetSceneGraphAsync(CancellationToken cancellationToken = default);

    Task<FlaxEditorSelection> GetSelectionAsync(CancellationToken cancellationToken = default);

    Task<FlaxEditorSelection> SetSelectionAsync(
        IReadOnlyList<string> actorIds,
        CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> GetActorDetailsAsync(string actorId, CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> ModifyActorAsync(
        string actorId,
        FlaxActorTransform transform,
        CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> CreateActorAsync(string actorType, string name, string? sceneId, string? parentId,
        FlaxActorTransform transform, CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> DuplicateActorAsync(string actorId, string name, string? sceneId, string? parentId,
        FlaxActorTransform transform, CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> RenameActorAsync(string actorId, string name,
        CancellationToken cancellationToken = default);

    Task<FlaxActorDetails> ReparentActorAsync(string actorId, string? sceneId, string? parentId,
        bool preserveWorldTransform, CancellationToken cancellationToken = default);

    Task<FlaxActorDeletionResult> DeleteActorAsync(string actorId, bool deleteDescendants,
        CancellationToken cancellationToken = default);

    Task<FlaxStaticModelDetails> GetStaticModelDetailsAsync(
        string actorId, CancellationToken cancellationToken = default);

    Task<FlaxStaticModelDetails> SetStaticModelAsync(
        string actorId, string modelId, CancellationToken cancellationToken = default);

    Task<FlaxStaticModelMaterialDetails> SetStaticModelMaterialAsync(
        string actorId, int slotIndex, string materialId, CancellationToken cancellationToken = default);

    Task<FlaxMaterialDetails> CreateMaterialAsync(
        string relativePath, FlaxColor baseColor, double roughness, double metallic,
        FlaxColor? emissiveColor, string? baseColorTextureId, string? normalTextureId,
        FlaxVector2? uvTiling, CancellationToken cancellationToken = default);

    Task<FlaxMaterialDetails> GetMaterialDetailsAsync(
        string materialId, CancellationToken cancellationToken = default);

    Task<FlaxMaterialInstanceDetails> CreateMaterialInstanceAsync(
        string relativePath, string baseMaterialId, IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken = default);

    Task<FlaxMaterialInstanceDetails> SetMaterialInstanceParameterAsync(
        string materialInstanceId, string parameterName, JsonElement value,
        CancellationToken cancellationToken = default);

    Task<FlaxBoxColliderDetails> GetBoxColliderDetailsAsync(
        string actorId, CancellationToken cancellationToken = default);

    Task<FlaxBoxColliderDetails> CreateBoxColliderAsync(
        string parentId, string name, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
        CancellationToken cancellationToken = default);

    Task<FlaxBoxColliderDetails> SetBoxColliderAsync(
        string actorId, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
        CancellationToken cancellationToken = default);

    Task<FlaxEditorSaveResult> SaveAsync(CancellationToken cancellationToken = default);

    Task<FlaxEditorPlayModeResult> SetPlayModeAsync(
        string action,
        CancellationToken cancellationToken = default);

    Task<FlaxBridgeScreenshot> CaptureScreenshotAsync(string path, CancellationToken cancellationToken = default);

    Task<FlaxCodeExecutionResult> ExecuteCSharpAsync(string code, CancellationToken cancellationToken = default);

    Task<FlaxEditorCompileResult> CompileAsync(CancellationToken cancellationToken = default);

    Task<FlaxEditorOpenSceneResult> OpenSceneAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class FlaxBridgeClient : IFlaxBridgeClient
{
    internal const int CurrentProtocolVersion = 15;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlayModeWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompileWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NothingToReloadAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectFolder;
    private readonly string _sessionsDirectory;

    public FlaxBridgeClient(IOptions<Configuration.FlaxMcpOptions> options)
        : this(
            Path.GetDirectoryName(options.Value.ResolveProjectFile())!,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlaxMcp", "sessions")
        )
    {
    }

    internal FlaxBridgeClient(string projectFolder, string sessionsDirectory)
    {
        _projectFolder = Path.GetFullPath(projectFolder);
        _sessionsDirectory = sessionsDirectory;
    }

    public async Task<FlaxBridgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        FlaxBridgeHandshake? handshake = null;
        try
        {
            handshake = ReadHandshake();
            if (handshake is null)
            {
                return FlaxBridgeStatus.Disconnected;
            }

            ValidateProtocol(handshake);
            await PingAsync(handshake, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FlaxBridgeStatus.Disconnected;
        }
        catch (FlaxBridgeProtocolException ex)
        {
            return new FlaxBridgeStatus(
                false,
                handshake?.PluginVersion,
                handshake?.EngineBuild,
                handshake?.StartedUtc,
                ex.Message
            );
        }
        catch (Exception ex) when (ex is IOException
                                       or JsonException
                                       or TimeoutException
                                       or InvalidOperationException
                                       or McpException
                                       or KeyNotFoundException)
        {
            return FlaxBridgeStatus.Disconnected;
        }

        return new FlaxBridgeStatus(
            true,
            handshake.PluginVersion,
            handshake.EngineBuild,
            handshake.StartedUtc,
            null
        );
    }

    public Task<FlaxBridgePing> PingAsync(CancellationToken cancellationToken = default)
    {
        return PingAsync(RequireHandshake(), cancellationToken);
    }

    public Task<FlaxLiveSceneGraph> GetSceneGraphAsync(CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxLiveSceneGraph>(RequireHandshake(), "scene_graph", null, cancellationToken);
    }

    public Task<FlaxEditorSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxEditorSelection>(RequireHandshake(), "get_selection", null, cancellationToken);
    }

    public Task<FlaxEditorSelection> SetSelectionAsync(
        IReadOnlyList<string> actorIds,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxEditorSelection>(
            RequireHandshake(),
            "set_selection",
            new { actorIds },
            cancellationToken
        );
    }

    public Task<FlaxActorDetails> GetActorDetailsAsync(
        string actorId,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(
            RequireHandshake(),
            "actor_details",
            new { actorId },
            cancellationToken
        );
    }

    public Task<FlaxActorDetails> ModifyActorAsync(
        string actorId,
        FlaxActorTransform transform,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(
            RequireHandshake(),
            "modify_actor",
            new { actorId, transform },
            cancellationToken
        );
    }

    public Task<FlaxActorDetails> CreateActorAsync(string actorType, string name, string? sceneId, string? parentId,
        FlaxActorTransform transform, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(RequireHandshake(), "create_actor",
            new { actorType, name, sceneId, parentId, transform }, cancellationToken);
    }

    public Task<FlaxActorDetails> DuplicateActorAsync(string actorId, string name, string? sceneId, string? parentId,
        FlaxActorTransform transform, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(RequireHandshake(), "duplicate_actor",
            new { actorId, name, sceneId, parentId, transform }, cancellationToken);
    }

    public Task<FlaxActorDetails> RenameActorAsync(string actorId, string name,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(RequireHandshake(), "rename_actor",
            new { actorId, name }, cancellationToken);
    }

    public Task<FlaxActorDetails> ReparentActorAsync(string actorId, string? sceneId, string? parentId,
        bool preserveWorldTransform, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDetails>(RequireHandshake(), "reparent_actor",
            new { actorId, sceneId, parentId, preserveWorldTransform }, cancellationToken);
    }

    public Task<FlaxActorDeletionResult> DeleteActorAsync(string actorId, bool deleteDescendants,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxActorDeletionResult>(RequireHandshake(), "delete_actor",
            new { actorId, deleteDescendants }, cancellationToken);
    }

    public Task<FlaxStaticModelDetails> GetStaticModelDetailsAsync(
        string actorId, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxStaticModelDetails>(RequireHandshake(), "static_model_details",
            new { actorId }, cancellationToken);
    }

    public Task<FlaxStaticModelDetails> SetStaticModelAsync(
        string actorId, string modelId, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxStaticModelDetails>(RequireHandshake(), "set_static_model",
            new { actorId, modelId }, cancellationToken);
    }

    public Task<FlaxStaticModelMaterialDetails> SetStaticModelMaterialAsync(
        string actorId, int slotIndex, string materialId, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxStaticModelMaterialDetails>(RequireHandshake(), "set_static_model_material",
            new { actorId, slotIndex, materialId }, cancellationToken);
    }

    public Task<FlaxMaterialDetails> CreateMaterialAsync(
        string relativePath, FlaxColor baseColor, double roughness, double metallic,
        FlaxColor? emissiveColor, string? baseColorTextureId, string? normalTextureId,
        FlaxVector2? uvTiling, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxMaterialDetails>(RequireHandshake(), "create_material",
            new
            {
                relativePath,
                baseColor,
                roughness,
                metallic,
                emissiveColor,
                baseColorTextureId,
                normalTextureId,
                uvTiling,
            }, cancellationToken);
    }

    public Task<FlaxMaterialDetails> GetMaterialDetailsAsync(
        string materialId, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxMaterialDetails>(RequireHandshake(), "material_details",
            new { materialId }, cancellationToken);
    }

    public Task<FlaxMaterialInstanceDetails> CreateMaterialInstanceAsync(
        string relativePath, string baseMaterialId, IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxMaterialInstanceDetails>(RequireHandshake(), "create_material_instance",
            new { relativePath, baseMaterialId, parameters }, cancellationToken);
    }

    public Task<FlaxMaterialInstanceDetails> SetMaterialInstanceParameterAsync(
        string materialInstanceId, string parameterName, JsonElement value,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxMaterialInstanceDetails>(RequireHandshake(), "set_material_instance_parameter",
            new { materialInstanceId, parameterName, value }, cancellationToken);
    }

    public Task<FlaxBoxColliderDetails> GetBoxColliderDetailsAsync(
        string actorId, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxBoxColliderDetails>(RequireHandshake(), "box_collider_details",
            new { actorId }, cancellationToken);
    }

    public Task<FlaxBoxColliderDetails> CreateBoxColliderAsync(
        string parentId, string name, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxBoxColliderDetails>(RequireHandshake(), "create_box_collider",
            new { parentId, name, size, center, isTrigger }, cancellationToken);
    }

    public Task<FlaxBoxColliderDetails> SetBoxColliderAsync(
        string actorId, FlaxVector3 size, FlaxVector3 center, bool isTrigger,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxBoxColliderDetails>(RequireHandshake(), "set_box_collider",
            new { actorId, size, center, isTrigger }, cancellationToken);
    }

    public Task<FlaxEditorSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxEditorSaveResult>(RequireHandshake(), "save", null, cancellationToken);
    }

    public async Task<FlaxEditorPlayModeResult> SetPlayModeAsync(
        string action,
        CancellationToken cancellationToken = default)
    {
        var (isPlayMode, isPaused) = ExpectedPlayState(action);
        var state = await CallAsync<FlaxEditorPlayModeResult>(
            RequireHandshake(),
            "play_mode",
            new { action },
            cancellationToken
        );
        var deadline = DateTime.UtcNow + PlayModeWait;
        while (state.IsPlayMode != isPlayMode || state.IsPaused != isPaused)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new McpException(
                    $"The Flax Editor did not complete the '{action}' play mode action within " +
                    $"{PlayModeWait.TotalSeconds:0} seconds (play mode {state.IsPlayMode}, paused {state.IsPaused})."
                );
            }

            await Task.Delay(PollInterval, cancellationToken);
            state = await TryCallAsync<FlaxEditorPlayModeResult>("play_mode_state", cancellationToken) ?? state;
        }

        return state with { RequestedAction = action.ToLowerInvariant() };
    }

    private static (bool IsPlayMode, bool IsPaused) ExpectedPlayState(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "start" => (true, false),
            "stop" => (false, false),
            "pause" => (true, true),
            "resume" => (true, false),
            _ => throw new McpException("Play mode action must be start, stop, pause, or resume."),
        };
    }

    public async Task<FlaxEditorCompileResult> CompileAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var logFile = FlaxLogReader.FindLatestLogFile(_projectFolder);
        var logMark = logFile is null ? 0 : FlaxLogReader.ReadLog(logFile.FullName).Length;
        var playState = await CallAsync<FlaxEditorPlayModeResult>(RequireHandshake(), "play_mode_state", null, cancellationToken);
        if (playState.IsPlayMode)
        {
            await SetPlayModeAsync("stop", cancellationToken);
        }

        var started = await CallAsync<FlaxCompileStatus>(RequireHandshake(), "compile", null, cancellationToken);
        var deadline = DateTime.UtcNow + CompileWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var status = await TryCallAsync<FlaxCompileStatus>("compile_status", cancellationToken);
            if (status is null)
            {
                continue;
            }

            if (status.LoadedUtcTicks != started.LoadedUtcTicks)
            {
                return new FlaxEditorCompileResult("reloaded", watch.Elapsed.TotalSeconds, [], []);
            }

            if (status.IsCompiling || status.CompilationsCount <= started.CompilationsCount)
            {
                continue;
            }

            if (status.LastCompilationFailed)
            {
                return Failed(logMark, watch.Elapsed.TotalSeconds);
            }

            if (watch.Elapsed > NothingToReloadAfter)
            {
                return new FlaxEditorCompileResult("unchanged", watch.Elapsed.TotalSeconds, [], []);
            }
        }

        throw new McpException($"The Flax Editor did not finish compiling within {CompileWait.TotalSeconds:0} seconds.");
    }

    private FlaxEditorCompileResult Failed(int logMark, double seconds)
    {
        var logFile = FlaxLogReader.FindLatestLogFile(_projectFolder);
        var log = logFile is null ? "" : FlaxLogReader.ReadLog(logFile.FullName);
        var since = logMark <= log.Length ? log[logMark..] : log;
        var errors = FlaxCompilerDiagnosticParser.Parse(since).Where(diagnostic => diagnostic.Severity == "error").ToArray();
        var buildErrors = since.Split('\n')
            .Where(line => line.Contains("Sharing violation", StringComparison.Ordinal) ||
                line.Contains("Failed to run build tool", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToArray();
        return new FlaxEditorCompileResult("failed", seconds, errors, buildErrors);
    }

    public Task<FlaxEditorOpenSceneResult> OpenSceneAsync(string path, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxEditorOpenSceneResult>(RequireHandshake(), "open_scene", new { path }, cancellationToken);
    }

    private async Task<T?> TryCallAsync<T>(string method, CancellationToken cancellationToken) where T : class
    {
        var handshake = ReadHandshake();
        if (handshake is null)
        {
            return null;
        }

        try
        {
            return await CallAsync<T>(handshake, method, null, cancellationToken);
        }
        catch (McpException ex) when (ex.InnerException is IOException or OperationCanceledException)
        {
            return null;
        }
        catch (FlaxBridgeRequestException ex) when (ex.Code == "request_timeout")
        {
            return null;
        }
    }

    public Task<FlaxBridgeScreenshot> CaptureScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxBridgeScreenshot>(RequireHandshake(), "screenshot", new { path }, cancellationToken);
    }

    public Task<FlaxCodeExecutionResult> ExecuteCSharpAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<FlaxCodeExecutionResult>(
            RequireHandshake(),
            "execute_csharp",
            new { code },
            cancellationToken
        );
    }

    private Task<FlaxBridgePing> PingAsync(FlaxBridgeHandshake handshake, CancellationToken cancellationToken)
    {
        return CallAsync<FlaxBridgePing>(handshake, "ping", null, cancellationToken);
    }

    private async Task<T> CallAsync<T>(
        FlaxBridgeHandshake handshake,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            handshake.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        ValidateProtocol(handshake);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionTimeout.CancelAfter(ConnectionTimeout);
        var connected = false;

        try
        {
            await pipe.ConnectAsync(connectionTimeout.Token);
            connected = true;
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

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(RequestTimeout);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(
                    new { protocolVersion = CurrentProtocolVersion, id = 1, method, @params = parameters },
                    SerializerOptions
                )
            );
            var responseLine = await reader.ReadLineAsync(requestTimeout.Token) ??
                throw new IOException("The Flax Editor bridge disconnected before returning a response.");
            using var response = JsonDocument.Parse(responseLine);
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var bridgeError = error.Deserialize<FlaxBridgeError>(SerializerOptions) ??
                    throw new JsonException("The Flax Editor bridge returned an invalid error response.");
                throw new FlaxBridgeRequestException(bridgeError.Code, bridgeError.Message);
            }

            return root.GetProperty("result").Deserialize<T>(SerializerOptions) ??
                throw new JsonException("The Flax Editor bridge returned an empty result.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var timeout = connected ?
                RequestTimeout :
                ConnectionTimeout;
            throw new McpException(
                $"The Flax Editor bridge did not respond within {timeout.TotalSeconds:0} " +
                $"{(timeout == ConnectionTimeout ? "second" : "seconds")}.",
                ex
            );
        }
        catch (IOException ex)
        {
            var message = connected ?
                "The Flax Editor bridge disconnected before returning a response." :
                "The Flax Editor bridge session is no longer available.";
            throw new McpException(message, ex);
        }
    }

    private FlaxBridgeHandshake RequireHandshake()
    {
        return ReadHandshake() ??
            throw new McpException(
                "No Flax Editor bridge session is available for the configured project."
            );
    }

    private static void ValidateProtocol(FlaxBridgeHandshake handshake)
    {
        if (handshake.ProtocolVersion != CurrentProtocolVersion)
        {
            throw new FlaxBridgeProtocolException(
                $"Flax Editor bridge protocol mismatch: server requires version {CurrentProtocolVersion}, " +
                $"but the editor plugin reports version {handshake.ProtocolVersion}."
            );
        }
    }

    private FlaxBridgeHandshake? ReadHandshake()
    {
        var path = Path.Combine(_sessionsDirectory, Hash(_projectFolder) + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<FlaxBridgeHandshake>(
            File.ReadAllText(path),
            SerializerOptions
        );
    }

    private static string Hash(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16];
    }

    private sealed record FlaxBridgeHandshake(
        string PipeName,
        int ProtocolVersion,
        string PluginVersion,
        int EngineBuild,
        DateTime StartedUtc);
}

public sealed record FlaxBridgeStatus(
    bool Connected,
    string? PluginVersion,
    int? EngineBuild,
    DateTime? StartedUtc,
    string? Error = null)
{
    public static FlaxBridgeStatus Disconnected { get; } = new(
        false,
        null,
        null,
        null,
        null
    );
}

public sealed record FlaxBridgePing(bool Pong, DateTime UtcNow);

public sealed class FlaxBridgeRequestException(string code, string message)
    : McpException($"Flax Editor bridge error [{code}]: {message}")
{
    public string Code { get; } = code;
}

public sealed record FlaxBridgeScreenshot(string Path, long Bytes);

public sealed record FlaxCodeExecutionResult(int MainThreadId, string? TypeName, JsonElement? Result);

public sealed record FlaxEditorSaveResult(int MainThreadId, bool Saved, int SavedScenes);

public sealed record FlaxCompileStatus(
    bool IsCompiling,
    bool LastCompilationFailed,
    int CompilationsCount,
    long LoadedUtcTicks);

public sealed record FlaxEditorCompileResult(
    string Outcome,
    double Seconds,
    IReadOnlyList<FlaxCompilerDiagnostic> Errors,
    IReadOnlyList<string> BuildErrors);

public sealed record FlaxEditorOpenSceneResult(int MainThreadId, string SceneId, string SceneName, IReadOnlyList<string> Scenes);

public sealed record FlaxEditorPlayModeResult(
    int MainThreadId,
    string RequestedAction,
    bool IsPlayMode,
    bool IsPaused);

public sealed record FlaxLiveSceneGraph(
    int MainThreadId,
    IReadOnlyList<FlaxLiveSceneNode> Scenes,
    bool Truncated);

public sealed record FlaxLiveSceneNode(
    string Id,
    string TypeName,
    string? Name,
    IReadOnlyList<FlaxLiveSceneNode> Children);

public sealed record FlaxEditorSelection(
    int MainThreadId,
    IReadOnlyList<FlaxEditorSelectionItem> Selected);

public sealed record FlaxEditorSelectionItem(string Id, string TypeName, string Name);

public sealed record FlaxActorDetails(
    int MainThreadId,
    string Id,
    string TypeName,
    string Name,
    string? ParentId,
    string? SceneId,
    bool IsActive,
    bool IsActiveInHierarchy,
    int Layer,
    string LayerName,
    IReadOnlyList<string> Tags,
    FlaxActorTransform Transform,
    FlaxActorTransform LocalTransform,
    IReadOnlyList<FlaxActorScript> Scripts);

public sealed record FlaxActorTransform(
    FlaxVector3 Translation,
    FlaxQuaternion Orientation,
    FlaxVector3 Scale);

public sealed record FlaxVector3(double X, double Y, double Z);

public sealed record FlaxVector2(double X, double Y);

public sealed record FlaxColor(double R, double G, double B, double A = 1.0);

public sealed record FlaxQuaternion(double X, double Y, double Z, double W);

public sealed record FlaxActorScript(string Id, string TypeName, bool Enabled, bool IsEnabledInHierarchy);

public sealed record FlaxActorDeletionResult(
    int MainThreadId,
    string ActorId,
    bool DeletedDescendants,
    IReadOnlyList<string> DeletedActorIds);

public sealed record FlaxStaticModelDetails(
    int MainThreadId,
    FlaxActorDetails Actor,
    string? ModelId,
    string? ModelPath,
    bool ModelIsLoaded);

public sealed record FlaxStaticModelMaterialDetails(
    int MainThreadId,
    FlaxActorDetails Actor,
    int SlotIndex,
    string MaterialId,
    string? MaterialPath);

public sealed record FlaxMaterialDetails(
    int MainThreadId,
    string MaterialId,
    string MaterialPath,
    FlaxColor BaseColor,
    double Roughness,
    double Metallic,
    FlaxColor? EmissiveColor,
    string? BaseColorTextureId,
    string? NormalTextureId,
    FlaxVector2? UvTiling,
    IReadOnlyList<FlaxMaterialParameterDetails> Parameters);

public sealed record FlaxMaterialParameterDetails(
    string Name,
    string Type,
    bool IsPublic,
    bool IsOverride);

public sealed record FlaxMaterialInstanceDetails(
    int MainThreadId,
    string MaterialInstanceId,
    string MaterialInstancePath,
    string BaseMaterialId,
    string? BaseMaterialPath,
    IReadOnlyList<FlaxMaterialInstanceParameterDetails> Parameters);

public sealed record FlaxMaterialInstanceParameterDetails(
    string Name,
    string Type,
    bool IsOverride,
    JsonElement Value);

public sealed record FlaxBoxColliderDetails(
    int MainThreadId,
    FlaxActorDetails Actor,
    FlaxVector3 Size,
    FlaxVector3 Center,
    bool IsTrigger);

internal sealed record FlaxBridgeError(string Code, string Message);

internal sealed class FlaxBridgeProtocolException : McpException
{
    public FlaxBridgeProtocolException(string message)
        : base(message)
    {
    }
}
