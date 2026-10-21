using System.ComponentModel;
using FlaxMcp.Flax;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

/// <summary>The editor's recorded events: what happened, in order, instead of reading the log file.</summary>
[McpServerToolType]
public sealed class EditorEventTools
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);
    private readonly IFlaxEditorEventClient _client;

    public EditorEventTools(IFlaxEditorEventClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "editor_events", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns the recorded editor events after a sequence number, oldest first. " +
        "The kinds are log, exception, compile_begin, compile_succeeded, compile_failed, reload_begin, reload_end, play_begin, play_end, scene_loaded, scene_load_error, scene_saved, scene_save_error and scene_unloaded. " +
        "Log events contain only managed log lines. " +
        "Native engine lines, for example compiler output, stay in the log file. " +
        "The bridge host keeps the last 4096 events through script reloads. " +
        "Truncated is true when the host overwrote some events after sinceSeq. " +
        "A new HostStartedUtc means that the editor started again and the sequence started again."
    )]
    public Task<FlaxEditorEventPage> GetEventsAsync(
        [Description("Only the events after this sequence number (LastSeq of an earlier call). Use 0 for all the events that the host keeps.")] long sinceSeq = 0,
        [Description("Only these kinds. Omit this value for all kinds.")] string[]? kinds = null,
        [Description("The maximum number of events, from 1 to 500.")] int limit = 100,
        [Description("If no event matches yet, wait up to this time for one, from 0 to 2 seconds.")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        ArgumentOutOfRangeException.ThrowIfLessThan(waitSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitSeconds, 2);
        return _client.GetEventsAsync(sinceSeq, kinds, limit, (int)(waitSeconds * 1000), cancellationToken);
    }

    [McpServerTool(Name = "editor_wait_for", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Waits for the first editor event of a kind (see editor_events), optionally with a text, and returns it. " +
        "Without sinceSeq, the tool waits for an event after the call. " +
        "With sinceSeq, an event that the host recorded after that number also counts. " +
        "A timeout is a result (TimedOut true), not an error. " +
        "Example: start play mode with editor_play_mode. " +
        "Then call editor_wait_for kind=log textContains=\"[Test]\" to read an answer from the game."
    )]
    public Task<FlaxEditorEventWait> WaitForAsync(
        [Description("The event kind, for example play_begin, reload_end or log.")] string kind,
        [Description("Only an event whose text contains this string (ordinal comparison).")] string? textContains = null,
        [Description("Count only the events after this sequence number. Omit this value to wait for a new event.")] long? sinceSeq = null,
        [Description("The maximum time to wait, up to 600 seconds.")] double timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxWait);
        return _client.WaitForEventAsync(kind, textContains, sinceSeq, timeout, cancellationToken);
    }
}
