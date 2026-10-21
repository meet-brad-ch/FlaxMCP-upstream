namespace FlaxMcp.Flax;

/// <summary>
/// A compile in the editor, checked first: the scripts are compiled outside the editor (<see cref="IFlaxScriptChecker"/>),
/// and only a clean check goes on to the editor's compile and reload. A failing check returns its structured errors
/// at once and leaves the editor alone.
/// </summary>
public static class FlaxCheckedCompile
{
    /// <summary>Checks the scripts, then compiles them in the editor when the check did not fail.</summary>
    /// <param name="checker">Compiles the scripts outside the editor.</param>
    /// <param name="bridgeClient">The editor's bridge.</param>
    /// <param name="progress">Receives what the compile is doing.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The editor's compile result with the check's status, or the check's errors when it failed.</returns>
    public static async Task<FlaxEditorCompileResult> RunAsync(
        IFlaxScriptChecker checker,
        IFlaxBridgeClient bridgeClient,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("checking the scripts outside the editor");
        var check = await checker.CheckAsync(cancellationToken);
        if (check.Status == "failed")
        {
            return new FlaxEditorCompileResult("failed", check.Seconds, check.Errors, [], 0, "failed");
        }

        var compiled = await bridgeClient.CompileAsync(cancellationToken, progress);
        var checkNote = check.Status == "unavailable" ? "unavailable: " + check.Reason : check.Status;
        return compiled with { Seconds = compiled.Seconds + check.Seconds, Check = checkNote };
    }
}
