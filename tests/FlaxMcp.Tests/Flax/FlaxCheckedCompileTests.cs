using FlaxMcp.Flax;
using FlaxMcp.Flax.Models;
using FlaxMcp.Tests.Tools;
using Xunit;

namespace FlaxMcp.Tests.Flax;

public sealed class FlaxCheckedCompileTests
{
    [Fact]
    public async Task FailedCheck_ReturnsItsErrorsAndLeavesTheEditorAlone()
    {
        var error = new FlaxCompilerDiagnostic("C:/P/A.cs", 3, 17, "error", "CS1525", "Invalid expression term ';'");
        var checker = new StubScriptChecker { Result = new FlaxScriptCheckResult("failed", 0.8, null, [], [error], []) };
        var bridge = new EditorToolsTests.StubBridgeClient();

        var result = await FlaxCheckedCompile.RunAsync(checker, bridge, null, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("failed", result.Check);
        Assert.Equal([error], result.Errors);
        Assert.Equal(0, bridge.CompileCalls);
    }

    [Fact]
    public async Task PassedCheck_GoesOnToTheEditorsCompile()
    {
        var checker = new StubScriptChecker();
        var bridge = new EditorToolsTests.StubBridgeClient { CompileResult = new FlaxEditorCompileResult("reloaded", 1.5, [], [], 1) };

        var result = await FlaxCheckedCompile.RunAsync(checker, bridge, null, TestContext.Current.CancellationToken);

        Assert.Equal("reloaded", result.Outcome);
        Assert.Equal("passed", result.Check);
        Assert.Equal(2.0, result.Seconds, 3);
        Assert.Equal(1, bridge.CompileCalls);
    }

    [Fact]
    public async Task UnavailableCheck_SaysWhyAndCompilesInTheEditor()
    {
        var checker = new StubScriptChecker { Result = FlaxScriptCheckResult.Unavailable(0.01, "never built") };
        var bridge = new EditorToolsTests.StubBridgeClient { CompileResult = new FlaxEditorCompileResult("reloaded", 1.5, [], [], 1) };

        var result = await FlaxCheckedCompile.RunAsync(checker, bridge, null, TestContext.Current.CancellationToken);

        Assert.Equal("reloaded", result.Outcome);
        Assert.Equal("unavailable: never built", result.Check);
    }
}
