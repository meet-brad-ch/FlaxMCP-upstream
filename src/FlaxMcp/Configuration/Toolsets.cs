using FlaxMcp.Tools;

namespace FlaxMcp.Configuration;

public static class Toolsets
{
    public const string ServerToolset = "server";

    private static readonly Dictionary<string, Type> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        [ServerToolset] = typeof(ServerInfoTool),
        ["project"] = typeof(ProjectTools),
        ["content"] = typeof(ContentTools),
        ["scene"] = typeof(SceneTools),
        ["build"] = typeof(BuildTools),
        ["logs"] = typeof(LogTools),
        ["editor"] = typeof(EditorTools),
        ["game"] = typeof(GameTools),
        ["events"] = typeof(EditorEventTools),
        ["jobs"] = typeof(JobTools)
    };

    public static IReadOnlyCollection<string> Names => Registry.Keys;

    public static IReadOnlyList<Type> Resolve(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return [.. Registry.Values];
        }

        var selected = new List<Type> { Registry[ServerToolset] };
        var unknown = new List<string>();

        foreach (var name in requested.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                 ))
        {
            if (name.Equals(ServerToolset, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Registry.TryGetValue(name, out var type))
            {
                if (!selected.Contains(type))
                {
                    selected.Add(type);
                }
            }
            else
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"{FlaxMcpOptions.ToolsetsVariable} contains unknown toolsets: {string.Join(", ", unknown)}. " +
                $"Valid toolsets are: {string.Join(", ", Registry.Keys)}."
            );
        }

        return selected;
    }
}
