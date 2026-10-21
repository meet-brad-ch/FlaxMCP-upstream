using System.ComponentModel;
using FlaxMcp.Configuration;
using FlaxMcp.Flax;
using FlaxMcp.Flax.Models;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

[McpServerToolType]
public sealed class BuildTools
{
    private static readonly TimeSpan ShortOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    private readonly FlaxHeadlessEditorRunner _runner;
    private readonly FlaxJobManager _jobs;
    private readonly IFlaxScriptChecker _checker;

    public BuildTools(FlaxHeadlessEditorRunner runner, FlaxJobManager jobs, IFlaxScriptChecker checker)
    {
        _runner = runner;
        _jobs = jobs;
        _checker = checker;
    }

    [McpServerTool(Name = "build_generate_projects", ReadOnly = false, UseStructuredContent = true)]
    [Description("Runs FlaxEditor.exe -genprojectfiles to regenerate IDE project/solution files.")]
    public async Task<FlaxBuildOperationResult> GenerateProjects(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(["-genprojectfiles"], ShortOperationTimeout, cancellationToken);
        return ToOperationResult(result);
    }

    [McpServerTool(Name = "build_compile_scripts", ReadOnly = false, UseStructuredContent = true)]
    [Description("Compiles game scripts headlessly. Returns structured compiler errors (file/line/code/message) parsed from the engine log, not a raw log dump.")]
    public async Task<FlaxBuildOperationResult> CompileScripts(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync([], ShortOperationTimeout, cancellationToken);
        return ToOperationResult(result);
    }

    [McpServerTool(Name = "build_clear_cache", ReadOnly = false, UseStructuredContent = true)]
    [Description("Runs FlaxEditor.exe -clearcache, and optionally -clearcooker too, to clear build caches.")]
    public async Task<FlaxBuildOperationResult> ClearCache(bool alsoClearCooker = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = alsoClearCooker ? ["-clearcache", "-clearcooker"] : ["-clearcache"];
        var result = await _runner.RunAsync(arguments, ShortOperationTimeout, cancellationToken);
        return ToOperationResult(result);
    }

    [McpServerTool(Name = "build_check_scripts", ReadOnly = true, UseStructuredContent = true)]
    [Description("Compiles the script modules of the project, and of the projects that it references, outside the editor. It uses the compiler arguments that Flax.Build used last and the current files of the modules. It writes the output to a scratch folder. It returns passed, failed or unavailable. Unavailable means that the project has no build yet, and Reason gives the cause. The result contains structured errors and warnings (file, line, column, code) and the outcome of each module. The tool works while the editor is open, also during play mode. It does not change the project.")]
    public Task<FlaxScriptCheckResult> CheckScriptsAsync(CancellationToken cancellationToken)
    {
        return _checker.CheckAsync(cancellationToken);
    }

    [McpServerTool(Name = "build_game", ReadOnly = false, UseStructuredContent = true)]
    [Description("Starts a game build (-build <preset>.<target>, for example preset 'Development', target 'Windows') in the background and returns its job immediately. A real build can take minutes. Use job_status or job_wait to follow the build. The result of the job is the build outcome.")]
    public FlaxJobHandle StartBuild(string preset, string target)
    {
        return _jobs.Start("build_game", exclusive: false, async (progress, cancellationToken) =>
        {
            progress.Report($"building {preset}.{target}");
            return ToOperationResult(await _runner.RunAsync(["-build", $"{preset}.{target}"], BuildTimeout, cancellationToken));
        });
    }

    internal static FlaxBuildOperationResult ToOperationResult(FlaxHeadlessRunResult result)
    {
        var errors = result.Log is not null ?
            FlaxCompilerDiagnosticParser.Parse(result.Log).Where(diagnostic => diagnostic.Severity == "error").Take(ResponseLimits.DefaultListTop).ToArray() :
            [];

        return new FlaxBuildOperationResult(
            Succeeded: result.ExitCode == 0 && !result.TimedOut && result.ErrorCount is null or 0,
            result.ExitCode,
            result.TimedOut,
            result.ErrorCount,
            errors
        );
    }
}
