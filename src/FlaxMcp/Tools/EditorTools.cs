using System.ComponentModel;
using FlaxMcp.Configuration;
using FlaxMcp.Flax;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

[McpServerToolType]
public sealed class EditorTools
{
    private readonly IFlaxBridgeClient _bridgeClient;

    public EditorTools(IFlaxBridgeClient bridgeClient)
    {
        _bridgeClient = bridgeClient;
    }

    [McpServerTool(Name = "editor_scene_graph", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns the live actor tree from every scene loaded in the Flax Editor, including unsaved runtime state. " +
        "Truncated is true when the 500-node or 32-level response limit is reached."
    )]
    public Task<FlaxLiveSceneGraph> GetSceneGraphAsync(CancellationToken cancellationToken)
    {
        return _bridgeClient.GetSceneGraphAsync(cancellationToken);
    }

    [McpServerTool(Name = "editor_get_selection", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the actors currently selected in the live Flax Editor.")]
    public Task<FlaxEditorSelection> GetSelectionAsync(CancellationToken cancellationToken)
    {
        return _bridgeClient.GetSelectionAsync(cancellationToken);
    }

    [McpServerTool(Name = "editor_set_selection", UseStructuredContent = true)]
    [Description(
        "Replaces the live Flax Editor actor selection with the actors identified by GUID. " +
        "Pass an empty list to clear the selection."
    )]
    public Task<FlaxEditorSelection> SetSelectionAsync(
        [Description("Actor GUIDs from editor_scene_graph.")] IReadOnlyList<string> actorIds,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SetSelectionAsync(actorIds, cancellationToken);
    }

    [McpServerTool(Name = "editor_actor_details", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns live identity, hierarchy, activation, layer, tags, world/local transforms, and scripts " +
        "for one actor loaded in the Flax Editor."
    )]
    public Task<FlaxActorDetails> GetActorDetailsAsync(
        [Description("Actor GUID from editor_scene_graph or editor_get_selection.")] string actorId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.GetActorDetailsAsync(actorId, cancellationToken);
    }

    [McpServerTool(Name = "editor_modify_actor", UseStructuredContent = true)]
    [Description(
        "Replaces an existing live actor's world transform and returns its updated details. " +
        "Use editor_actor_details first to preserve transform values that should not change."
    )]
    public Task<FlaxActorDetails> ModifyActorAsync(
        [Description("Actor GUID from editor_scene_graph or editor_get_selection.")] string actorId,
        [Description("Replacement world-space translation.")]
        FlaxVector3 translation,
        [Description("Replacement world-space orientation quaternion.")]
        FlaxQuaternion orientation,
        [Description("Replacement world-space scale.")]
        FlaxVector3 scale,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.ModifyActorAsync(
            actorId,
            new FlaxActorTransform(translation, orientation, scale),
            cancellationToken
        );
    }

    [McpServerTool(Name = "editor_create_actor", UseStructuredContent = true)]
    [Description("Creates an allowlisted EmptyActor or StaticModel in a loaded scene and returns its live details.")]
    public Task<FlaxActorDetails> CreateActorAsync(
        [Description("Allowlisted actor type: EmptyActor or StaticModel.")] string actorType,
        string name,
        string? sceneId,
        string? parentId,
        FlaxVector3 translation,
        FlaxQuaternion orientation,
        FlaxVector3 scale,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.CreateActorAsync(actorType, name, sceneId, parentId,
            new FlaxActorTransform(translation, orientation, scale), cancellationToken);
    }

    [McpServerTool(Name = "editor_duplicate_actor", UseStructuredContent = true)]
    [Description("Duplicates a loaded actor into a loaded scene or parent and returns the duplicate's live details.")]
    public Task<FlaxActorDetails> DuplicateActorAsync(
        string actorId,
        string name,
        string? sceneId,
        string? parentId,
        FlaxVector3 translation,
        FlaxQuaternion orientation,
        FlaxVector3 scale,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.DuplicateActorAsync(actorId, name, sceneId, parentId,
            new FlaxActorTransform(translation, orientation, scale), cancellationToken);
    }

    [McpServerTool(Name = "editor_rename_actor", UseStructuredContent = true)]
    [Description("Renames a loaded actor through the editor Undo/Redo history and returns its updated details.")]
    public Task<FlaxActorDetails> RenameActorAsync(
        [Description("Actor GUID from editor_scene_graph.")] string actorId,
        [Description("New non-empty actor name.")] string name,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.RenameActorAsync(actorId, name, cancellationToken);
    }

    [McpServerTool(Name = "editor_reparent_actor", UseStructuredContent = true)]
    [Description(
        "Moves a loaded actor to another loaded parent or to a loaded scene root through Undo/Redo history. " +
        "Set exactly one of sceneId or parentId and explicitly choose whether to preserve the world transform."
    )]
    public Task<FlaxActorDetails> ReparentActorAsync(
        [Description("Actor GUID from editor_scene_graph.")] string actorId,
        string? sceneId,
        string? parentId,
        bool preserveWorldTransform,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.ReparentActorAsync(
            actorId, sceneId, parentId, preserveWorldTransform, cancellationToken);
    }

    [McpServerTool(Name = "editor_delete_actor", UseStructuredContent = true)]
    [Description(
        "Deletes a loaded actor through the editor Undo/Redo history. " +
        "Set deleteDescendants to true to explicitly delete its complete hierarchy; false rejects actors with children."
    )]
    public Task<FlaxActorDeletionResult> DeleteActorAsync(
        [Description("Actor GUID from editor_scene_graph.")] string actorId,
        bool deleteDescendants,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.DeleteActorAsync(actorId, deleteDescendants, cancellationToken);
    }

    [McpServerTool(Name = "editor_static_model_details", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the assigned model reference and live actor details for a loaded StaticModel actor.")]
    public Task<FlaxStaticModelDetails> GetStaticModelDetailsAsync(
        [Description("StaticModel actor GUID from editor_scene_graph.")] string actorId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.GetStaticModelDetailsAsync(actorId, cancellationToken);
    }

    [McpServerTool(Name = "editor_set_static_model", UseStructuredContent = true)]
    [Description(
        "Assigns an existing FlaxEngine.Model asset to a loaded StaticModel actor through Undo/Redo history. " +
        "The bridge validates that modelId resolves to a compatible model before changing the scene."
    )]
    public Task<FlaxStaticModelDetails> SetStaticModelAsync(
        [Description("StaticModel actor GUID from editor_scene_graph.")] string actorId,
        [Description("FlaxEngine.Model asset GUID from content_search.")] string modelId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SetStaticModelAsync(actorId, modelId, cancellationToken);
    }

    [McpServerTool(Name = "editor_set_static_model_material", UseStructuredContent = true)]
    [Description(
        "Assigns an existing FlaxEngine.MaterialBase asset to one slot of a loaded StaticModel through " +
        "Undo/Redo history. The bridge validates the actor, slot, and material before changing the scene."
    )]
    public Task<FlaxStaticModelMaterialDetails> SetStaticModelMaterialAsync(
        [Description("StaticModel actor GUID from editor_scene_graph.")] string actorId,
        [Description("Zero-based material slot index from the assigned model.")] int slotIndex,
        [Description("FlaxEngine.Material or MaterialInstance asset GUID from content_search.")] string materialId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SetStaticModelMaterialAsync(actorId, slotIndex, materialId, cancellationToken);
    }

    [McpServerTool(Name = "editor_box_collider_details", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the local size, center, trigger mode, and actor details for a loaded BoxCollider.")]
    public Task<FlaxBoxColliderDetails> GetBoxColliderDetailsAsync(
        [Description("BoxCollider actor GUID from editor_scene_graph.")] string actorId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.GetBoxColliderDetailsAsync(actorId, cancellationToken);
    }

    [McpServerTool(Name = "editor_create_box_collider", UseStructuredContent = true)]
    [Description("Creates a BoxCollider under a loaded parent actor and returns its effective properties.")]
    public Task<FlaxBoxColliderDetails> CreateBoxColliderAsync(
        [Description("Loaded parent actor GUID from editor_scene_graph.")] string parentId,
        string name,
        [Description("Positive collider dimensions in local space.")] FlaxVector3 size,
        [Description("Collider center offset in local space.")] FlaxVector3 center,
        bool isTrigger,
        CancellationToken cancellationToken)
    {
        ValidateBoxColliderProperties(size, center);
        return _bridgeClient.CreateBoxColliderAsync(
            parentId, name, size, center, isTrigger, cancellationToken);
    }

    [McpServerTool(Name = "editor_set_box_collider", UseStructuredContent = true)]
    [Description("Updates a loaded BoxCollider's local size, center, and trigger mode through Undo/Redo history.")]
    public Task<FlaxBoxColliderDetails> SetBoxColliderAsync(
        [Description("BoxCollider actor GUID from editor_scene_graph.")] string actorId,
        [Description("Positive collider dimensions in local space.")] FlaxVector3 size,
        [Description("Collider center offset in local space.")] FlaxVector3 center,
        bool isTrigger,
        CancellationToken cancellationToken)
    {
        ValidateBoxColliderProperties(size, center);
        return _bridgeClient.SetBoxColliderAsync(
            actorId, size, center, isTrigger, cancellationToken);
    }

    private static void ValidateBoxColliderProperties(FlaxVector3 size, FlaxVector3 center)
    {
        if (!IsFinite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0)
        {
            throw new ArgumentException("BoxCollider size components must be finite and greater than zero.",
                nameof(size));
        }
        if (!IsFinite(center))
        {
            throw new ArgumentException("BoxCollider center components must be finite.", nameof(center));
        }
    }

    private static bool IsFinite(FlaxVector3 value)
    {
        return double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    }

    [McpServerTool(Name = "editor_save", UseStructuredContent = true)]
    [Description(
        "Saves all open scenes and all changed content assets in the live Flax Editor. " +
        "This includes changes made through the API, which the editor does not mark as changed. " +
        "The tool refuses the request during play mode, because a save then writes the play-time scene, with all the actor scripts that the game created at run time."
    )]
    public Task<FlaxEditorSaveResult> SaveAsync(CancellationToken cancellationToken)
    {
        return _bridgeClient.SaveAsync(cancellationToken);
    }

    [McpServerTool(Name = "editor_play_mode", UseStructuredContent = true)]
    [Description(
        "Controls play mode in the live Flax Editor. " +
        "Action must be start, stop, pause, or resume. " +
        "The tool returns when the editor is in the requested state. " +
        "It waits up to 30 seconds, so that a large scene has time to load."
    )]
    public Task<FlaxEditorPlayModeResult> SetPlayModeAsync(
        [Description("Play mode action: start, stop, pause, or resume.")] string action,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SetPlayModeAsync(action, cancellationToken);
    }

    [McpServerTool(Name = "editor_screenshot", UseStructuredContent = true)]
    [Description(
        "Captures the visible Flax Editor scene viewport to a PNG file. " +
        "The output directory must already exist, and capture is unavailable in headless mode."
    )]
    public Task<FlaxBridgeScreenshot> CaptureScreenshotAsync(
        [Description("Absolute or current-working-directory-relative output path ending in .png.")] string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolvedPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(resolvedPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Screenshot output path must use the .png extension.", nameof(path));
        }

        var directory = Path.GetDirectoryName(resolvedPath)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Screenshot output directory does not exist: {directory}");
        }

        return _bridgeClient.CaptureScreenshotAsync(resolvedPath, cancellationToken);
    }

    [McpServerTool(Name = "editor_compile", UseStructuredContent = true)]
    [Description(
        "Compiles the scripts of the project in the live Flax Editor and waits for the result, for up to 120 seconds. " +
        "The tool stops play mode first. " +
        "The result is 'reloaded' (the scripts and this bridge reloaded), 'unchanged' (nothing to reload), or 'failed'. " +
        "A failed result contains the compiler errors and the build errors from the log (for example, a sharing violation)."
    )]
    public Task<FlaxEditorCompileResult> CompileAsync(CancellationToken cancellationToken)
    {
        return _bridgeClient.CompileAsync(cancellationToken);
    }

    [McpServerTool(Name = "editor_open_scene", UseStructuredContent = true)]
    [Description(
        "Opens a scene in the live Flax Editor and returns when the scene is loaded. " +
        "The path is absolute or relative to the Content folder of the project (for example, Scenes/Main.scene). " +
        "The tool refuses the request during play mode."
    )]
    public Task<FlaxEditorOpenSceneResult> OpenSceneAsync(
        [Description("The scene asset path, absolute or relative to Content/, with the extension .scene.")] string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _bridgeClient.OpenSceneAsync(path, cancellationToken);
    }

    [McpServerTool(Name = "editor_execute_csharp", UseStructuredContent = true)]
    [RequiresCodeExecution]
    [Description(
        "DANGER: Compiles and executes arbitrary C# with the Flax Editor process's full machine permissions. " +
        "The code is the body of a static method, runs on the editor main thread, and must return a JSON-serializable value."
    )]
    public Task<FlaxCodeExecutionResult> ExecuteCSharpAsync(
        [Description("C# method body, for example: return FlaxEngine.Globals.EngineBuildNumber;")] string code,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.ExecuteCSharpAsync(code, cancellationToken);
    }
}
