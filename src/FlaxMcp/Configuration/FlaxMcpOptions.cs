namespace FlaxMcp.Configuration;

public sealed class FlaxMcpOptions
{
    public const string ProjectPathVariable = "FLAX_PROJECT_PATH";
    public const string EnginePathVariable = "FLAX_ENGINE_PATH";
    public const string EditorConfigVariable = "FLAX_EDITOR_CONFIG";
    public const string ToolsetsVariable = "FLAX_TOOLSETS";
    public const string ReadOnlyVariable = "FLAX_READ_ONLY";
    public const string BridgeVariable = "FLAX_BRIDGE";
    public const string AllowCodeExecutionVariable = "FLAX_ALLOW_CODE_EXECUTION";
    public const string LogLevelVariable = "FLAX_LOG_LEVEL";
    public const string DefaultEditorConfig = "Development";
    public const string DefaultBridgeMode = "auto";

    public string ProjectPath { get; set; } = string.Empty;

    public string? EnginePath { get; set; }

    public string EditorConfig { get; set; } = DefaultEditorConfig;

    /// <summary>Whether <see cref="EditorConfig"/> was set (FLAX_EDITOR_CONFIG), not the default.</summary>
    public bool EditorConfigSet { get; set; }

    public string? Toolsets { get; set; }

    public bool ReadOnly { get; set; }

    public string Bridge { get; set; } = DefaultBridgeMode;

    public bool AllowCodeExecution { get; set; }

    public void LoadFromEnvironment()
    {
        ProjectPath = Environment.GetEnvironmentVariable(ProjectPathVariable) ?? string.Empty;
        EnginePath = Environment.GetEnvironmentVariable(EnginePathVariable);
        EditorConfigSet = Environment.GetEnvironmentVariable(EditorConfigVariable) is { Length: > 0 };
        EditorConfig = Environment.GetEnvironmentVariable(EditorConfigVariable) is { Length: > 0 } config ?
            config :
            DefaultEditorConfig;
        Toolsets = Environment.GetEnvironmentVariable(ToolsetsVariable);
        ReadOnly = ParseBoolean(Environment.GetEnvironmentVariable(ReadOnlyVariable));
        Bridge = Environment.GetEnvironmentVariable(BridgeVariable) is { Length: > 0 } bridge ?
            bridge :
            DefaultBridgeMode;
        AllowCodeExecution = ParseBoolean(Environment.GetEnvironmentVariable(AllowCodeExecutionVariable));
    }

    /// <summary>
    /// Resolves <see cref="ProjectPath"/> to an actual <c>.flaxproj</c> file: the path itself if it
    /// already names one, or the single <c>.flaxproj</c> found directly inside it if it's a directory.
    /// </summary>
    public string ResolveProjectFile()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            throw new InvalidOperationException($"{ProjectPathVariable} is not set.");
        }

        if (ProjectPath.EndsWith(".flaxproj", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(ProjectPath))
            {
                throw new InvalidOperationException($"{ProjectPathVariable} points to '{ProjectPath}', which does not exist.");
            }
            return Path.GetFullPath(ProjectPath);
        }

        if (!Directory.Exists(ProjectPath))
        {
            throw new InvalidOperationException($"{ProjectPathVariable} points to '{ProjectPath}', which does not exist.");
        }

        var candidates = Directory.GetFiles(ProjectPath, "*.flaxproj", SearchOption.TopDirectoryOnly);
        return candidates.Length switch
        {
            0 => throw new InvalidOperationException($"No .flaxproj file found in '{ProjectPath}' ({ProjectPathVariable})."),
            1 => Path.GetFullPath(candidates[0]),
            _ => throw new InvalidOperationException(
                $"Multiple .flaxproj files found in '{ProjectPath}': {string.Join(", ", candidates.Select(Path.GetFileName))}. " +
                $"Set {ProjectPathVariable} to the exact file."
            ),
        };
    }

    /// <summary>
    /// Resolves the project's <c>Content/</c> directory from <see cref="ResolveProjectFile"/>.
    /// </summary>
    public string ResolveContentDirectory()
    {
        var projectFile = ResolveProjectFile();
        return Path.Combine(Path.GetDirectoryName(projectFile)!, "Content");
    }

    private static bool ParseBoolean(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.Ordinal) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
