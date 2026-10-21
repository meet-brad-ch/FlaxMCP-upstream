using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlaxMcpBridge.Host;

/// <summary>
/// The bridge's named-pipe server for the whole editor process. It lives in the Default AssemblyLoadContext, so a
/// script reload does not stop it: the reloaded editor plugin attaches again and requests that arrive meanwhile wait
/// for it. Requests in flight when the plugin detaches are answered with <c>interrupted_by_reload</c>.
/// </summary>
/// <remarks>
/// Pure .NET by design: the plugin compiles this file at run time (Roslyn 4.4, C# 11) and the tests compile it too.
/// </remarks>
public sealed class BridgeHostServer : IDisposable
{
    /// <summary>Error code of a request whose plugin generation detached before answering.</summary>
    public const string InterruptedByReload = "interrupted_by_reload";

    /// <summary>Error code of a request that found no attached plugin within the attach wait.</summary>
    public const string PluginNotAttached = "plugin_not_attached";

    private readonly BridgeHostOptions _options;
    private readonly BridgeEventRing _events;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<WeakReference> _pluginContexts = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private TaskCompletionSource<BridgeHostAttachment> _nextAttachment = NewAttachmentSource();
    private BridgeHostAttachment? _attachment;
    private Task? _acceptLoop;
    private int _attachCount;
    private int _detachCount;

    /// <summary>Creates a stopped server.</summary>
    /// <param name="options">The pipe, the handshake file and the waits.</param>
    /// <param name="events">The editor's recorded events, answered by <c>events_since</c> and <c>events_wait</c>.</param>
    public BridgeHostServer(BridgeHostOptions options, BridgeEventRing events)
    {
        _options = options;
        _events = events;
    }

    /// <summary>The host's version: a hash of its sources, chosen by whoever compiled it.</summary>
    public string Version => _options.HostVersion;

    /// <summary>Starts accepting clients on a background task.</summary>
    /// <exception cref="InvalidOperationException">The server was already started.</exception>
    public void Start()
    {
        lock (_gate)
        {
            if (_acceptLoop is not null)
            {
                throw new InvalidOperationException("The bridge host is already started.");
            }

            // The caller's execution context may hold values of a collectible load context (the plugin's); the
            // accept loop must not keep them alive.
            using (ExecutionContext.SuppressFlow())
            {
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
            }
        }
    }

    /// <summary>Attaches a plugin generation: its dispatcher answers requests until it detaches.</summary>
    /// <param name="attachment">The generation, its versions and its dispatcher.</param>
    public void Attach(BridgeHostAttachment attachment)
    {
        TaskCompletionSource<BridgeHostAttachment> waiters;
        lock (_gate)
        {
            _attachment?.Detached.TrySetResult(true);
            _attachment = attachment;
            _attachCount++;
            _pluginContexts.Add(new WeakReference(AssemblyLoadContext.GetLoadContext(attachment.Dispatch.Method.Module.Assembly)));
            waiters = _nextAttachment;
            WriteHandshake(attachment);
        }

        waiters.TrySetResult(attachment);
    }

    /// <summary>Detaches a plugin generation; requests it has not answered fail with <see cref="InterruptedByReload"/>.</summary>
    /// <param name="generation">The generation that is going away. An older generation is ignored.</param>
    public void Detach(long generation)
    {
        BridgeHostAttachment? detached;
        lock (_gate)
        {
            if (_attachment is null || _attachment.Generation != generation)
            {
                return;
            }

            detached = _attachment;
            _attachment = null;
            _detachCount++;
            _nextAttachment = NewAttachmentSource();
        }

        detached.Detached.TrySetResult(true);
    }

    /// <summary>The host's state as JSON (the <c>host_status</c> result).</summary>
    /// <param name="collect">Run a full garbage collection first, so the count of old plugin load contexts still
    /// alive is a measurement (it takes long on a large heap).</param>
    /// <returns>A JSON object.</returns>
    public string StatusJson(bool collect)
    {
        if (collect)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        lock (_gate)
        {
            var alive = 0;
            foreach (var context in _pluginContexts)
            {
                if (context.IsAlive)
                {
                    alive++;
                }
            }

            using var process = Process.GetCurrentProcess();
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["hostVersion"] = _options.HostVersion,
                ["hostStartedUtc"] = _startedUtc,
                ["generation"] = _attachment?.Generation,
                ["attached"] = _attachment is not null,
                ["accepting"] = _acceptLoop is { IsCompleted: false },
                ["acceptLoopFault"] = _acceptLoop?.Exception?.GetBaseException().Message,
                ["attachCount"] = _attachCount,
                ["detachCount"] = _detachCount,
                ["pluginContextsAlive"] = alive,
                ["pluginContextsSeen"] = _pluginContexts.Count,
                ["collected"] = collect,
                ["privateBytes"] = process.PrivateMemorySize64,
                ["managedBytes"] = GC.GetTotalMemory(false),
                ["gen0Collections"] = GC.CollectionCount(0),
                ["gen1Collections"] = GC.CollectionCount(1),
                ["gen2Collections"] = GC.CollectionCount(2),
                ["lastEventSeq"] = _events.LastSeq,
            });
        }
    }

    /// <summary>Stops accepting clients and removes the handshake file. Requests in flight are not answered.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        lock (_gate)
        {
            if (File.Exists(_options.HandshakePath))
            {
                File.Delete(_options.HandshakePath);
            }
        }
    }

    /// <summary>Answers one request line: <c>host_status</c> here, everything else through the attached plugin.</summary>
    /// <param name="line">The request JSON.</param>
    /// <param name="cancellationToken">Stops waiting for a plugin.</param>
    /// <returns>The response JSON.</returns>
    public async Task<string> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        BridgeHostRequest request;
        try
        {
            request = BridgeHostRequest.Parse(line);
        }
        catch (JsonException ex)
        {
            return BridgeHostRequest.Error(0, "invalid_request", "The request is not JSON: " + ex.Message);
        }

        if (request.Method == "host_status")
        {
            return BridgeHostRequest.Result(request.Id, StatusJson(request.Collect));
        }

        if (request.Method is "events_since" or "events_wait")
        {
            return await EventsAsync(request.Id, line, request.Method == "events_wait", cancellationToken);
        }

        var attachment = await WaitForAttachmentAsync(cancellationToken);
        if (attachment is null)
        {
            return BridgeHostRequest.Error(
                request.Id,
                PluginNotAttached,
                $"No editor plugin attached within {_options.AttachWait.TotalSeconds:0} seconds (scripts reloading or failed to load)."
            );
        }

        var answer = attachment.Dispatch(line);
        var finished = await Task.WhenAny(answer, attachment.Detached.Task);
        if (finished != answer)
        {
            ObserveFault(answer);
            return BridgeHostRequest.Error(
                request.Id,
                InterruptedByReload,
                $"Scripts reloaded while the request ran (plugin generation {attachment.Generation}); its effects are unknown."
            );
        }

        try
        {
            return await answer;
        }
        catch (Exception ex)
        {
            // The plugin answers its own failures; one that escapes it still reaches the client as an error.
            return BridgeHostRequest.Error(request.Id, "request_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The pipe name for a project folder (the same one the server derives).</summary>
    /// <param name="projectFolder">The project folder.</param>
    /// <returns>The pipe name.</returns>
    public static string PipeNameFor(string projectFolder) => "FlaxMcp-" + Hash(projectFolder);

    /// <summary>The handshake file name for a project folder (the same one the server derives).</summary>
    /// <param name="projectFolder">The project folder.</param>
    /// <returns>The file name without its folder.</returns>
    public static string HandshakeFileNameFor(string projectFolder) => Hash(projectFolder) + ".json";

    private async Task<string> EventsAsync(int id, string line, bool wait, CancellationToken cancellationToken)
    {
        BridgeEventQuery query;
        try
        {
            query = BridgeEventQuery.Parse(line);
        }
        catch (ArgumentException ex)
        {
            return BridgeHostRequest.Error(id, "invalid_request", ex.Message);
        }

        var page = wait ?
            await _events.WaitAsync(query.AfterSeq, query.Max, query.Kinds, query.Wait, cancellationToken) :
            _events.Read(query.AfterSeq, query.Max, query.Kinds);
        var events = new List<Dictionary<string, object?>>(page.Events.Count);
        foreach (var item in page.Events)
        {
            events.Add(new Dictionary<string, object?>
            {
                ["seq"] = item.Seq,
                ["utc"] = new DateTime(item.UtcTicks, DateTimeKind.Utc),
                ["kind"] = BridgeEventQuery.NameOf(item.Kind),
                ["level"] = item.Level,
                ["text"] = item.Text,
            });
        }

        return BridgeHostRequest.Result(id, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hostStartedUtc"] = _startedUtc,
            ["events"] = events,
            ["lastSeq"] = page.LastSeq,
            ["oldestSeq"] = page.OldestSeq,
            ["truncated"] = page.Truncated,
        }));
    }

    private async Task<BridgeHostAttachment?> WaitForAttachmentAsync(CancellationToken cancellationToken)
    {
        Task<BridgeHostAttachment> next;
        lock (_gate)
        {
            if (_attachment is not null)
            {
                return _attachment;
            }

            next = _nextAttachment.Task;
        }

        var finished = await Task.WhenAny(next, Task.Delay(_options.AttachWait, cancellationToken));
        return finished == next ? await next : null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var pipe = NewPipe();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A client that connected and left at once breaks only its own instance.
                await pipe.DisposeAsync();
                pipe = NewPipe();
                continue;
            }

            var client = pipe;
            // The next instance listens before this client is served: a client connecting meanwhile is not refused.
            pipe = NewPipe();
            _ = ServeClientAsync(client, token);
        }

        await pipe.DisposeAsync();
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" };
                while (pipe.IsConnected && !token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null)
                    {
                        break;
                    }

                    await writer.WriteLineAsync(await HandleLineAsync(line, token));
                    await writer.FlushAsync(token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away or the host is stopping: nothing is left to answer.
            }
        }
    }

    private NamedPipeServerStream NewPipe() =>
        new(
            _options.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

    private void WriteHandshake(BridgeHostAttachment attachment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.HandshakePath)!);
        var handshake = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pipeName"] = _options.PipeName,
            ["protocolVersion"] = attachment.ProtocolVersion,
            ["pid"] = Environment.ProcessId,
            ["projectFolder"] = _options.ProjectFolder,
            ["pluginVersion"] = attachment.PluginVersion,
            ["engineBuild"] = attachment.EngineBuild,
            ["startedUtc"] = _startedUtc,
            ["hostVersion"] = _options.HostVersion,
            ["generation"] = attachment.Generation,
        });
        // Written whole, then moved over the old file, so a reader never sees half of it.
        var temporary = _options.HandshakePath + ".tmp";
        File.WriteAllText(temporary, handshake);
        File.Move(temporary, _options.HandshakePath, overwrite: true);
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private static TaskCompletionSource<BridgeHostAttachment> NewAttachmentSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Hash(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16];
    }
}

/// <summary>An <c>events_since</c> or <c>events_wait</c> request.</summary>
/// <param name="AfterSeq">Only events newer than this.</param>
/// <param name="Max">At most this many (1 to 500).</param>
/// <param name="Kinds">Only these kinds; null for all.</param>
/// <param name="Wait">For <c>events_wait</c>: how long to wait for one (at most 2 seconds, under the client's request timeout).</param>
public sealed record BridgeEventQuery(long AfterSeq, int Max, IReadOnlyCollection<BridgeEventKind>? Kinds, TimeSpan Wait)
{
    private static readonly Dictionary<string, BridgeEventKind> Names = new(StringComparer.Ordinal)
    {
        ["log"] = BridgeEventKind.Log,
        ["exception"] = BridgeEventKind.Exception,
        ["compile_begin"] = BridgeEventKind.CompileBegin,
        ["compile_succeeded"] = BridgeEventKind.CompileSucceeded,
        ["compile_failed"] = BridgeEventKind.CompileFailed,
        ["reload_begin"] = BridgeEventKind.ReloadBegin,
        ["reload_end"] = BridgeEventKind.ReloadEnd,
        ["play_begin"] = BridgeEventKind.PlayBegin,
        ["play_end"] = BridgeEventKind.PlayEnd,
        ["scene_loaded"] = BridgeEventKind.SceneLoaded,
        ["scene_load_error"] = BridgeEventKind.SceneLoadError,
        ["scene_saved"] = BridgeEventKind.SceneSaved,
        ["scene_save_error"] = BridgeEventKind.SceneSaveError,
        ["scene_unloaded"] = BridgeEventKind.SceneUnloaded,
    };

    /// <summary>The wire name of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its name, for example <c>play_begin</c>.</returns>
    public static string NameOf(BridgeEventKind kind)
    {
        foreach (var pair in Names)
        {
            if (pair.Value == kind)
            {
                return pair.Key;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "The event kind has no wire name.");
    }

    /// <summary>Reads the query's parameters: afterSeq, max, kinds, waitMs.</summary>
    /// <param name="line">The request JSON.</param>
    /// <returns>The query.</returns>
    /// <exception cref="ArgumentException">A parameter is out of range or names an unknown kind.</exception>
    public static BridgeEventQuery Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var hasParams = document.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object;
        var afterSeq = hasParams && parameters.TryGetProperty("afterSeq", out var after) ? after.GetInt64() : 0;
        var max = hasParams && parameters.TryGetProperty("max", out var maxElement) ? maxElement.GetInt32() : 100;
        var waitMs = hasParams && parameters.TryGetProperty("waitMs", out var waitElement) ? waitElement.GetInt32() : 0;
        if (max < 1 || max > 500)
        {
            throw new ArgumentException($"max must be 1 to 500, not {max}.");
        }

        if (waitMs < 0 || waitMs > 2000)
        {
            throw new ArgumentException($"waitMs must be 0 to 2000, not {waitMs}.");
        }

        List<BridgeEventKind>? kinds = null;
        if (hasParams && parameters.TryGetProperty("kinds", out var kindsElement) && kindsElement.ValueKind == JsonValueKind.Array)
        {
            kinds = new List<BridgeEventKind>();
            foreach (var name in kindsElement.EnumerateArray())
            {
                var text = name.GetString() ?? "";
                kinds.Add(Names.TryGetValue(text, out var kind) ?
                    kind :
                    throw new ArgumentException($"Unknown event kind '{text}'. Kinds: {string.Join(", ", Names.Keys)}."));
            }
        }

        return new BridgeEventQuery(afterSeq, max, kinds, TimeSpan.FromMilliseconds(waitMs));
    }
}

/// <summary>Where the host listens and how long a request waits for a plugin.</summary>
/// <param name="ProjectFolder">The Flax project the editor has open.</param>
/// <param name="PipeName">The pipe the host listens on.</param>
/// <param name="HandshakePath">The file that tells clients the pipe, the protocol and the process.</param>
/// <param name="HostVersion">A hash of the host's sources.</param>
/// <param name="AttachWait">How long a request waits for a plugin to attach.</param>
public sealed record BridgeHostOptions(
    string ProjectFolder,
    string PipeName,
    string HandshakePath,
    string HostVersion,
    TimeSpan AttachWait);

/// <summary>One plugin generation attached to the host.</summary>
/// <param name="Generation">The plugin's load time in UTC ticks; changes with every script reload.</param>
/// <param name="ProtocolVersion">The bridge protocol the plugin speaks.</param>
/// <param name="PluginVersion">The plugin assembly's version.</param>
/// <param name="EngineBuild">The engine build number.</param>
/// <param name="Dispatch">Answers a request line with a response line.</param>
public sealed record BridgeHostAttachment(
    long Generation,
    int ProtocolVersion,
    string PluginVersion,
    int EngineBuild,
    Func<string, Task<string>> Dispatch)
{
    /// <summary>Completed when this generation detaches.</summary>
    public TaskCompletionSource<bool> Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The parts of a request the host reads itself, and the host's response envelope.</summary>
/// <param name="Id">The request id (0 when absent).</param>
/// <param name="Method">The method name.</param>
/// <param name="Collect">For <c>host_status</c>: collect garbage first.</param>
public sealed record BridgeHostRequest(int Id, string Method, bool Collect)
{
    /// <summary>Reads the id, the method and the <c>collect</c> parameter.</summary>
    /// <param name="line">The request JSON.</param>
    /// <returns>The request.</returns>
    /// <exception cref="JsonException">The line is not a JSON object.</exception>
    public static BridgeHostRequest Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var value) ? value : 0;
        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() ?? "" : "";
        var collect = root.TryGetProperty("params", out var parameters) &&
                      parameters.ValueKind == JsonValueKind.Object &&
                      parameters.TryGetProperty("collect", out var collectElement) &&
                      collectElement.ValueKind == JsonValueKind.True;
        return new BridgeHostRequest(id, method, collect);
    }

    /// <summary>A result response around JSON the host made.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="resultJson">The result object as JSON.</param>
    /// <returns>The response JSON.</returns>
    public static string Result(int id, string resultJson) => "{\"id\":" + id + ",\"result\":" + resultJson + "}";

    /// <summary>An error response.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>The response JSON.</returns>
    public static string Error(int id, string code, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        });
}
