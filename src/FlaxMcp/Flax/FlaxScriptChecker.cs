using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaxMcp.Configuration;
using FlaxMcp.Flax.Models;
using Microsoft.Extensions.Options;

namespace FlaxMcp.Flax;

/// <summary>Checks a project's scripts without the editor.</summary>
public interface IFlaxScriptChecker
{
    /// <summary>Compiles every script module of the project (and its referenced projects) to a scratch folder.</summary>
    Task<FlaxScriptCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Compiles the project's C# script modules the way Flax.Build last did, outside the editor: Flax.Build leaves the exact
/// compiler arguments in <c>Cache/Intermediate/&lt;EditorTarget&gt;/&lt;Platform&gt;/&lt;Arch&gt;/&lt;Config&gt;/&lt;Module&gt;.CSharp.response</c>.
/// The checker re-reads each module's source folder (files added or removed since), writes the output to a scratch
/// folder, and has the .NET SDK's compiler write its diagnostics as SARIF, so errors come back structured (file, line,
/// column, code) without the editor or its log. A module that references another module is compiled against that
/// module's fresh output; a module whose dependency failed is skipped.
/// </summary>
public sealed partial class FlaxScriptChecker : IFlaxScriptChecker
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(2);
    private readonly string _projectFile;
    private readonly string? _configuration;
    private readonly string _scratchRoot;
    private readonly string? _dotnetRoot;

    public FlaxScriptChecker(IOptions<FlaxMcpOptions> options)
        : this(options.Value.ResolveProjectFile(), options.Value.EditorConfigSet ? options.Value.EditorConfig : null,
            Path.Combine(Path.GetTempPath(), "FlaxMcp", "check"), null)
    {
    }

    /// <summary>Creates a checker.</summary>
    /// <param name="projectFile">The project's .flaxproj.</param>
    /// <param name="configuration">
    /// The editor build configuration (Development, Debug, Release); null for the one the editor built last (the newest
    /// response files): an editor run in another configuration than the default left the default's arguments stale, and
    /// the check failed on references they lacked.
    /// </param>
    /// <param name="scratchRoot">Where the check's outputs go.</param>
    /// <param name="dotnetRoot">The .NET installation; null to find it on the PATH.</param>
    internal FlaxScriptChecker(string projectFile, string? configuration, string scratchRoot, string? dotnetRoot)
    {
        _projectFile = projectFile;
        _configuration = configuration;
        _scratchRoot = scratchRoot;
        _dotnetRoot = dotnetRoot;
    }

    public async Task<FlaxScriptCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var modules = FindModules();
        if (modules.Count == 0)
        {
            return FlaxScriptCheckResult.Unavailable(
                watch.Elapsed.TotalSeconds,
                "No compiler response files under Cache/Intermediate: the scripts have not been built in the editor yet.");
        }

        var compiler = FindCompiler(modules[0]);
        if (compiler is null)
        {
            return FlaxScriptCheckResult.Unavailable(
                watch.Elapsed.TotalSeconds,
                $"No .NET SDK {modules[0].RuntimeMajor}.x compiler (sdk/<version>/Roslyn/bincore/csc.dll) was found.");
        }

        var scratch = Path.Combine(_scratchRoot, ProjectKey());
        Directory.CreateDirectory(scratch);
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reports = new List<FlaxScriptCheckModule>();
        var errors = new List<FlaxCompilerDiagnostic>();
        var warnings = new List<FlaxCompilerDiagnostic>();
        // Modules that do not reference each other compile at the same time; a layer waits for the one before.
        foreach (var layer in Layers(modules))
        {
            var ready = new List<FlaxCscModule>();
            foreach (var module in layer)
            {
                var blocker = module.References.FirstOrDefault(failed.Contains);
                if (blocker is null)
                {
                    ready.Add(module);
                    continue;
                }

                failed.Add(module.OutPath);
                reports.Add(new FlaxScriptCheckModule(module.Name, module.ProjectName, "skipped", 0, 0,
                    $"It references {Path.GetFileName(blocker)}, which did not compile."));
            }

            var runs = await Task.WhenAll(ready.Select(async module =>
            {
                var moduleWatch = Stopwatch.StartNew();
                var run = await CompileAsync(compiler, module, scratch, outputs, cancellationToken);
                return new FlaxTimedRun(module, run, moduleWatch.Elapsed.TotalMilliseconds);
            }));
            foreach (var timed in runs)
            {
                errors.AddRange(timed.Run.Errors);
                warnings.AddRange(timed.Run.Warnings);
                var passed = timed.Run.Errors.Count == 0 && timed.Run.ExitCode == 0;
                if (passed)
                {
                    outputs[timed.Module.OutPath] = timed.Run.OutPath;
                }
                else
                {
                    failed.Add(timed.Module.OutPath);
                }

                reports.Add(new FlaxScriptCheckModule(timed.Module.Name, timed.Module.ProjectName, passed ? "passed" : "failed",
                    timed.Run.SourceCount, timed.Milliseconds, timed.Run.Note));
            }
        }

        return new FlaxScriptCheckResult(
            failed.Count == 0 ? "passed" : "failed",
            watch.Elapsed.TotalSeconds,
            null,
            reports,
            errors,
            warnings);
    }

    private async Task<FlaxModuleRun> CompileAsync(
        FlaxCompilerTool compiler,
        FlaxCscModule module,
        string scratch,
        IReadOnlyDictionary<string, string> outputs,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(scratch, module.ProjectName, module.Name);
        Directory.CreateDirectory(folder);
        var outPath = Path.Combine(folder, Path.GetFileName(module.OutPath));
        var sarif = Path.Combine(folder, module.Name + ".sarif");
        File.Delete(sarif);
        var sources = Sources(module);
        var response = new StringBuilder();
        foreach (var option in module.Options)
        {
            if (option.StartsWith("/out:", StringComparison.Ordinal) || option.StartsWith("/doc:", StringComparison.Ordinal) ||
                option.StartsWith("/generatedfilesout:", StringComparison.Ordinal) || option.StartsWith("/errorlog:", StringComparison.Ordinal))
            {
                continue;
            }

            var reference = ReferencePath(option);
            response.AppendLine(reference is not null && outputs.TryGetValue(reference, out var fresh) ? $"/reference:\"{fresh}\"" : option);
        }

        response.AppendLine($"/out:\"{outPath}\"");
        response.AppendLine($"/errorlog:\"{sarif}\",version=2.1");
        foreach (var source in sources)
        {
            response.AppendLine($"\"{source}\"");
        }

        var responsePath = Path.Combine(folder, module.Name + ".check.response");
        await File.WriteAllTextAsync(responsePath, response.ToString(), cancellationToken);
        var process = await FlaxProcessRunner.RunAsync(
            // -shared: the compiler server (VBCSCompiler, as dotnet build uses) keeps the compiler warm between checks.
            compiler.DotnetHost, [compiler.CscPath, "-shared", "@" + responsePath], folder, CompileTimeout, cancellationToken);
        if (process.TimedOut)
        {
            return new FlaxModuleRun(outPath, null, sources.Count, [new FlaxCompilerDiagnostic("", 0, 0, "error", "TIMEOUT",
                $"The compiler did not finish within {CompileTimeout.TotalSeconds:0} seconds.")], [], null);
        }

        if (!File.Exists(sarif))
        {
            // No diagnostics file: the compiler did not start compiling (a bad option, a missing compiler). Its
            // output says why.
            var text = (process.StandardOutput + process.StandardError).Trim();
            return new FlaxModuleRun(outPath, process.ExitCode, sources.Count,
                [new FlaxCompilerDiagnostic("", 0, 0, "error", "CHECK", text.Length > 0 ? text : "The compiler wrote no diagnostics.")], [], null);
        }

        var diagnostics = FlaxSarifReader.Read(await File.ReadAllTextAsync(sarif, cancellationToken));
        return new FlaxModuleRun(
            outPath,
            process.ExitCode,
            sources.Count,
            diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToArray(),
            diagnostics.Where(diagnostic => diagnostic.Severity == "warning").ToArray(),
            null);
    }

    /// <summary>
    /// The module's sources now: the listed files outside its folder (generated ones) that still exist, and every .cs
    /// file in its folder except Flax.Build scripts and the folders of other modules.
    /// </summary>
    private static IReadOnlyList<string> Sources(FlaxCscModule module)
    {
        if (module.Folder is null)
        {
            return module.Sources.Where(File.Exists).ToArray();
        }

        var folder = Path.GetFullPath(module.Folder) + Path.DirectorySeparatorChar;
        var outside = module.Sources.Where(source => !Path.GetFullPath(source).StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(source));
        return outside.Concat(ModuleFiles(module.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ModuleFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            if (!file.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            if (Directory.EnumerateFiles(child, "*.Build.cs").Any())
            {
                continue;
            }

            foreach (var file in ModuleFiles(child))
            {
                yield return file;
            }
        }
    }

    private List<FlaxCscModule> FindModules()
    {
        var modules = new List<FlaxCscModule>();
        string? configuration = _configuration;
        foreach (var project in Projects())
        {
            var info = FlaxProjectReader.Read(project);
            if (string.IsNullOrEmpty(info.EditorTarget))
            {
                continue;
            }

            var projectFolder = Path.GetDirectoryName(project)!;
            var target = Path.Combine(projectFolder, "Cache", "Intermediate", info.EditorTarget, PlatformName(), ArchitectureName());
            // the main project decides the configuration; the editor builds its referenced projects in the same one
            configuration ??= NewestConfiguration(target);
            if (configuration is null)
            {
                continue;
            }

            var intermediate = Path.Combine(target, configuration);
            if (!Directory.Exists(intermediate))
            {
                continue;
            }

            foreach (var response in Directory.EnumerateFiles(intermediate, "*.CSharp.response"))
            {
                modules.Add(FlaxCscModule.Read(response, info.Name, Path.Combine(projectFolder, "Source")));
            }
        }

        return modules;
    }

    /// <summary>The configuration folder under <paramref name="target"/> with the newest compiler response file (the
    /// editor's last build); null when none has one.</summary>
    private static string? NewestConfiguration(string target)
    {
        if (!Directory.Exists(target))
        {
            return null;
        }

        return Directory.EnumerateDirectories(target)
            .Select(folder => new { Folder = folder, Newest = Directory.EnumerateFiles(folder, "*.CSharp.response").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() })
            .Where(candidate => candidate.Newest != default)
            .OrderByDescending(candidate => candidate.Newest)
            .Select(candidate => Path.GetFileName(candidate.Folder))
            .FirstOrDefault();
    }

    /// <summary>The project and the projects it references, except the engine.</summary>
    private IEnumerable<string> Projects()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>([Path.GetFullPath(_projectFile)]);
        while (pending.Count > 0)
        {
            var project = pending.Dequeue();
            if (!seen.Add(project) || !File.Exists(project))
            {
                continue;
            }

            yield return project;
            foreach (var reference in FlaxProjectReader.Read(project).References)
            {
                if (reference.Contains("$(EnginePath)", StringComparison.Ordinal))
                {
                    continue;
                }

                pending.Enqueue(Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(Path.GetDirectoryName(project)!, reference)));
            }
        }
    }

    /// <summary>The modules in layers: a module comes after every module whose output it references.</summary>
    private static IReadOnlyList<IReadOnlyList<FlaxCscModule>> Layers(IReadOnlyList<FlaxCscModule> modules)
    {
        var byOutput = modules.ToDictionary(module => module.OutPath, StringComparer.OrdinalIgnoreCase);
        var depths = new Dictionary<FlaxCscModule, int>();
        foreach (var module in modules)
        {
            Depth(module, []);
        }

        return modules.GroupBy(module => depths[module]).OrderBy(group => group.Key)
            .Select(group => (IReadOnlyList<FlaxCscModule>)group.ToArray()).ToArray();

        int Depth(FlaxCscModule module, HashSet<FlaxCscModule> path)
        {
            if (depths.TryGetValue(module, out var known))
            {
                return known;
            }

            if (!path.Add(module))
            {
                throw new InvalidOperationException($"The script modules reference each other in a cycle through {module.Name}.");
            }

            var depth = 0;
            foreach (var reference in module.References)
            {
                if (byOutput.TryGetValue(reference, out var dependency))
                {
                    depth = Math.Max(depth, Depth(dependency, path) + 1);
                }
            }

            path.Remove(module);
            depths[module] = depth;
            return depth;
        }
    }

    private FlaxCompilerTool? FindCompiler(FlaxCscModule module)
    {
        var roots = _dotnetRoot is not null ? [_dotnetRoot] : DotnetRoots();
        foreach (var root in roots)
        {
            var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var sdks = Path.Combine(root, "sdk");
            if (!File.Exists(host) || !Directory.Exists(sdks))
            {
                continue;
            }

            var csc = Directory.EnumerateDirectories(sdks)
                .Select(folder => new FlaxSdkFolder(folder, Version.TryParse(Path.GetFileName(folder).Split('-')[0], out var parsed) ? parsed : null))
                .Where(sdk => sdk.Version is not null && sdk.Version.Major == module.RuntimeMajor)
                .OrderByDescending(sdk => sdk.Version)
                .Select(sdk => Path.Combine(sdk.Folder, "Roslyn", "bincore", "csc.dll"))
                .FirstOrDefault(File.Exists);
            if (csc is not null)
            {
                return new FlaxCompilerTool(host, csc);
            }
        }

        return null;
    }

    /// <summary>
    /// Where .NET may be installed: the installation of the runtime running the server, DOTNET_ROOT, then the folders
    /// on the PATH (a link such as /usr/bin/dotnet is followed to its installation).
    /// </summary>
    private static IEnumerable<string> DotnetRoots()
    {
        // .../shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtime is not null)
        {
            yield return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        }

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        {
            yield return dotnetRoot;
        }

        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim('"'), name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var target = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;
            yield return Path.GetDirectoryName(target)!;
        }
    }

    private static string? ReferencePath(string option)
    {
        return option.StartsWith("/reference:", StringComparison.Ordinal) ? Path.GetFullPath(option["/reference:".Length..].Trim('"')) : null;
    }

    internal static string PlatformName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Mac" : "Linux";

    internal static string ArchitectureName() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x64";

    private string ProjectKey() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_projectFile).ToLowerInvariant())))[..12];

    /// <summary>The compiler: the dotnet host and the SDK's csc.dll.</summary>
    private sealed record FlaxCompilerTool(string DotnetHost, string CscPath);

    /// <summary>An installed SDK's folder and version.</summary>
    private sealed record FlaxSdkFolder(string Folder, Version? Version);

    /// <summary>A module's compile and how long it took.</summary>
    private sealed record FlaxTimedRun(FlaxCscModule Module, FlaxModuleRun Run, double Milliseconds);

    /// <summary>One module's compile in the check.</summary>
    private sealed record FlaxModuleRun(
        string OutPath,
        int? ExitCode,
        int SourceCount,
        IReadOnlyList<FlaxCompilerDiagnostic> Errors,
        IReadOnlyList<FlaxCompilerDiagnostic> Warnings,
        string? Note);
}

/// <summary>One script module as Flax.Build last compiled it.</summary>
/// <param name="Name">The module's name.</param>
/// <param name="ProjectName">The project it belongs to.</param>
/// <param name="Options">The compiler options, one per line as Flax.Build wrote them.</param>
/// <param name="Sources">The source files Flax.Build listed.</param>
/// <param name="OutPath">The assembly Flax.Build writes.</param>
/// <param name="References">The referenced assemblies' paths.</param>
/// <param name="Folder">The module's source folder (where its .Build.cs is), or null when not found.</param>
/// <param name="RuntimeMajor">The .NET major version of the reference pack it compiles against.</param>
public sealed partial record FlaxCscModule(
    string Name,
    string ProjectName,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Sources,
    string OutPath,
    IReadOnlyList<string> References,
    string? Folder,
    int RuntimeMajor)
{
    /// <summary>Reads a response file Flax.Build wrote.</summary>
    /// <param name="responsePath">The <c>&lt;Module&gt;.CSharp.response</c> file.</param>
    /// <param name="projectName">The project's name.</param>
    /// <param name="sourceRoot">The project's Source folder, searched for the module's .Build.cs.</param>
    /// <returns>The module.</returns>
    /// <exception cref="InvalidOperationException">The file has no /out option.</exception>
    public static FlaxCscModule Read(string responsePath, string projectName, string sourceRoot)
    {
        var name = Path.GetFileName(responsePath)[..^".CSharp.response".Length];
        var options = new List<string>();
        var sources = new List<string>();
        foreach (var raw in File.ReadAllLines(responsePath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] is '/' or '-')
            {
                options.Add(line);
            }
            else
            {
                // A line may hold several quoted paths with nothing between them.
                var quoted = QuotedPattern().Matches(line).Select(match => match.Groups[1].Value).ToArray();
                sources.AddRange(quoted.Length > 0 ? quoted : [line]);
            }
        }

        var outOption = options.FirstOrDefault(option => option.StartsWith("/out:", StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"{responsePath} has no /out option.");
        var references = options
            .Where(option => option.StartsWith("/reference:", StringComparison.Ordinal))
            .Select(option => Path.GetFullPath(option["/reference:".Length..].Trim('"')))
            .ToArray();
        var pack = references.Select(reference => RefPackPattern().Match(reference)).FirstOrDefault(match => match.Success);
        var buildScript = Directory.Exists(sourceRoot) ?
            Directory.EnumerateFiles(sourceRoot, name + ".Build.cs", SearchOption.AllDirectories).FirstOrDefault() :
            null;
        return new FlaxCscModule(
            name,
            projectName,
            options,
            sources,
            Path.GetFullPath(outOption["/out:".Length..].Trim('"')),
            references,
            buildScript is null ? null : Path.GetDirectoryName(buildScript),
            pack is null ? Environment.Version.Major : int.Parse(pack.Groups[1].Value));
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"Microsoft\.NETCore\.App\.Ref[\\/](\d+)\.")]
    private static partial Regex RefPackPattern();
}

/// <summary>Reads the diagnostics of a SARIF 2.1 log the C# compiler wrote (<c>/errorlog</c>).</summary>
public static class FlaxSarifReader
{
    /// <summary>The diagnostics: errors, warnings, and notes as "info".</summary>
    /// <param name="sarif">The SARIF JSON.</param>
    /// <returns>Every result of every run.</returns>
    public static IReadOnlyList<FlaxCompilerDiagnostic> Read(string sarif)
    {
        using var document = JsonDocument.Parse(sarif);
        var diagnostics = new List<FlaxCompilerDiagnostic>();
        foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                var level = result.TryGetProperty("level", out var levelElement) ? levelElement.GetString() : "warning";
                var file = "";
                var line = 0;
                var column = 0;
                if (result.TryGetProperty("locations", out var locations) && locations.GetArrayLength() > 0 &&
                    locations[0].TryGetProperty("physicalLocation", out var physical))
                {
                    if (physical.TryGetProperty("artifactLocation", out var artifact) && artifact.TryGetProperty("uri", out var uri))
                    {
                        var text = uri.GetString() ?? "";
                        file = Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : text;
                    }

                    if (physical.TryGetProperty("region", out var region))
                    {
                        line = region.TryGetProperty("startLine", out var startLine) ? startLine.GetInt32() : 0;
                        column = region.TryGetProperty("startColumn", out var startColumn) ? startColumn.GetInt32() : 0;
                    }
                }

                diagnostics.Add(new FlaxCompilerDiagnostic(
                    file,
                    line,
                    column,
                    level == "note" ? "info" : level ?? "warning",
                    result.TryGetProperty("ruleId", out var rule) ? rule.GetString() ?? "" : "",
                    result.GetProperty("message").GetProperty("text").GetString() ?? ""));
            }
        }

        return diagnostics;
    }
}

/// <summary>The outcome of a script check.</summary>
/// <param name="Status">passed, failed, or unavailable (the check could not run; Reason says why).</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="Reason">Why it was unavailable.</param>
/// <param name="Modules">Each module's outcome.</param>
/// <param name="Errors">The compiler errors, structured.</param>
/// <param name="Warnings">The compiler warnings.</param>
public sealed record FlaxScriptCheckResult(
    string Status,
    double Seconds,
    string? Reason,
    IReadOnlyList<FlaxScriptCheckModule> Modules,
    IReadOnlyList<FlaxCompilerDiagnostic> Errors,
    IReadOnlyList<FlaxCompilerDiagnostic> Warnings)
{
    /// <summary>A check that could not run.</summary>
    public static FlaxScriptCheckResult Unavailable(double seconds, string reason) => new("unavailable", seconds, reason, [], [], []);
}

/// <summary>One module's outcome in a script check.</summary>
/// <param name="Module">The module's name.</param>
/// <param name="Project">Its project.</param>
/// <param name="Status">passed, failed, or skipped (a module it references did not compile).</param>
/// <param name="Sources">How many source files were compiled.</param>
/// <param name="Milliseconds">How long its compile took.</param>
/// <param name="Note">Why it was skipped.</param>
public sealed record FlaxScriptCheckModule(string Module, string Project, string Status, int Sources, double Milliseconds, string? Note);
