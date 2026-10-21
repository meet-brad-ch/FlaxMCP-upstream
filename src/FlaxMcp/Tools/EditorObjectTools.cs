using System.ComponentModel;
using System.Text.Json;
using FlaxMcp.Configuration;
using FlaxMcp.Flax;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

/// <summary>Typed access to live objects: what editor_execute_csharp snippets were mostly written for.</summary>
[McpServerToolType]
public sealed class EditorObjectTools
{
    private readonly IFlaxEditorObjectClient _client;

    public EditorObjectTools(IFlaxEditorObjectClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "editor_find_actors", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Finds actors in the loaded scenes by name, actor type and script type. " +
        "During play mode, the tool searches the play-time scene. " +
        "All three filters are optional, and the tool applies all the filters that you give. " +
        "Types match by full or short name. " +
        "The result contains the id, name, type, scene path and script types of each actor. " +
        "Truncated is true when more actors than the limit match."
    )]
    public Task<FlaxFoundActors> FindActorsAsync(
        [Description("Part of the actor name. The comparison ignores case.")] string? nameContains = null,
        [Description("The actor type, for example StaticModel or FlaxEngine.Camera.")] string? actorType = null,
        [Description("A script type that the actor has, for example PlayerController.")] string? scriptType = null,
        [Description("The maximum number of results, from 1 to 500.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        return _client.FindActorsAsync(nameContains, actorType, scriptType, limit, cancellationToken);
    }

    [McpServerTool(Name = "editor_get_member", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Reads a field or property of an actor, or of one of its scripts, through a dotted path (for example, Stats.Score). " +
        "The tool also reads private members. " +
        "For an unknown name, the error lists the members that exist."
    )]
    public Task<FlaxMemberValue> GetMemberAsync(
        [Description("The actor id, from editor_find_actors or editor_scene_graph.")] string actorId,
        [Description("The member path, for example Health or Stats.Score.")] string member,
        [Description("Read from the script of this type on the actor, not from the actor.")] string? scriptType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return _client.GetMemberAsync(actorId, scriptType, member, cancellationToken);
    }

    [McpServerTool(Name = "editor_set_member", UseStructuredContent = true)]
    [Description(
        "Writes a field or property of an actor, or of one of its scripts, through a dotted path. " +
        "If the path goes through a struct, the tool writes the struct back. " +
        "The value is JSON in the shape of the member (a number, true, \"text\", an enum name, {\"X\":1,\"Y\":2,\"Z\":3}). " +
        "In edit mode, the change goes through the undo of the editor and marks the scene as edited, so that editor_save keeps it. " +
        "In play mode, the change lasts until play mode ends. " +
        "The tool returns the value that it reads back."
    )]
    public Task<FlaxMemberValue> SetMemberAsync(
        [Description("The actor id.")] string actorId,
        [Description("The member path, for example Enabled or Settings.Speed.")] string member,
        [Description("The new value, as JSON.")] JsonElement value,
        [Description("Write to the script of this type on the actor, not to the actor.")] string? scriptType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return _client.SetMemberAsync(actorId, scriptType, member, value, cancellationToken);
    }

    [McpServerTool(Name = "editor_invoke_method", UseStructuredContent = true)]
    [RequiresCodeExecution]
    [Description(
        "Calls a method on the main thread of the editor. " +
        "The method is an instance method of an actor or of one of its scripts (actorId, scriptType), or a static method of a type (typeName). " +
        "The arguments are a JSON array. " +
        "The tool converts them to the parameter types and calls the overload that has that number of parameters and accepts them. " +
        "The method runs with the permissions of the editor. " +
        "This tool is available only when code execution is allowed."
    )]
    public Task<FlaxMethodResult> InvokeMethodAsync(
        [Description("The name of the method.")] string method,
        [Description("The arguments as a JSON array, for example [] or [2, \"fast\"].")] JsonElement? args = null,
        [Description("The actor that has the instance method, or that has the script with the method.")] string? actorId = null,
        [Description("Call the method on the script of this type on the actor.")] string? scriptType = null,
        [Description("For a static method: the full or short name of the type.")] string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var arguments = args ?? JsonDocument.Parse("[]").RootElement.Clone();
        return _client.InvokeMethodAsync(typeName, method, actorId, scriptType, arguments, cancellationToken);
    }

    [McpServerTool(Name = "editor_add_script", UseStructuredContent = true)]
    [Description(
        "Adds a script of the given type to an actor. " +
        "If the actor already has a script of this type, the tool does nothing. " +
        "In edit mode, the tool marks the scene as edited, so that editor_save keeps the change. " +
        "The tool finds the type by its full or short name in the loaded assemblies."
    )]
    public Task<FlaxAddedScript> AddScriptAsync(
        [Description("The actor id.")] string actorId,
        [Description("The script type, for example MyGame.Testing.TestDriver.")] string scriptType,
        [Description("The position of the script among the scripts of the actor.")] int? orderInParent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptType);
        return _client.AddScriptAsync(actorId, scriptType, orderInParent, cancellationToken);
    }
}
