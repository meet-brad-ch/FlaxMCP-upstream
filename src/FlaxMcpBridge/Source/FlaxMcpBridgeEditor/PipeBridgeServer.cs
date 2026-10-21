using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaxEngine;
using FlaxEditor.Content;
using FlaxEditor.Surface;
using FlaxEditor.Windows;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlaxMcpBridge.Editor;

/// <summary>
/// Named-pipe bridge transport accepting one client connection at a time and exchanging
/// newline-delimited JSON messages.
/// </summary>
internal sealed class PipeBridgeServer : IDisposable
{
    private const int ProtocolVersion = 15;
    private static readonly long LoadedUtcTicks = DateTime.UtcNow.Ticks;
    private const int MaxSceneGraphDepth = 32;
    private const int MaxSceneGraphNodes = 500;
    private const double AssetLoadTimeoutMilliseconds = 3500;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<int, MethodInfo> DynamicMethods = new();
    private static int _nextDynamicMethodId;
    private readonly string _projectFolder;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private NamedPipeServerStream? _currentPipe;
    private string? _handshakePath;

    public string PipeName { get; }

    public PipeBridgeServer(string projectFolder)
    {
        _projectFolder = projectFolder;
        PipeName = "FlaxMcp-" + Hash(projectFolder);
    }

    public void Start()
    {
        WriteHandshakeFile();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _currentPipe = pipe;
                await pipe.WaitForConnectionAsync(token);
                await HandleClientAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                // Shutting down while waiting for a client or reading a request.
            }
            catch (IOException)
            {
                // Client disconnected mid-message; loop and accept the next connection.
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlaxMcpBridge] Pipe connection error: {ex}");
            }
            finally
            {
                pipe?.Dispose();
                _currentPipe = null;
            }
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

        while (pipe.IsConnected && !token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
                break;

            string responseJson;
            try
            {
                var dispatchTask = DispatchAsync(line);
                if (!dispatchTask.Wait(RequestTimeout))
                {
                    ObserveFault(dispatchTask);
                    responseJson = SerializeError(
                        ReadRequestId(line),
                        "request_timeout",
                        $"The editor action did not complete within {RequestTimeout.TotalSeconds:0} seconds."
                    );
                }
                else
                {
                    responseJson = dispatchTask.GetAwaiter().GetResult();
                }
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                responseJson = SerializeError(ReadRequestId(line), "request_failed", ex.InnerException!.Message);
            }
            catch (Exception ex)
            {
                responseJson = SerializeError(ReadRequestId(line), "request_failed", ex.Message);
            }

            await writer.WriteLineAsync(responseJson);
            await writer.FlushAsync(token);
        }
    }

    private static async Task<string> DispatchAsync(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        var id = 0;
        if (root.TryGetProperty("id", out var idElement) && !idElement.TryGetInt32(out id))
        {
            return SerializeError(0, "invalid_request", "Request id must be a 32-bit integer.");
        }

        var protocolVersion = 0;
        if (root.TryGetProperty("protocolVersion", out var versionElement) &&
            !versionElement.TryGetInt32(out protocolVersion))
        {
            return SerializeError(id, "invalid_request", "Protocol version must be a 32-bit integer.");
        }

        if (protocolVersion != ProtocolVersion)
        {
            return SerializeError(
                id,
                "protocol_mismatch",
                $"The editor plugin requires protocol version {ProtocolVersion}, but the client sent version {protocolVersion}."
            );
        }
        var method = root.GetProperty("method").GetString() ?? string.Empty;

        object result = method switch
        {
            "ping" => new { pong = true, utcNow = DateTime.UtcNow },
            "scene_graph" => await GetSceneGraphAsync(),
            "get_selection" => await GetSelectionAsync(),
            "set_selection" => await SetSelectionAsync(RequireStringArrayParam(root, "actorIds")),
            "actor_details" => await GetActorDetailsAsync(RequireParam(root, "actorId")),
            "modify_actor" => await ModifyActorAsync(
                RequireParam(root, "actorId"),
                RequireObjectParam(root, "transform")
            ),
            "create_actor" => await CreateActorAsync(
                RequireParam(root, "actorType"), RequireParam(root, "name"),
                OptionalParam(root, "sceneId"), OptionalParam(root, "parentId"),
                RequireObjectParam(root, "transform")
            ),
            "duplicate_actor" => await DuplicateActorAsync(
                RequireParam(root, "actorId"), RequireParam(root, "name"),
                OptionalParam(root, "sceneId"), OptionalParam(root, "parentId"),
                RequireObjectParam(root, "transform")
            ),
            "rename_actor" => await RenameActorAsync(
                RequireParam(root, "actorId"), RequireParam(root, "name")
            ),
            "reparent_actor" => await ReparentActorAsync(
                RequireParam(root, "actorId"), OptionalParam(root, "sceneId"),
                OptionalParam(root, "parentId"), RequireBoolParam(root, "preserveWorldTransform")
            ),
            "delete_actor" => await DeleteActorAsync(
                RequireParam(root, "actorId"), RequireBoolParam(root, "deleteDescendants")
            ),
            "static_model_details" => await GetStaticModelDetailsAsync(RequireParam(root, "actorId")),
            "set_static_model" => await SetStaticModelAsync(
                RequireParam(root, "actorId"), RequireParam(root, "modelId")
            ),
            "set_static_model_material" => await SetStaticModelMaterialAsync(
                RequireParam(root, "actorId"), RequireIntParam(root, "slotIndex"),
                RequireParam(root, "materialId")
            ),
            "create_material" => await CreateMaterialAsync(
                RequireParam(root, "relativePath"), RequireObjectParam(root, "baseColor"),
                RequireDoubleParam(root, "roughness"), RequireDoubleParam(root, "metallic"),
                OptionalObjectParam(root, "emissiveColor"), OptionalParam(root, "baseColorTextureId"),
                OptionalParam(root, "normalTextureId"), OptionalObjectParam(root, "uvTiling")
            ),
            "material_details" => await GetMaterialDetailsAsync(RequireParam(root, "materialId")),
            "create_material_instance" => await CreateMaterialInstanceAsync(
                RequireParam(root, "relativePath"), RequireParam(root, "baseMaterialId"),
                RequireObjectParam(root, "parameters")
            ),
            "set_material_instance_parameter" => await SetMaterialInstanceParameterAsync(
                RequireParam(root, "materialInstanceId"), RequireParam(root, "parameterName"),
                RequireValueParam(root, "value")
            ),
            "box_collider_details" => await GetBoxColliderDetailsAsync(RequireParam(root, "actorId")),
            "create_box_collider" => await CreateBoxColliderAsync(
                RequireParam(root, "parentId"), RequireParam(root, "name"),
                RequireObjectParam(root, "size"), RequireObjectParam(root, "center"),
                RequireBoolParam(root, "isTrigger")
            ),
            "set_box_collider" => await SetBoxColliderAsync(
                RequireParam(root, "actorId"), RequireObjectParam(root, "size"),
                RequireObjectParam(root, "center"), RequireBoolParam(root, "isTrigger")
            ),
            "save" => await SaveAsync(),
            "play_mode" => await SetPlayModeAsync(RequireParam(root, "action")),
            "play_mode_state" => await GetPlayModeStateAsync(),
            "compile" => await CompileAsync(),
            "compile_status" => await GetCompileStatusAsync(),
            "open_scene" => await OpenSceneAsync(RequireParam(root, "path")),
            "screenshot" => await CaptureScreenshotAsync(RequireParam(root, "path")),
            "execute_csharp" => await ExecuteCSharpAsync(RequireParam(root, "code")),
            _ => throw new InvalidOperationException($"Unknown method '{method}'"),
        };

        var normalized = NormalizeDynamicResult(result, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["result"] = normalized,
        });
    }

    private static string SerializeError(int id, string code, string message)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
            },
        });
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static int ReadRequestId(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ?
                value :
                0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string RequireParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.TryGetProperty(name, out var value) &&
            value.GetString() is { } stringValue)
        {
            return stringValue;
        }
        throw new ArgumentException($"Missing required params.{name}");
    }

    private static string? OptionalParam(JsonElement root, string name)
    {
        return root.TryGetProperty("params", out var paramsElement) &&
               paramsElement.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static IReadOnlyList<string> RequireStringArrayParam(JsonElement root, string name)
    {
        if (!root.TryGetProperty("params", out var paramsElement) ||
            !paramsElement.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Missing required params.{name} array");
        }

        var result = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"params.{name} must contain strings");
            }

            result.Add(item.GetString()!);
        }
        return result;
    }

    private static JsonElement RequireObjectParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value.Clone();
        }

        throw new ArgumentException($"Missing required params.{name} object");
    }

    private static JsonElement RequireValueParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) && paramsElement.TryGetProperty(name, out var value))
            return value.Clone();
        throw new ArgumentException($"Missing required params.{name}");
    }

    private static JsonElement? OptionalObjectParam(JsonElement root, string name)
    {
        return root.TryGetProperty("params", out var paramsElement) &&
               paramsElement.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Object ? value.Clone() : null;
    }

    private static double RequireDoubleParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.TryGetProperty(name, out var value) && value.TryGetDouble(out var doubleValue))
        {
            return doubleValue;
        }

        throw new ArgumentException($"Missing required params.{name} number");
    }

    private static bool RequireBoolParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.TryGetProperty(name, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
        {
            return value.GetBoolean();
        }

        throw new ArgumentException($"Missing required params.{name} boolean");
    }

    private static int RequireIntParam(JsonElement root, string name)
    {
        if (root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.TryGetProperty(name, out var value) &&
            value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        throw new ArgumentException($"Missing required params.{name} integer");
    }

    private static Task<object> GetSceneGraphAsync()
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripting.InvokeOnUpdate(() =>
        {
            try
            {
                var state = new SceneGraphState();
                var scenes = new List<object>();
                foreach (var scene in Level.Scenes)
                {
                    if (state.NodeCount >= MaxSceneGraphNodes)
                    {
                        state.Truncated = true;
                        break;
                    }

                    scenes.Add(BuildSceneNode(scene, 0, state));
                }
                tcs.TrySetResult(new { mainThreadId = Globals.MainThreadID, scenes, truncated = state.Truncated });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Task<object> GetSelectionAsync()
    {
        return InvokeOnUpdateAsync(GetSelection);
    }

    private static Task<object> SetSelectionAsync(IReadOnlyList<string> actorIds)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var nodes = new List<FlaxEditor.SceneGraph.SceneGraphNode>(actorIds.Count);
            foreach (var actorId in actorIds)
            {
                if (!Guid.TryParse(actorId, out var id))
                {
                    throw new ArgumentException($"Actor id '{actorId}' is not a valid GUID.");
                }

                var node = FlaxEditor.Editor.Instance.Scene.GetActorNode(id) ??
                    throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor scene graph.");
                nodes.Add(node);
            }

            FlaxEditor.Editor.Instance.SceneEditing.Select(nodes, additive: false);
            return GetSelection();
        });
    }

    private static object GetSelection()
    {
        var selected = FlaxEditor.Editor.Instance.SceneEditing.Selection
            .OfType<FlaxEditor.SceneGraph.ActorNode>()
            .Select(node => new
            {
                id = node.Actor.ID.ToString("D"),
                typeName = node.Actor.GetType().FullName ?? node.Actor.GetType().Name,
                name = node.Actor.Name,
            })
            .ToList();
        return new { mainThreadId = Globals.MainThreadID, selected };
    }

    private static Task<object> GetActorDetailsAsync(string actorId)
    {
        return InvokeOnUpdateAsync(() =>
        {
            if (!Guid.TryParse(actorId, out var id))
            {
                throw new ArgumentException($"Actor id '{actorId}' is not a valid GUID.");
            }

            var actor = Level.FindActor(id) ??
                throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor.");
            return BuildActorDetails(actor);
        });
    }

    private static Task<object> ModifyActorAsync(string actorId, JsonElement transform)
    {
        return InvokeOnUpdateAsync(() =>
        {
            if (!Guid.TryParse(actorId, out var id))
            {
                throw new ArgumentException($"Actor id '{actorId}' is not a valid GUID.");
            }

            var actor = Level.FindActor(id) ??
                throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor.");
            actor.Transform = ReadTransform(transform);
            if (actor.Scene is { } scene)
            {
                FlaxEditor.Editor.Instance.Scene.MarkSceneEdited(scene);
            }
            return BuildActorDetails(actor);
        });
    }

    private static Task<object> CreateActorAsync(
        string actorType, string name, string? sceneId, string? parentId, JsonElement transform)
    {
        return InvokeOnUpdateAsync(() =>
        {
            ValidateActorName(name);
            var destination = ResolveDestination(sceneId, parentId);
            var actorTransform = ReadTransform(transform);
            Actor actor = actorType switch
            {
                "EmptyActor" => new EmptyActor(),
                "StaticModel" => new StaticModel(),
                _ => throw new ArgumentException(
                    $"Actor type '{actorType}' is not allowed. Allowed types: EmptyActor, StaticModel."
                ),
            };
            return SpawnActor(actor, name, destination, actorTransform);
        });
    }

    private static Task<object> DuplicateActorAsync(
        string actorId, string name, string? sceneId, string? parentId, JsonElement transform)
    {
        return InvokeOnUpdateAsync(() =>
        {
            ValidateActorName(name);
            var destination = ResolveDestination(sceneId, parentId);
            var actorTransform = ReadTransform(transform);
            if (!Guid.TryParse(actorId, out var id))
            {
                throw new ArgumentException($"Actor id '{actorId}' is not a valid GUID.");
            }
            var source = Level.FindActor(id) ??
                throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor.");
            return SpawnActor(source.Clone(), name, destination, actorTransform);
        });
    }

    private static Task<object> RenameActorAsync(string actorId, string name)
    {
        return InvokeOnUpdateAsync(() =>
        {
            ValidateActorName(name);
            var actor = FindActor(actorId);
            FlaxEditor.Editor.Instance.SceneEditing.Undo.RecordAction(actor, "Rename actor", () =>
            {
                actor.Name = name;
                MarkSceneEdited(actor);
            });
            return BuildActorDetails(actor);
        });
    }

    private static Task<object> ReparentActorAsync(
        string actorId, string? sceneId, string? parentId, bool preserveWorldTransform)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindActor(actorId);
            var destination = ResolveDestination(sceneId, parentId);
            if (actor.Scene != destination.Scene)
            {
                throw new InvalidOperationException("Moving actors between scenes is not supported.");
            }

            for (Actor? ancestor = destination; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor.ID == actor.ID)
                {
                    throw new InvalidOperationException("An actor cannot be parented to itself or its descendant.");
                }
            }

            FlaxEditor.Editor.Instance.SceneEditing.Undo.RecordAction(actor, "Reparent actor", () =>
            {
                actor.SetParent(destination, preserveWorldTransform, false);
                MarkSceneEdited(actor);
            });
            return BuildActorDetails(actor);
        });
    }

    private static Task<object> DeleteActorAsync(string actorId, bool deleteDescendants)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindActor(actorId);
            var deletedActorIds = CollectActorIds(actor);
            if (!deleteDescendants && deletedActorIds.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Actor '{actorId}' has {deletedActorIds.Count - 1} descendant(s). " +
                    "Set deleteDescendants to true to delete the complete hierarchy."
                );
            }

            var selectedActorIds = FlaxEditor.Editor.Instance.SceneEditing.Selection
                .OfType<FlaxEditor.SceneGraph.ActorNode>()
                .Select(node => node.Actor.ID)
                .Where(id => !deletedActorIds.Contains(id.ToString("D")))
                .ToList();
            var node = FlaxEditor.Editor.Instance.Scene.GetActorNode(actor.ID) ??
                throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor scene graph.");
            FlaxEditor.Editor.Instance.SceneEditing.Select(new[] { node }, additive: false);
            FlaxEditor.Editor.Instance.SceneEditing.Delete();

            var restoredSelection = new List<FlaxEditor.SceneGraph.SceneGraphNode>();
            foreach (var selectedActorId in selectedActorIds)
            {
                if (FlaxEditor.Editor.Instance.Scene.GetActorNode(selectedActorId) is { } selectedNode)
                {
                    restoredSelection.Add(selectedNode);
                }
            }
            FlaxEditor.Editor.Instance.SceneEditing.Select(restoredSelection, additive: false);

            return new
            {
                mainThreadId = Globals.MainThreadID,
                actorId,
                deletedDescendants = deletedActorIds.Count > 1,
                deletedActorIds,
            };
        });
    }

    private static List<string> CollectActorIds(Actor actor)
    {
        var actorIds = new List<string> { actor.ID.ToString("D") };
        foreach (var child in actor.Children)
        {
            actorIds.AddRange(CollectActorIds(child));
        }
        return actorIds;
    }

    private static Task<object> GetStaticModelDetailsAsync(string actorId)
    {
        return InvokeOnUpdateAsync(() => BuildStaticModelDetails(FindStaticModel(actorId)));
    }

    private static Task<object> SetStaticModelAsync(string actorId, string modelId)
    {
        if (!TryParseContentGuid(modelId, out var id))
        {
            throw new ArgumentException($"Model id '{modelId}' is not a valid GUID.");
        }

        var model = Content.LoadAsync<Model>(id) ??
            throw new KeyNotFoundException($"Model asset '{modelId}' does not exist or is not a FlaxEngine.Model.");
        if (model.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !model.IsLoaded)
        {
            throw new InvalidOperationException(
                $"Model asset '{modelId}' could not be loaded within {AssetLoadTimeoutMilliseconds:0} ms."
            );
        }
        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindStaticModel(actorId);
            FlaxEditor.Editor.Instance.SceneEditing.Undo.RecordAction(actor, "Set static model", () =>
            {
                actor.Model = model;
                MarkSceneEdited(actor);
            });
            return BuildStaticModelDetails(actor);
        });
    }

    private static StaticModel FindStaticModel(string actorId)
    {
        return FindActor(actorId) as StaticModel ??
            throw new InvalidOperationException($"Actor '{actorId}' is not a FlaxEngine.StaticModel.");
    }

    private static Task<object> SetStaticModelMaterialAsync(string actorId, int slotIndex, string materialId)
    {
        if (!TryParseContentGuid(materialId, out var id))
        {
            throw new ArgumentException($"Material id '{materialId}' is not a valid GUID.");
        }

        var material = Content.LoadAsync<MaterialBase>(id) ??
            throw new KeyNotFoundException(
                $"Material asset '{materialId}' does not exist or is not a FlaxEngine.MaterialBase."
            );
        if (material.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !material.IsLoaded)
        {
            throw new InvalidOperationException(
                $"Material asset '{materialId}' could not be loaded within {AssetLoadTimeoutMilliseconds:0} ms."
            );
        }
        if (!material.IsSurface)
        {
            throw new InvalidOperationException(
                $"Material asset '{materialId}' is not a surface material compatible with StaticModel."
            );
        }

        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindStaticModel(actorId);
            var model = actor.Model;
            if (model is null || !model.IsLoaded)
            {
                throw new InvalidOperationException(
                    $"StaticModel actor '{actorId}' does not have a loaded model."
                );
            }
            if (slotIndex < 0 || slotIndex >= model.MaterialSlotsCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slotIndex), slotIndex,
                    $"StaticModel actor '{actorId}' has {model.MaterialSlotsCount} material slots."
                );
            }

            FlaxEditor.Editor.Instance.SceneEditing.Undo.RecordAction(actor, "Set static model material", () =>
            {
                actor.SetMaterial(slotIndex, material);
                MarkSceneEdited(actor);
            });
            return BuildStaticModelMaterialDetails(actor, slotIndex);
        });
    }

    private static object BuildStaticModelMaterialDetails(StaticModel actor, int slotIndex)
    {
        var material = actor.GetMaterial(slotIndex);
        return new
        {
            mainThreadId = Globals.MainThreadID,
            actor = BuildActorDetails(actor),
            slotIndex,
            materialId = FormatContentGuid(material.ID),
            materialPath = material.Path,
        };
    }

    private static async Task<object> CreateMaterialAsync(
        string relativePath, JsonElement baseColor, double roughness, double metallic,
        JsonElement? emissiveColor, string? baseColorTextureId, string? normalTextureId,
        JsonElement? uvTiling)
    {
        var color = ReadColor(baseColor, "baseColor");
        var emissive = emissiveColor.HasValue ? ReadColor(emissiveColor.Value, "emissiveColor") : (Color?)null;
        ValidateUnitValue(roughness, "roughness");
        ValidateUnitValue(metallic, "metallic");
        var tiling = uvTiling.HasValue ? ReadPositiveFloat2(uvTiling.Value, "uvTiling") : (Float2?)null;
        if (tiling.HasValue && baseColorTextureId is null && normalTextureId is null)
        {
            throw new ArgumentException("uvTiling requires baseColorTextureId or normalTextureId.");
        }

        var path = (string)await InvokeOnUpdateAsync(() => ResolveContentPath(relativePath));
        var directory = Path.GetDirectoryName(path)!;
        var directoryExisted = Directory.Exists(directory);
        Material? createdMaterial = null;
        var mutationStarted = false;
        try
        {
            return await InvokeOnUpdateAsync(() =>
            {
                if (File.Exists(path) || FlaxEditor.Editor.Instance.ContentDatabase.Find(path) is not null)
                    throw new InvalidOperationException($"A content item already exists at '{relativePath}'.");
                var baseTexture = LoadTexture(baseColorTextureId, false);
                var normalTexture = LoadTexture(normalTextureId, true);
                Directory.CreateDirectory(directory);
                mutationStarted = true;
                new MaterialProxy().Create(path, null);
                RefreshContentDatabase();
                var item = FlaxEditor.Editor.Instance.ContentDatabase.Find(path) as AssetItem ??
                    throw new InvalidOperationException("The created material was not registered in the Content Database.");
                item.LoadAsync();
                var material = createdMaterial = Content.LoadAsync<Material>(item.ID) ??
                    throw new InvalidOperationException("The created material could not be loaded.");
                if (material.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !material.IsLoaded)
                {
                    throw new InvalidOperationException(
                        $"The created material could not be loaded within {AssetLoadTimeoutMilliseconds:0} ms."
                    );
                }

                WriteMaterialGraph(material, color, (float)roughness, (float)metallic, emissive,
                    baseTexture, normalTexture, tiling);
                return BuildMaterialDetails(material);
            });
        }
        catch
        {
            if (mutationStarted)
                await CleanupFailedMaterialAsync(path, directory, directoryExisted, createdMaterial);
            throw;
        }
    }

    private static async Task CleanupFailedMaterialAsync(
        string path, string directory, bool directoryExisted, Material? material)
    {
        await CleanupFailedAssetAsync(path, directory, directoryExisted, material);
    }

    private static async Task CleanupFailedAssetAsync(
        string path, string directory, bool directoryExisted, Asset? asset)
    {
        if (asset is not null)
            await InvokeOnUpdateAsync(() => { Content.UnloadAsset(asset); return true; });
        await InvokeOnUpdateAsync(() =>
        {
            var item = FlaxEditor.Editor.Instance.ContentDatabase.Find(path) as AssetItem;
            if (item is not null)
                FlaxEditor.Editor.Instance.ContentDatabase.Delete(item, true);
            else if (File.Exists(path))
                File.Delete(path);
            return true;
        });
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var removed = (bool)await InvokeOnUpdateAsync(() =>
            {
                RefreshContentDatabase();
                return !File.Exists(path) && FlaxEditor.Editor.Instance.ContentDatabase.Find(path) is null;
            });
            if (removed)
            {
                if (!directoryExisted && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
                return;
            }
        }
        throw new InvalidOperationException($"Failed to remove partial material asset '{path}'.");
    }

    private static Task<object> GetMaterialDetailsAsync(string materialId)
    {
        if (!TryParseContentGuid(materialId, out var id))
            throw new ArgumentException($"Material id '{materialId}' is not a valid GUID.");

        return InvokeOnUpdateAsync(() =>
        {
            var material = Content.LoadAsync<Material>(id) ??
                throw new KeyNotFoundException(
                    $"Material asset '{materialId}' does not exist or is not a FlaxEngine.Material."
                );
            if (material.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !material.IsLoaded)
            {
                throw new InvalidOperationException(
                    $"Material asset '{materialId}' could not be loaded within {AssetLoadTimeoutMilliseconds:0} ms."
                );
            }
            if (!material.IsSurface)
                throw new InvalidOperationException($"Material asset '{materialId}' is not a surface material.");
            return BuildMaterialDetails(material);
        });
    }

    private static async Task<object> CreateMaterialInstanceAsync(
        string relativePath, string baseMaterialId, JsonElement parameters)
    {
        if (!TryParseContentGuid(baseMaterialId, out var baseId))
            throw new ArgumentException($"Base material id '{baseMaterialId}' is not a valid GUID.");
        var path = (string)await InvokeOnUpdateAsync(() => ResolveContentPath(relativePath));
        var directory = Path.GetDirectoryName(path)!;
        var directoryExisted = Directory.Exists(directory);
        MaterialInstance? createdInstance = null;
        var mutationStarted = false;
        try
        {
            return await InvokeOnUpdateAsync(() =>
            {
                if (File.Exists(path) || FlaxEditor.Editor.Instance.ContentDatabase.Find(path) is not null)
                    throw new InvalidOperationException($"A content item already exists at '{relativePath}'.");
                var baseMaterial = LoadSurfaceMaterial(baseId, baseMaterialId);
                var overrides = ReadMaterialParameterOverrides(baseMaterial, parameters);
                Directory.CreateDirectory(directory);
                mutationStarted = true;
                new MaterialInstanceProxy().Create(path, null);
                RefreshContentDatabase();
                var item = FlaxEditor.Editor.Instance.ContentDatabase.Find(path) as AssetItem ??
                    throw new InvalidOperationException("The created material instance was not registered in the Content Database.");
                item.LoadAsync();
                var instance = createdInstance = Content.LoadAsync<MaterialInstance>(item.ID) ??
                    throw new InvalidOperationException("The created material instance could not be loaded.");
                if (instance.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !instance.IsLoaded)
                    throw new InvalidOperationException("The created material instance could not be loaded.");
                instance.BaseMaterial = baseMaterial;
                foreach (var entry in overrides)
                    instance.SetParameterValue(entry.Key, entry.Value, true);
                if (instance.Save(path))
                    throw new InvalidOperationException("The material instance could not be saved.");
                return BuildMaterialInstanceDetails(instance);
            });
        }
        catch
        {
            if (mutationStarted)
                await CleanupFailedAssetAsync(path, directory, directoryExisted, createdInstance);
            throw;
        }
    }

    private static Task<object> SetMaterialInstanceParameterAsync(
        string materialInstanceId, string parameterName, JsonElement value)
    {
        if (!TryParseContentGuid(materialInstanceId, out var id))
            throw new ArgumentException($"Material instance id '{materialInstanceId}' is not a valid GUID.");
        return InvokeOnUpdateAsync(() =>
        {
            var instance = Content.LoadAsync<MaterialInstance>(id) ??
                throw new KeyNotFoundException(
                    $"Material instance '{materialInstanceId}' does not exist or is not a FlaxEngine.MaterialInstance.");
            if (instance.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !instance.IsLoaded)
                throw new InvalidOperationException($"Material instance '{materialInstanceId}' could not be loaded.");
            var baseMaterial = instance.BaseMaterial ??
                throw new InvalidOperationException($"Material instance '{materialInstanceId}' has no base material.");
            if (!baseMaterial.IsSurface)
                throw new InvalidOperationException("The base material is not a surface material.");
            var parameter = FindPublicParameter(baseMaterial, parameterName);
            var converted = ReadMaterialParameterValue(parameter.ParameterType.ToString(), value, parameterName);
            instance.SetParameterValue(parameterName, converted, true);
            if (instance.Save(instance.Path))
                throw new InvalidOperationException("The material instance could not be saved.");
            return BuildMaterialInstanceDetails(instance);
        });
    }

    private static Material LoadSurfaceMaterial(Guid id, string materialId)
    {
        var material = Content.LoadAsync<Material>(id) ??
            throw new KeyNotFoundException($"Base material '{materialId}' does not exist or is not a FlaxEngine.Material.");
        if (material.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !material.IsLoaded)
            throw new InvalidOperationException($"Base material '{materialId}' could not be loaded.");
        if (!material.IsSurface)
            throw new InvalidOperationException($"Base material '{materialId}' is not a surface material.");
        return material;
    }

    private static Dictionary<string, object> ReadMaterialParameterOverrides(Material material, JsonElement parameters)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            var parameter = FindPublicParameter(material, property.Name);
            result.Add(property.Name,
                ReadMaterialParameterValue(parameter.ParameterType.ToString(), property.Value, property.Name));
        }
        return result;
    }

    private static MaterialParameter FindPublicParameter(MaterialBase material, string name)
    {
        var parameter = material.Parameters.FirstOrDefault(candidate => candidate.Name == name) ??
            throw new ArgumentException($"Material parameter '{name}' does not exist.");
        if (!parameter.IsPublic)
            throw new ArgumentException($"Material parameter '{name}' is not public.");
        return parameter;
    }

    private static object ReadMaterialParameterValue(string type, JsonElement value, string name)
    {
        return type switch
        {
            "Color" => ReadColor(value, name),
            "Float" => ReadFiniteFloat(value, name),
            "Vector2" => ReadFiniteFloat2(value, name),
            "Vector3" => ReadFiniteFloat3(value, name),
            "Vector4" => ReadFiniteFloat4(value, name),
            "Texture" => LoadParameterTexture(value, name),
            "Bool" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            "Bool" => throw new ArgumentException($"Material parameter '{name}' requires a boolean value."),
            _ => throw new NotSupportedException($"Material parameter '{name}' has unsupported type '{type}'."),
        };
    }

    private static float ReadFiniteFloat(JsonElement value, string name)
    {
        var result = value.GetSingle();
        if (!float.IsFinite(result))
            throw new ArgumentException($"Material parameter '{name}' must be finite.");
        return result;
    }

    private static Float2 ReadFiniteFloat2(JsonElement value, string name)
    {
        var result = new Float2(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle());
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y))
            throw new ArgumentException($"Material parameter '{name}' components must be finite.");
        return result;
    }

    private static Float3 ReadFiniteFloat3(JsonElement value, string name)
    {
        var result = new Float3(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle(),
            value.GetProperty("z").GetSingle());
        if (!IsFinite(result))
            throw new ArgumentException($"Material parameter '{name}' components must be finite.");
        return result;
    }

    private static Float4 ReadFiniteFloat4(JsonElement value, string name)
    {
        var result = new Float4(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle(),
            value.GetProperty("z").GetSingle(), value.GetProperty("w").GetSingle());
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z) || !float.IsFinite(result.W))
            throw new ArgumentException($"Material parameter '{name}' components must be finite.");
        return result;
    }

    private static Texture LoadParameterTexture(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Material parameter '{name}' requires a texture GUID.");
        return LoadTexture(value.GetString(), false) ??
            throw new ArgumentException($"Material parameter '{name}' requires a texture GUID.");
    }

    private static object BuildMaterialInstanceDetails(MaterialInstance instance)
    {
        var baseMaterial = instance.BaseMaterial ??
            throw new InvalidOperationException("The material instance has no base material.");
        return new
        {
            mainThreadId = Globals.MainThreadID,
            materialInstanceId = FormatContentGuid(instance.ID),
            materialInstancePath = instance.Path,
            baseMaterialId = FormatContentGuid(baseMaterial.ID),
            baseMaterialPath = baseMaterial.Path,
            parameters = instance.Parameters.Select(parameter => new
            {
                name = parameter.Name,
                type = parameter.ParameterType.ToString(),
                isOverride = parameter.IsOverride,
                value = BuildMaterialParameterValue(parameter.Value),
            }).ToArray(),
        };
    }

    private static object? BuildMaterialParameterValue(object? value)
    {
        return value switch
        {
            null => null,
            Color color => BuildColor(color),
            Float2 vector => BuildVector2(vector),
            Float3 vector => new { x = vector.X, y = vector.Y, z = vector.Z },
            Float4 vector => new { x = vector.X, y = vector.Y, z = vector.Z, w = vector.W },
            Texture texture => FormatContentGuid(texture.ID),
            Guid id => FormatContentGuid(id),
            _ => value,
        };
    }

    private static void WriteMaterialGraph(
        Material material, Color baseColor, float roughness, float metallic, Color? emissive,
        Texture? baseTexture, Texture? normalTexture, Float2? uvTiling)
    {
        var owner = new MaterialSurfaceOwner(material.LoadSurface(false));
        var surface = new MaterialSurface(owner);
        try
        {
            if (surface.Load())
                throw new InvalidOperationException("The editor-generated material graph could not be loaded.");
            var main = surface.Nodes.Single(node => node.Type == 65537);
            var colorNode = surface.Context.SpawnNode(2, 7, new Float2(-500, -100), [baseColor], null);
            SurfaceNode colorOutput = colorNode;
            var textures = new List<SurfaceNode>();
            if (baseTexture is not null)
            {
                var texture = surface.Context.SpawnNode(5, 1, new Float2(-700, -200), [baseTexture.ID], null);
                var multiply = surface.Context.SpawnNode(3, 3, new Float2(-300, -100), null, null);
                Connect(texture, 1, multiply, 0);
                Connect(colorNode, 0, multiply, 1);
                colorOutput = multiply;
                textures.Add(texture);
            }
            Connect(colorOutput, colorOutput == colorNode ? 0 : 2, main, 1);

            var roughnessNode = surface.Context.SpawnNode(2, 3, new Float2(-300, 100), [roughness], null);
            var metallicNode = surface.Context.SpawnNode(2, 3, new Float2(-300, 180), [metallic], null);
            Connect(roughnessNode, 0, main, 6);
            Connect(metallicNode, 0, main, 4);
            if (emissive.HasValue)
            {
                var emissiveNode = surface.Context.SpawnNode(2, 7, new Float2(-300, 260), [emissive.Value], null);
                Connect(emissiveNode, 0, main, 3);
            }
            if (normalTexture is not null)
            {
                var texture = surface.Context.SpawnNode(5, 4, new Float2(-700, 320), [normalTexture.ID], null);
                Connect(texture, 1, main, 8);
                textures.Add(texture);
            }
            if (uvTiling.HasValue)
            {
                var coordinates = surface.Context.SpawnNode(5, 2, new Float2(-1100, 0), [0u], null);
                var scale = surface.Context.SpawnNode(2, 4, new Float2(-1100, 100), [uvTiling.Value], null);
                var multiply = surface.Context.SpawnNode(3, 3, new Float2(-900, 50), null, null);
                Connect(coordinates, 0, multiply, 0);
                Connect(scale, 0, multiply, 1);
                foreach (var texture in textures)
                    Connect(multiply, 2, texture, 0);
            }

            if (surface.Save())
                throw new InvalidOperationException("The material graph could not be serialized.");
            // MaterialInfo.Default defaults to ShadingModel.Unlit, which would make Flax render only the
            // Emissive input and ignore BaseColor/Roughness/Metallic entirely.
            var info = MaterialInfo.Default;
            info.ShadingModel = MaterialShadingModel.Lit;
            if (material.SaveSurface(owner.SurfaceData, info))
                throw new InvalidOperationException("The material graph could not be saved.");
            // Without an explicit reload, actors that bind this brand-new material right after creation
            // render it as solid black -- its GPU-side compiled representation isn't picked up otherwise.
            material.Reload();
        }
        finally
        {
            surface.Dispose();
        }
    }

    private static object BuildMaterialDetails(Material material)
    {
        var owner = new MaterialSurfaceOwner(material.LoadSurface(false));
        var surface = new MaterialSurface(owner);
        try
        {
            if (surface.Load())
                throw new InvalidOperationException("The material graph could not be loaded.");
            var main = surface.Nodes.Single(node => node.Type == 65537);
            var colorSource = GetSource(main, 1);
            var baseTexture = FindNode(colorSource, 5, 1);
            var colorNode = FindNode(colorSource, 2, 7) ??
                throw UnsupportedMaterialGraph("base color");
            var roughness = GetRequiredFloatValue(main, 6, "roughness");
            var metallic = GetRequiredFloatValue(main, 4, "metallic");
            var normalTexture = FindNode(GetSource(main, 8), 5, 4);
            var texture = baseTexture ?? normalTexture;
            var uvMultiply = texture is null ? null : GetSource(texture, 0);
            var tilingNode = FindNode(uvMultiply, 2, 4);
            var emissiveNode = FindNode(GetSource(main, 3), 2, 7);
            return new
            {
                mainThreadId = Globals.MainThreadID,
                materialId = FormatContentGuid(material.ID),
                materialPath = material.Path,
                baseColor = BuildColor((Color)colorNode.Values[0]),
                roughness,
                metallic,
                emissiveColor = emissiveNode?.Values?[0] is Color emissive ? BuildColor(emissive) : null,
                baseColorTextureId = baseTexture?.Values?[0] is Guid baseId ? FormatContentGuid(baseId) : null,
                normalTextureId = normalTexture?.Values?[0] is Guid normalId ? FormatContentGuid(normalId) : null,
                uvTiling = tilingNode?.Values?[0] is Float2 tiling ? BuildVector2(tiling) : null,
                parameters = material.Parameters.Select(parameter => new
                {
                    name = parameter.Name,
                    type = parameter.ParameterType.ToString(),
                    isPublic = parameter.IsPublic,
                    isOverride = parameter.IsOverride,
                }).ToArray(),
            };
        }
        finally
        {
            surface.Dispose();
        }
    }

    private static void Connect(SurfaceNode source, int sourceBox, SurfaceNode target, int targetBox)
    {
        target.GetBox(targetBox).CreateConnection(source.GetBox(sourceBox));
    }

    private static SurfaceNode? GetSource(SurfaceNode node, int inputBox)
    {
        return node.GetBox(inputBox).Connections.FirstOrDefault()?.ParentNode;
    }

    private static SurfaceNode? FindNode(SurfaceNode? node, ushort group, ushort type)
    {
        if (node is null)
            return null;
        if (node.GroupArchetype.GroupID == group && node.Archetype.TypeID == type)
            return node;
        return node.Elements.OfType<FlaxEditor.Surface.Elements.Box>()
            .SelectMany(box => box.Connections)
            .Select(connection => connection.ParentNode)
            .FirstOrDefault(candidate => candidate.GroupArchetype.GroupID == group && candidate.Archetype.TypeID == type);
    }

    private static float GetRequiredFloatValue(SurfaceNode main, int inputBox, string propertyName)
    {
        return GetSource(main, inputBox)?.Values?[0] is float value ?
            value :
            throw UnsupportedMaterialGraph(propertyName);
    }

    private static InvalidOperationException UnsupportedMaterialGraph(string propertyName)
    {
        return new InvalidOperationException(
            $"The material graph uses an unsupported {propertyName} expression; no single effective value can be reported."
        );
    }

    private static string ResolveContentPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            !string.Equals(Path.GetExtension(relativePath), ".flax", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("relativePath must be a relative Content path ending in .flax.");
        }
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            throw new ArgumentException("relativePath cannot contain empty, '.' or '..' path segments.");
        var root = Path.GetFullPath(Globals.ProjectContentFolder);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("relativePath must stay within the project's Content directory.");
        }
        return path;
    }

    private static bool TryParseContentGuid(string value, out Guid result)
    {
        result = default;
        if (value.Length != 32)
            return false;
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < 4; index++)
        {
            if (!uint.TryParse(value.AsSpan(index * 8, 8), System.Globalization.NumberStyles.HexNumber,
                    null, out var chunk))
                return false;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(index * 4, 4), chunk);
        }
        result = new Guid(bytes);
        return true;
    }

    private static string FormatContentGuid(Guid value)
    {
        var bytes = value.ToByteArray();
        return string.Concat(Enumerable.Range(0, 4)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4)).ToString("x8")));
    }

    private static Texture? LoadTexture(string? textureId, bool requireNormalMap)
    {
        if (textureId is null)
            return null;
        if (!TryParseContentGuid(textureId, out var id))
            throw new ArgumentException($"Texture id '{textureId}' is not a valid GUID.");
        var texture = Content.LoadAsync<Texture>(id) ??
            throw new KeyNotFoundException($"Texture asset '{textureId}' does not exist or is not a FlaxEngine.Texture.");
        if (texture.WaitForLoaded(AssetLoadTimeoutMilliseconds) || !texture.IsLoaded)
            throw new InvalidOperationException($"Texture asset '{textureId}' could not be loaded.");
        if (requireNormalMap && !texture.IsNormalMap)
            throw new InvalidOperationException($"Texture asset '{textureId}' is not imported as a normal map.");
        return texture;
    }

    private static Color ReadColor(JsonElement value, string name)
    {
        var color = new Color(
            value.GetProperty("r").GetSingle(), value.GetProperty("g").GetSingle(),
            value.GetProperty("b").GetSingle(), value.GetProperty("a").GetSingle()
        );
        ValidateUnitValue(color.R, $"{name}.r");
        ValidateUnitValue(color.G, $"{name}.g");
        ValidateUnitValue(color.B, $"{name}.b");
        ValidateUnitValue(color.A, $"{name}.a");
        return color;
    }

    private static Float2 ReadPositiveFloat2(JsonElement value, string name)
    {
        var result = new Float2(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle());
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || result.X <= 0 || result.Y <= 0)
            throw new ArgumentException($"{name} components must be finite and greater than zero.");
        return result;
    }

    private static void ValidateUnitValue(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be finite and in the 0-1 range.");
    }

    private static object BuildColor(Color color)
    {
        return new { r = color.R, g = color.G, b = color.B, a = color.A };
    }

    private static object BuildVector2(Float2 value)
    {
        return new { x = value.X, y = value.Y };
    }

    private static void RefreshContentDatabase()
    {
        var database = FlaxEditor.Editor.Instance.ContentDatabase;
        database.RefreshFolder(database.Game.Folder, true);
    }

    private sealed class MaterialSurfaceOwner : IVisjectSurfaceOwner
    {
        public MaterialSurfaceOwner(byte[] surfaceData)
        {
            SurfaceData = surfaceData;
        }

        public Asset? SurfaceAsset => null;
        public string? SurfaceName => null;
        public FlaxEditor.Undo? Undo => null;
        public byte[] SurfaceData { get; set; }
        public VisjectSurfaceContext? ParentContext => null;

        public void OnContextCreated(VisjectSurfaceContext context) { }
        public void OnSurfaceEditedChanged() { }
        public void OnSurfaceGraphEdited() { }
        public void OnSurfaceClose() { }
    }

    private static object BuildStaticModelDetails(StaticModel actor)
    {
        var model = actor.Model;
        return new
        {
            mainThreadId = Globals.MainThreadID,
            actor = BuildActorDetails(actor),
            modelId = model is null ? null : FormatContentGuid(model.ID),
            modelPath = model?.Path,
            modelIsLoaded = model?.IsLoaded ?? false,
        };
    }

    private static Task<object> GetBoxColliderDetailsAsync(string actorId)
    {
        return InvokeOnUpdateAsync(() => BuildBoxColliderDetails(FindBoxCollider(actorId)));
    }

    private static Task<object> CreateBoxColliderAsync(
        string parentId, string name, JsonElement size, JsonElement center, bool isTrigger)
    {
        var properties = ReadBoxColliderProperties(size, center, isTrigger);
        return InvokeOnUpdateAsync(() =>
        {
            ValidateActorName(name);
            var parent = ResolveDestination(null, parentId);
            var actor = new BoxCollider
            {
                Size = properties.Size,
                Center = properties.Center,
                IsTrigger = properties.IsTrigger,
            };
            SpawnActor(actor, name, parent, parent.Transform);
            return BuildBoxColliderDetails(actor);
        });
    }

    private static Task<object> SetBoxColliderAsync(
        string actorId, JsonElement size, JsonElement center, bool isTrigger)
    {
        var properties = ReadBoxColliderProperties(size, center, isTrigger);
        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindBoxCollider(actorId);
            FlaxEditor.Editor.Instance.SceneEditing.Undo.RecordAction(actor, "Set box collider", () =>
            {
                actor.Size = properties.Size;
                actor.Center = properties.Center;
                actor.IsTrigger = properties.IsTrigger;
                MarkSceneEdited(actor);
            });
            return BuildBoxColliderDetails(actor);
        });
    }

    private static BoxCollider FindBoxCollider(string actorId)
    {
        return FindActor(actorId) as BoxCollider ??
            throw new InvalidOperationException($"Actor '{actorId}' is not a FlaxEngine.BoxCollider.");
    }

    private static (Float3 Size, Vector3 Center, bool IsTrigger) ReadBoxColliderProperties(
        JsonElement size, JsonElement center, bool isTrigger)
    {
        var parsedSize = ReadFloat3(size);
        var parsedCenter = ReadVector3(center);
        if (!IsFinite(parsedSize) || parsedSize.X <= 0 || parsedSize.Y <= 0 || parsedSize.Z <= 0)
        {
            throw new ArgumentException("BoxCollider size components must be finite and greater than zero.");
        }
        if (!IsFinite(parsedCenter))
        {
            throw new ArgumentException("BoxCollider center components must be finite.");
        }
        return (parsedSize, parsedCenter, isTrigger);
    }

    private static Float3 ReadFloat3(JsonElement value)
    {
        return new Float3(
            value.GetProperty("x").GetSingle(),
            value.GetProperty("y").GetSingle(),
            value.GetProperty("z").GetSingle()
        );
    }

    private static Vector3 ReadVector3(JsonElement value)
    {
        return new Vector3(
            value.GetProperty("x").GetSingle(),
            value.GetProperty("y").GetSingle(),
            value.GetProperty("z").GetSingle()
        );
    }

    private static bool IsFinite(Float3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static object BuildBoxColliderDetails(BoxCollider actor)
    {
        return new
        {
            mainThreadId = Globals.MainThreadID,
            actor = BuildActorDetails(actor),
            size = BuildVector3(actor.Size.X, actor.Size.Y, actor.Size.Z),
            center = BuildVector3(actor.Center.X, actor.Center.Y, actor.Center.Z),
            isTrigger = actor.IsTrigger,
        };
    }

    private static object BuildVector3(float x, float y, float z)
    {
        return new { x, y, z };
    }

    private static Actor FindActor(string actorId)
    {
        if (!Guid.TryParse(actorId, out var id))
        {
            throw new ArgumentException($"Actor id '{actorId}' is not a valid GUID.");
        }

        return Level.FindActor(id) ??
            throw new KeyNotFoundException($"Actor '{actorId}' is not loaded in the editor.");
    }

    private static void MarkSceneEdited(Actor actor)
    {
        if (actor.Scene is { } scene)
        {
            FlaxEditor.Editor.Instance.Scene.MarkSceneEdited(scene);
        }
    }

    private static void ValidateActorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Actor name cannot be empty.");
        }
    }

    private static Actor ResolveDestination(string? sceneId, string? parentId)
    {
        if ((sceneId is null) == (parentId is null))
        {
            throw new ArgumentException("Set exactly one of sceneId or parentId.");
        }

        Actor destination;
        if (parentId is not null)
        {
            if (!Guid.TryParse(parentId, out var id))
            {
                throw new ArgumentException($"Parent actor id '{parentId}' is not a valid GUID.");
            }
            destination = Level.FindActor(id) ??
                throw new KeyNotFoundException($"Parent actor '{parentId}' is not loaded in the editor.");
        }
        else
        {
            if (!Guid.TryParse(sceneId, out var id))
            {
                throw new ArgumentException($"Scene id '{sceneId}' is not a valid GUID.");
            }
            destination = Level.Scenes.FirstOrDefault(scene => scene.ID == id) ??
                throw new KeyNotFoundException($"Scene '{sceneId}' is not loaded in the editor.");
        }

        return destination;
    }

    private static object SpawnActor(Actor actor, string name, Actor destination, Transform transform)
    {
        actor.Name = name;
        actor.Transform = transform;
        FlaxEditor.Editor.Instance.SceneEditing.Spawn(actor, destination, -1, false);
        return BuildActorDetails(actor);
    }

    private static Transform ReadTransform(JsonElement value)
    {
        var translation = value.GetProperty("translation");
        var orientation = value.GetProperty("orientation");
        var scale = value.GetProperty("scale");
        return new Transform(
            new Vector3(
                translation.GetProperty("x").GetSingle(),
                translation.GetProperty("y").GetSingle(),
                translation.GetProperty("z").GetSingle()
            ),
            new Quaternion(
                orientation.GetProperty("x").GetSingle(),
                orientation.GetProperty("y").GetSingle(),
                orientation.GetProperty("z").GetSingle(),
                orientation.GetProperty("w").GetSingle()
            ),
            new Vector3(
                scale.GetProperty("x").GetSingle(),
                scale.GetProperty("y").GetSingle(),
                scale.GetProperty("z").GetSingle()
            )
        );
    }

    private static object BuildActorDetails(Actor actor)
    {
        return new
        {
            mainThreadId = Globals.MainThreadID,
            id = actor.ID.ToString("D"),
            typeName = actor.GetType().FullName ?? actor.GetType().Name,
            name = actor.Name,
            parentId = actor.Parent?.ID.ToString("D"),
            sceneId = actor.Scene?.ID.ToString("D"),
            isActive = actor.IsActive,
            isActiveInHierarchy = actor.IsActiveInHierarchy,
            layer = actor.Layer,
            layerName = actor.LayerName,
            tags = actor.Tags.Select(tag => tag.ToString()).ToList(),
            transform = BuildTransform(actor.Transform),
            localTransform = BuildTransform(actor.LocalTransform),
            scripts = actor.Scripts.Select(script => new
            {
                id = script.ID.ToString("D"),
                typeName = script.GetType().FullName ?? script.GetType().Name,
                enabled = script.Enabled,
                isEnabledInHierarchy = script.IsEnabledInHierarchy,
            }).ToList(),
        };
    }

    private static object BuildTransform(Transform transform)
    {
        return new
        {
            translation = new
            {
                x = transform.Translation.X,
                y = transform.Translation.Y,
                z = transform.Translation.Z,
            },
            orientation = new
            {
                x = transform.Orientation.X,
                y = transform.Orientation.Y,
                z = transform.Orientation.Z,
                w = transform.Orientation.W,
            },
            scale = new
            {
                x = transform.Scale.X,
                y = transform.Scale.Y,
                z = transform.Scale.Z,
            },
        };
    }

    private static Task<object> InvokeOnUpdateAsync(Func<object> action)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripting.InvokeOnUpdate(() =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static object BuildSceneNode(Actor actor, int depth, SceneGraphState state)
    {
        state.NodeCount++;
        var children = new List<object>();

        if (actor.Children.Any() && depth >= MaxSceneGraphDepth)
        {
            state.Truncated = true;
        }
        else
        {
            foreach (var child in actor.Children)
            {
                if (state.NodeCount >= MaxSceneGraphNodes)
                {
                    state.Truncated = true;
                    break;
                }

                children.Add(BuildSceneNode(child, depth + 1, state));
            }
        }

        return new
        {
            id = actor.ID.ToString(),
            typeName = actor.GetType().FullName ?? actor.GetType().Name,
            name = actor.Name,
            children,
        };
    }

    private sealed class SceneGraphState
    {
        public int NodeCount;
        public bool Truncated;
    }

    private sealed record PlayModeState(int MainThreadId, bool IsPlayMode, bool IsPaused)
    {
        public PlayModeState(bool isPlayMode, bool isPaused)
            : this(0, isPlayMode, isPaused)
        {
        }
    }

    private static Task<object> SaveAsync()
    {
        return InvokeOnUpdateAsync(() =>
        {
            var editor = FlaxEditor.Editor.Instance;
            if (FlaxEditor.Editor.IsPlayMode)
                throw new InvalidOperationException(
                    "The editor is in play mode: stop it before saving, or the play-time scene is written.");
            // Changes made through the API do not mark a scene as modified, and SaveAll skips unmodified scenes.
            foreach (var scene in Level.Scenes)
                editor.Scene.MarkSceneEdited(scene);
            editor.SaveAll();
            return new { mainThreadId = Globals.MainThreadID, saved = true, savedScenes = Level.ScenesCount };
        });
    }

    private static async Task<object> GetPlayModeStateAsync()
    {
        var state = await ReadPlayModeStateAsync();
        return new
        {
            mainThreadId = state.MainThreadId,
            requestedAction = "state",
            isPlayMode = state.IsPlayMode,
            isPaused = state.IsPaused,
        };
    }

    private static object CompileStatus() => new
    {
        isCompiling = FlaxEditor.ScriptsBuilder.IsCompiling,
        lastCompilationFailed = FlaxEditor.ScriptsBuilder.LastCompilationFailed,
        compilationsCount = FlaxEditor.ScriptsBuilder.CompilationsCount,
        loadedUtcTicks = LoadedUtcTicks,
    };

    private static Task<object> CompileAsync()
    {
        return InvokeOnUpdateAsync(() =>
        {
            if (FlaxEditor.Editor.IsPlayMode)
                throw new InvalidOperationException("The editor is in play mode: stop it before compiling.");
            var before = CompileStatus();
            FlaxEditor.ScriptsBuilder.Compile();
            return before;
        });
    }

    private static Task<object> GetCompileStatusAsync()
    {
        return InvokeOnUpdateAsync(CompileStatus);
    }

    private static async Task<object> OpenSceneAsync(string path)
    {
        var item = (AssetItem)await InvokeOnUpdateAsync(() =>
        {
            if (FlaxEditor.Editor.IsPlayMode)
                throw new InvalidOperationException("The editor is in play mode: stop it before opening a scene.");
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Globals.ProjectContentFolder, path);
            var found = FlaxEditor.Editor.Instance.ContentDatabase.Find(full) as AssetItem ??
                throw new FileNotFoundException($"No scene asset at '{full}'.");
            FlaxEditor.Editor.Instance.Scene.OpenScene(found.ID);
            return found;
        });
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var opened = await InvokeOnUpdateAsync(() =>
            {
                var scene = Level.Scenes.FirstOrDefault(candidate => candidate.ID == item.ID);
                if (scene is null)
                    return (object)null!;
                return new
                {
                    mainThreadId = Globals.MainThreadID,
                    sceneId = scene.ID.ToString(),
                    sceneName = scene.Name,
                    scenes = Level.Scenes.Select(candidate => candidate.Name).ToArray(),
                };
            });
            if (opened is not null)
                return opened;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The scene '{path}' did not finish loading within 3 seconds.");
    }

    private static async Task<object> SetPlayModeAsync(string action)
    {
        var normalizedAction = action.ToLowerInvariant();
        _ = normalizedAction switch
        {
            "start" => new PlayModeState(true, false),
            "stop" => new PlayModeState(false, false),
            "pause" => new PlayModeState(true, true),
            "resume" => new PlayModeState(true, false),
            _ => throw new ArgumentException("Play mode action must be start, stop, pause, or resume."),
        };

        // The request is queued, not awaited: starting play loads the scene on the main thread, which can take longer
        // than one request may wait; the server polls play_mode_state until the state is reached.
        var state = await ReadPlayModeStateAsync();
        Scripting.InvokeOnUpdate(() =>
        {
            var editor = FlaxEditor.Editor.Instance;
            switch (normalizedAction)
            {
                case "start":
                    editor.Simulation.RequestStartPlayScenes();
                    break;
                case "stop":
                    editor.Simulation.RequestStopPlay();
                    break;
                case "pause":
                    editor.Simulation.RequestPausePlay();
                    break;
                case "resume":
                    editor.Simulation.RequestResumePlay();
                    break;
            }
        });
        return new
        {
            mainThreadId = state.MainThreadId,
            requestedAction = normalizedAction,
            isPlayMode = state.IsPlayMode,
            isPaused = state.IsPaused,
        };
    }

    private static Task<PlayModeState> ReadPlayModeStateAsync()
    {
        var tcs = new TaskCompletionSource<PlayModeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripting.InvokeOnUpdate(() =>
        {
            var editor = FlaxEditor.Editor.Instance;
            var playingState = editor.StateMachine.CurrentState as FlaxEditor.States.PlayingState;
            tcs.TrySetResult(new PlayModeState(
                checked((int)Globals.MainThreadID),
                FlaxEditor.Editor.IsPlayMode,
                playingState?.IsPaused ?? false
            ));
        });
        return tcs.Task;
    }

    private static async Task<object> CaptureScreenshotAsync(string path)
    {
        var captureQueued = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scripting.InvokeOnUpdate(() =>
        {
            try
            {
                if (Engine.IsHeadless)
                {
                    throw new InvalidOperationException(
                        "Screenshot capture is unavailable in headless mode because no rendered viewport output exists.");
                }

                var sceneWindow = FlaxEditor.Editor.Instance.Windows.Windows
                    .OfType<EditGameWindow>()
                    .FirstOrDefault(window => window.IsSelected && window.VisibleInHierarchy && !window.IsHidden);
                var renderTask = sceneWindow?.Viewport.Task;
                if (renderTask?.Output is null)
                {
                    throw new InvalidOperationException(
                        "Screenshot capture requires a visible scene viewport with allocated render output.");
                }

                Screenshot.Capture(renderTask, path);
                captureQueued.TrySetResult(new object());
            }
            catch (Exception ex)
            {
                captureQueued.TrySetException(ex);
            }
        });
        await captureQueued.Task;

        // Capture is queued for the end of the frame by the engine; poll briefly for the file.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return new { path, bytes = new FileInfo(path).Length };
            }
        }

        throw new IOException("Screenshot file was not produced within 2 seconds of the viewport capture request.");
    }

    private static async Task<object> ExecuteCSharpAsync(string code)
    {
        Task<(object Result, WeakReference LoadContext)>? executionTask = ExecuteInCollectibleContextAsync(code);
        var execution = await executionTask;
        executionTask = null;
        await InvokeOnUpdateAsync(() => new object());
        for (var attempt = 0; execution.LoadContext.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (execution.LoadContext.IsAlive)
        {
            throw new InvalidOperationException("The dynamic C# assembly could not be unloaded.");
        }

        return execution.Result;
    }

    private static string? ReferencePath(Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location))
            return assembly.Location;
        var binaries = Path.Combine(Globals.ProjectFolder, "Binaries");
        var name = assembly.GetName().Name;
        if (name is null || !Directory.Exists(binaries))
            return null;
        return Directory.EnumerateFiles(binaries, name + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(object Result, WeakReference LoadContext)> ExecuteInCollectibleContextAsync(string code)
    {
        const string typeName = "FlaxMcpBridge.Dynamic.CodeExecution";
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using FlaxEngine;
            using FlaxEditor;

            namespace FlaxMcpBridge.Dynamic;

            public static class CodeExecution
            {
                public static object? Execute()
                {
                    {{code}}
                }
            }
            """;
        // A script reload that cannot unload the old assemblies leaves several copies of the game's assemblies loaded; only
        // the newest copy of each name is referenced, or a snippet naming a game type is ambiguous.
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .GroupBy(assembly => assembly.GetName().Name)
            .Select(group => group.Last())
            .ToArray();
        // The game's script assemblies are loaded from memory (no Location): they are referenced through their newest
        // built file, so a snippet can name a game type.
        var references = loadedAssemblies
            .Select(ReferencePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "FlaxMcpDynamic_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString());
            throw new InvalidOperationException("C# compilation failed: " + string.Join(Environment.NewLine, errors));
        }

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver = (_, assemblyName) => loadedAssemblies.FirstOrDefault(
            assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName)
        );
        loadContext.Resolving += resolver;
        var loadContextReference = new WeakReference(loadContext);
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var method = assembly.GetType(typeName)?.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static) ??
                throw new InvalidOperationException("Compiled C# entry point was not found.");
            var methodId = Interlocked.Increment(ref _nextDynamicMethodId);
            DynamicMethods[methodId] = method;
            try
            {
                var result = await InvokeOnUpdateAsync(() => ExecuteDynamicMethod(methodId));
                return (result, loadContextReference);
            }
            finally
            {
                DynamicMethods.TryRemove(methodId, out _);
            }
        }
        finally
        {
            loadContext.Resolving -= resolver;
            loadContext.Unload();
        }
    }

    private static object ExecuteDynamicMethod(int methodId)
    {
        if (!DynamicMethods.TryRemove(methodId, out var method))
        {
            throw new InvalidOperationException("The compiled C# entry point is unavailable.");
        }

        try
        {
            var result = method.Invoke(null, null);
            return new
            {
                mainThreadId = checked((int)Globals.MainThreadID),
                typeName = result?.GetType().FullName,
                result = result is null ?
                    (JsonElement?)null :
                    SerializeDynamicResult(result),
            };
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static JsonElement SerializeDynamicResult(object result)
    {
        var normalized = NormalizeDynamicResult(result, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static object? NormalizeDynamicResult(object? value, HashSet<object> visited, int depth)
    {
        if (value is JsonElement element)
        {
            return element.Clone();
        }
        if (value is JsonDocument document)
        {
            return document.RootElement.Clone();
        }
        if (value is null || value is string || value is bool || value is char ||
            value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal || value is Guid ||
            value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Uri)
        {
            return value;
        }
        if (value.GetType().IsEnum)
        {
            return Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (depth >= 32)
        {
            throw new InvalidOperationException("The dynamic C# result exceeds the maximum serialization depth of 32.");
        }
        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            throw new InvalidOperationException("The dynamic C# result contains a reference cycle.");
        }

        try
        {
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ??
                        throw new InvalidOperationException("The dynamic C# result contains a null dictionary key.");
                    result.Add(key, NormalizeDynamicResult(entry.Value, visited, depth + 1));
                }
                return result;
            }
            if (value is IEnumerable enumerable)
            {
                var result = new List<object?>();
                foreach (var item in enumerable)
                {
                    result.Add(NormalizeDynamicResult(item, visited, depth + 1));
                }
                return result;
            }

            return value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .ToDictionary(
                    property => property.Name,
                    property => NormalizeDynamicResult(property.GetValue(value), visited, depth + 1)
                );
        }
        finally
        {
            if (!value.GetType().IsValueType)
            {
                visited.Remove(value);
            }
        }
    }

    private void WriteHandshakeFile()
    {
        var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlaxMcp", "sessions");
        Directory.CreateDirectory(sessionsDir);
        _handshakePath = Path.Combine(sessionsDir, Hash(_projectFolder) + ".json");

        var handshake = new Dictionary<string, object?>
        {
            ["pipeName"] = PipeName,
            ["protocolVersion"] = ProtocolVersion,
            ["pid"] = Environment.ProcessId,
            ["projectFolder"] = _projectFolder,
            ["pluginVersion"] = typeof(PipeBridgeServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["engineBuild"] = Globals.EngineBuildNumber,
            ["startedUtc"] = DateTime.UtcNow,
        };
        File.WriteAllText(_handshakePath, JsonSerializer.Serialize(handshake));
    }

    private static string Hash(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        var acceptLoopReference = StopAcceptLoop();
        _cts.Dispose();
        for (var attempt = 0; acceptLoopReference?.IsAlive == true && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (_handshakePath is not null && File.Exists(_handshakePath))
        {
            try { File.Delete(_handshakePath); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? StopAcceptLoop()
    {
        _cts.Cancel();
        _currentPipe?.Dispose();

        var acceptLoop = _acceptLoop;
        _acceptLoop = null;
        _currentPipe = null;
        try
        {
            acceptLoop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation unwinds through the accept loop.
        }

        return acceptLoop is null ? null : new WeakReference(acceptLoop);
    }
}
