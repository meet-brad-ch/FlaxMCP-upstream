using FlaxMcp.Flax;

namespace FlaxMcp.Tests.Tools;

/// <summary>A script checker that returns a set result and counts its calls.</summary>
internal sealed class StubScriptChecker : IFlaxScriptChecker
{
    public FlaxScriptCheckResult Result { get; set; } = new("passed", 0.5, null, [], [], []);

    public int Calls { get; private set; }

    public Task<FlaxScriptCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
