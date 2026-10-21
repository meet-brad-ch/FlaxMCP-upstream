using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlaxEngine;

namespace FlaxMcpBridge.Editor;

/// <summary>
/// Typed access to live objects, for what snippets were used for most: finding actors, reading and writing a script's
/// or an actor's members, calling a method, adding a script.
/// </summary>
internal sealed partial class PipeBridgeServer
{
    private const int MaxFoundActors = 500;
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly JsonSerializerOptions MemberValueOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task<object> FindActorsAsync(string? nameContains, string? actorType, string? scriptType, int limit)
    {
        if (limit < 1 || limit > MaxFoundActors)
            throw new ArgumentException($"limit must be 1 to {MaxFoundActors}, not {limit}.");
        return InvokeOnUpdateAsync(() =>
        {
            var found = new List<object>();
            var truncated = false;
            foreach (var scene in Level.Scenes)
            {
                Visit(scene, scene.Name);
            }

            return new { mainThreadId = Globals.MainThreadID, actors = found, truncated };

            void Visit(Actor actor, string path)
            {
                if (Matches(actor))
                {
                    if (found.Count >= limit)
                    {
                        truncated = true;
                        return;
                    }

                    found.Add(new
                    {
                        id = actor.ID.ToString(),
                        name = actor.Name,
                        typeName = actor.GetType().FullName,
                        path,
                        scripts = actor.Scripts.Select(script => script.GetType().FullName).ToArray(),
                    });
                }

                foreach (var child in actor.Children)
                {
                    Visit(child, path + "/" + child.Name);
                }
            }
        });

        bool Matches(Actor actor) =>
            (nameContains is null || actor.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) &&
            (actorType is null || IsNamed(actor.GetType(), actorType)) &&
            (scriptType is null || actor.Scripts.Any(script => IsNamed(script.GetType(), scriptType)));
    }

    private static Task<object> GetMemberAsync(string actorId, string? scriptType, string member)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var target = FindTarget(actorId, scriptType);
            var value = MemberPath(target, member).Aggregate((object?)target, (owner, info) => ValueOf(info, owner!));
            return new
            {
                mainThreadId = Globals.MainThreadID,
                targetId = target.ID.ToString(),
                targetType = target.GetType().FullName,
                member,
                typeName = value?.GetType().FullName,
                value = MemberJson(value),
            };
        });
    }

    private static Task<object> SetMemberAsync(string actorId, string? scriptType, string member, JsonElement value)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var target = FindTarget(actorId, scriptType);
            var path = MemberPath(target, member);
            var converted = ConvertJson(value, TypeOf(path[^1]), member);
            // In edit mode the change goes through the editor's undo (a snapshot before and after) and marks the scene
            // edited; in play mode it lasts until play ends.
            var recordedUndo = !FlaxEditor.Editor.IsPlayMode;
            if (recordedUndo)
            {
                FlaxEditor.Editor.Instance.Undo.RecordAction(target, "Set " + member, () => SetPath(target, path, 0, converted));
                var scene = (target as Actor)?.Scene ?? (target as Script)?.Actor?.Scene;
                if (scene is not null)
                    FlaxEditor.Editor.Instance.Scene.MarkSceneEdited(scene);
            }
            else
            {
                SetPath(target, path, 0, converted);
            }

            var now = path.Aggregate((object?)target, (owner, info) => ValueOf(info, owner!));
            return new
            {
                mainThreadId = Globals.MainThreadID,
                targetId = target.ID.ToString(),
                targetType = target.GetType().FullName,
                member,
                typeName = now?.GetType().FullName,
                value = MemberJson(now),
                recordedUndo,
            };
        });
    }

    private static Task<object> InvokeMethodAsync(string? typeName, string method, string? actorId, string? scriptType, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args must be a JSON array.");
        return InvokeOnUpdateAsync(() =>
        {
            object? instance = actorId is null ? null : FindTarget(actorId, scriptType);
            var type = instance?.GetType() ??
                       ResolveType(typeName ?? throw new ArgumentException("typeName is needed for a static method (no actorId)."));
            var flags = (instance is null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.FlattenHierarchy;
            var arguments = args.EnumerateArray().ToArray();
            var candidates = type.GetMethods(flags)
                .Where(info => info.Name == method && info.GetParameters().Length == arguments.Length)
                .OrderBy(info => info.GetParameters().Select((parameter, index) => Fit(arguments[index], parameter.ParameterType)).Sum())
                .ToArray();
            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                object?[] converted;
                try
                {
                    converted = arguments.Select((argument, index) => ConvertJson(argument, parameters[index].ParameterType, parameters[index].Name ?? "?")).ToArray();
                }
                catch (BridgeRequestException)
                {
                    continue;   // the arguments fit another overload, or none: answered below
                }

                object? result;
                try
                {
                    result = candidate.Invoke(instance, converted);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    throw new BridgeRequestException("method_failed", $"{ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }

                return new
                {
                    mainThreadId = Globals.MainThreadID,
                    method = candidate.ToString(),
                    typeName = result?.GetType().FullName,
                    result = MemberJson(result),
                };
            }

            var overloads = type.GetMethods(flags).Where(info => info.Name == method).Select(info => info.ToString()).ToArray();
            throw new BridgeRequestException(
                "no_matching_method",
                overloads.Length == 0 ?
                    $"{type.FullName} has no {(instance is null ? "static" : "instance")} method '{method}'." :
                    $"No overload of {type.FullName}.{method} takes these {arguments.Length} arguments: {string.Join("; ", overloads)}.");
        });
    }

    private static Task<object> AddScriptAsync(string actorId, string scriptType, int? orderInParent)
    {
        return InvokeOnUpdateAsync(() =>
        {
            var actor = FindActor(actorId);
            var type = ResolveType(scriptType);
            if (!typeof(Script).IsAssignableFrom(type) || type.IsAbstract)
                throw new BridgeRequestException("invalid_script_type", $"{type.FullName} is not a script type that can be added.");
            var script = actor.Scripts.FirstOrDefault(existing => existing.GetType() == type);
            var added = script is null;
            if (added)
            {
                script = actor.AddScript(type);
                if (orderInParent is not null)
                    script.OrderInParent = orderInParent.Value;
                if (!FlaxEditor.Editor.IsPlayMode)
                    FlaxEditor.Editor.Instance.Scene.MarkSceneEdited(actor.Scene);
            }

            return new
            {
                mainThreadId = Globals.MainThreadID,
                actorId = actor.ID.ToString(),
                scriptId = script!.ID.ToString(),
                scriptType = type.FullName,
                added,
                orderInParent = script.OrderInParent,
                scripts = actor.Scripts.Select(existing => existing.GetType().FullName).ToArray(),
            };
        });
    }

    /// <summary>How well a JSON value suits a parameter type (lower is better): a number prefers int, then long, then double.</summary>
    private static int Fit(JsonElement value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return value.ValueKind switch
        {
            JsonValueKind.Number when type == typeof(int) => 0,
            JsonValueKind.Number when type == typeof(long) => 1,
            JsonValueKind.Number when type == typeof(double) => 2,
            JsonValueKind.Number when type == typeof(float) => 3,
            JsonValueKind.String when type == typeof(string) => 0,
            JsonValueKind.True or JsonValueKind.False when type == typeof(bool) => 0,
            _ => 5,
        };
    }

    /// <summary>
    /// A member's value as JSON: an engine object as its id, type and name; a struct (for example, Vector3) by its fields, so
    /// properties that make new structs (Normalized) are not followed; anything else as the snippets serialize it.
    /// </summary>
    private static JsonElement? MemberJson(object? value)
    {
        return value is null ? null : JsonSerializer.SerializeToElement(Plain(value, 0));
    }

    private static object? Plain(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or Enum:
                return value is Enum ? value.ToString() : value;
            case FlaxEngine.Object engineObject:
                return new Dictionary<string, object?>
                {
                    ["id"] = engineObject.ID.ToString(),
                    ["type"] = engineObject.GetType().FullName,
                    ["name"] = (engineObject as Actor)?.Name,
                };
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is decimal)
            return value;
        if (type.IsValueType && depth < 8)
        {
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .ToDictionary(field => field.Name, field => Plain(field.GetValue(value), depth + 1));
        }

        return NormalizeDynamicResult(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    private static int? OptionalIntParam(JsonElement root, string name)
    {
        if (!root.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.TryGetInt32(out var number) ? number : throw new ArgumentException($"params.{name} must be an integer");
    }

    private static FlaxEngine.Object FindTarget(string actorId, string? scriptType)
    {
        var actor = FindActor(actorId);
        if (scriptType is null)
            return actor;
        return actor.Scripts.FirstOrDefault(script => IsNamed(script.GetType(), scriptType)) ??
               throw new BridgeRequestException(
                   "unknown_script",
                   $"{actor.Name} has no {scriptType} script. Scripts: {string.Join(", ", actor.Scripts.Select(script => script.GetType().FullName).DefaultIfEmpty("none"))}.");
    }

    private static bool IsNamed(Type type, string name) => type.FullName == name || type.Name == name;

    /// <summary>
    /// A type by full or short name, from the newest loaded copy of each assembly (a reload that could not unload the
    /// old game assemblies leaves older copies loaded).
    /// </summary>
    private static Type ResolveType(string name)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .GroupBy(assembly => assembly.GetName().Name)
            .Select(group => group.Last())
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.OfType<Type>().ToArray();
                }
            })
            .Where(type => IsNamed(type, name))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new BridgeRequestException("unknown_type", $"No loaded type is named '{name}'."),
            _ => throw new BridgeRequestException("ambiguous_type", $"'{name}' names {matches.Length} types: {string.Join(", ", matches.Select(type => type.FullName))}; use the full name."),
        };
    }

    private static MemberInfo[] MemberPath(object target, string path)
    {
        var members = new List<MemberInfo>();
        var type = target.GetType();
        foreach (var name in path.Split('.'))
        {
            var member = (MemberInfo?)type.GetProperty(name, InstanceMembers) ?? type.GetField(name, InstanceMembers) ??
                         throw new BridgeRequestException(
                             "unknown_member",
                             $"{type.FullName} has no field or property '{name}'. Members: {string.Join(", ", type.GetProperties(InstanceMembers).Select(info => info.Name).Concat(type.GetFields(InstanceMembers).Where(info => !info.Name.Contains('<')).Select(info => info.Name)).Distinct().OrderBy(text => text))}.");
            members.Add(member);
            type = TypeOf(member);
        }

        return members.ToArray();
    }

    private static Type TypeOf(MemberInfo member) => member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    private static object? ValueOf(MemberInfo member, object owner) =>
        member is PropertyInfo property ? property.GetValue(owner) : ((FieldInfo)member).GetValue(owner);

    /// <summary>Sets the last member of a path; a struct on the way is written back into its owner.</summary>
    private static object SetPath(object owner, MemberInfo[] path, int index, object? value)
    {
        var member = path[index];
        var newValue = index == path.Length - 1 ? value : SetPath(ValueOf(member, owner) ?? throw new BridgeRequestException("null_member", $"{member.Name} is null."), path, index + 1, value);
        switch (member)
        {
            case PropertyInfo property when property.CanWrite:
                property.SetValue(owner, newValue);
                break;
            case FieldInfo field when !field.IsInitOnly:
                field.SetValue(owner, newValue);
                break;
            default:
                if (index == path.Length - 1 || TypeOf(member).IsValueType)
                    throw new BridgeRequestException("read_only_member", $"{member.Name} cannot be written.");
                break;
        }

        return owner;
    }

    private static object? ConvertJson(JsonElement value, Type type, string what)
    {
        try
        {
            return value.Deserialize(type, MemberValueOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new BridgeRequestException("invalid_value", $"{what}: {value.GetRawText()} is not a {type.FullName} ({ex.Message}).");
        }
    }
}
