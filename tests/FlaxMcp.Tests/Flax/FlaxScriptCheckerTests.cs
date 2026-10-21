using FlaxMcp.Flax;
using Xunit;

namespace FlaxMcp.Tests.Flax;

public sealed class FlaxScriptCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FlaxMcpCheck_" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly string _intermediate;

    public FlaxScriptCheckerTests()
    {
        _project = Path.Combine(_root, "Game");
        _intermediate = Path.Combine(_project, "Cache", "Intermediate", "GameEditorTarget",
            FlaxScriptChecker.PlatformName(), FlaxScriptChecker.ArchitectureName(), "Development");
        Directory.CreateDirectory(_intermediate);
        File.WriteAllText(Path.Combine(_project, "Game.flaxproj"),
            "{\"Name\":\"Game\",\"EditorTarget\":\"GameEditorTarget\",\"References\":[{\"Name\":\"$(EnginePath)/Flax.flaxproj\"}]}");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Read_SplitsOptionsSourcesAndPathsSharingALine()
    {
        var response = Path.Combine(_intermediate, "Game.CSharp.response");
        File.WriteAllText(response, string.Join('\n',
            "/nologo",
            "/reference:\"C:\\Program Files\\dotnet\\packs\\Microsoft.NETCore.App.Ref\\10.0.11\\ref\\net10.0\\System.Runtime.dll\"",
            "/out:\"C:\\P\\Binaries\\Game.CSharp.dll\"",
            "\"C:\\P\\Source\\Game\\A.cs\"",
            "\"C:\\P\\Cache\\Attributes.cs\"\"C:\\P\\Source\\Game.Gen.cs\""));

        var module = FlaxCscModule.Read(response, "Game", Path.Combine(_project, "Source"));

        Assert.Equal("Game", module.Name);
        Assert.Equal(["/nologo", module.Options[1], module.Options[2]], module.Options);
        Assert.Equal(["C:\\P\\Source\\Game\\A.cs", "C:\\P\\Cache\\Attributes.cs", "C:\\P\\Source\\Game.Gen.cs"], module.Sources);
        Assert.Equal(10, module.RuntimeMajor);
        Assert.Null(module.Folder);
    }

    [Fact]
    public void SarifReader_ReadsErrorsWarningsAndNotes()
    {
        const string sarif = """
            {"runs":[{"results":[
              {"ruleId":"CS1525","level":"error","message":{"text":"Invalid expression term ';'"},
               "locations":[{"physicalLocation":{"artifactLocation":{"uri":"file:///C:/P/Source/Game/A.cs"},
                 "region":{"startLine":3,"startColumn":17,"endLine":3,"endColumn":18}}}]},
              {"ruleId":"CS0168","level":"warning","message":{"text":"unused"}},
              {"ruleId":"SYSLIB1054","level":"note","message":{"text":"info"}}]}]}
            """;

        var diagnostics = FlaxSarifReader.Read(sarif);

        Assert.Equal(3, diagnostics.Count);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("CS1525", diagnostics[0].Code);
        Assert.Equal(3, diagnostics[0].Line);
        Assert.Equal(17, diagnostics[0].Column);
        Assert.EndsWith("A.cs", diagnostics[0].File);
        Assert.Equal("warning", diagnostics[1].Severity);
        Assert.Equal("", diagnostics[1].File);
        Assert.Equal("info", diagnostics[2].Severity);
    }

    [Fact]
    public async Task Check_WithoutResponseFiles_IsUnavailable()
    {
        var result = await Checker().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", result.Status);
        Assert.Contains("not been built", result.Reason);
    }

    [Fact]
    public async Task Check_OfCleanModules_PassesInDependencyOrder()
    {
        WriteModule("Core", "public static class Answer { public static int Value => 42; }");
        WriteModule("Tools", "public static class Use { public static int Twice => Answer.Value * 2; }", dependsOn: "Core");

        var result = await Checker().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.Status);
        Assert.Equal(["Core", "Tools"], result.Modules.Select(module => module.Module));
        Assert.All(result.Modules, module => Assert.Equal("passed", module.Status));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Check_WithAnError_ReportsItStructuredAndSkipsTheDependents()
    {
        WriteModule("Core", "public static class Answer { public static int Value => ; }");
        WriteModule("Tools", "public static class Use { public static int Twice => Answer.Value * 2; }", dependsOn: "Core");

        var result = await Checker().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal("CS1525", error.Code);
        Assert.EndsWith("Core.cs", error.File);
        Assert.Equal(1, error.Line);
        Assert.Equal("skipped", result.Modules.Single(module => module.Module == "Tools").Status);
    }

    [Fact]
    public async Task Check_FindsAFileAddedSinceTheLastBuild()
    {
        WriteModule("Core", "public static class Answer { public static int Value => 42; }");
        File.WriteAllText(Path.Combine(_project, "Source", "Core", "Added.cs"), "public static class Added { public static int X => Missing.Y; }");

        var result = await Checker().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Contains(result.Errors, error => error.Code == "CS0103" && error.File.EndsWith("Added.cs", StringComparison.Ordinal));
        Assert.Equal(2, result.Modules.Single().Sources);
    }

    private FlaxScriptChecker Checker() =>
        new(Path.Combine(_project, "Game.flaxproj"), "Development", Path.Combine(_root, "scratch"), null);

    /// <summary>A module the way Flax.Build leaves it: its folder with a .Build.cs and one source, and its response file.</summary>
    private void WriteModule(string name, string code, string? dependsOn = null)
    {
        var folder = Path.Combine(_project, "Source", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".Build.cs"), "// the module's build script");
        var source = Path.Combine(folder, name + ".cs");
        File.WriteAllText(source, code);
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var lines = new List<string>
        {
            "/nologo",
            "/target:library",
            "/nostdlib+",
            $"/reference:\"{Path.Combine(runtime, "System.Private.CoreLib.dll")}\"",
            $"/reference:\"{Path.Combine(runtime, "System.Runtime.dll")}\"",
            $"/out:\"{Output(name)}\"",
        };
        if (dependsOn is not null)
        {
            lines.Add($"/reference:\"{Output(dependsOn)}\"");
        }

        lines.Add($"\"{source}\"");
        File.WriteAllLines(Path.Combine(_intermediate, name + ".CSharp.response"), lines);
    }

    private string Output(string module) => Path.Combine(_project, "Binaries", module + ".CSharp.dll");
}
