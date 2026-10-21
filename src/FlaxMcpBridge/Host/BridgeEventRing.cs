using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlaxMcpBridge.Host;

/// <summary>What happened in the editor.</summary>
public enum BridgeEventKind : byte
{
    /// <summary>A managed log line (Debug.Log and friends; the engine's native lines do not reach managed code).</summary>
    Log,

    /// <summary>An exception logged by managed code.</summary>
    Exception,

    /// <summary>A script compile began.</summary>
    CompileBegin,

    /// <summary>A script compile succeeded.</summary>
    CompileSucceeded,

    /// <summary>A script compile failed (the errors are in the editor log).</summary>
    CompileFailed,

    /// <summary>A script reload began.</summary>
    ReloadBegin,

    /// <summary>A script reload ended.</summary>
    ReloadEnd,

    /// <summary>Play mode began.</summary>
    PlayBegin,

    /// <summary>Play mode ended.</summary>
    PlayEnd,

    /// <summary>A scene finished loading.</summary>
    SceneLoaded,

    /// <summary>A scene failed to load.</summary>
    SceneLoadError,

    /// <summary>A scene was saved.</summary>
    SceneSaved,

    /// <summary>A scene failed to save.</summary>
    SceneSaveError,

    /// <summary>A scene was unloaded.</summary>
    SceneUnloaded,
}

/// <summary>One recorded event.</summary>
/// <param name="Seq">Its position in the ring's sequence, from 1.</param>
/// <param name="UtcTicks">When it was recorded.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Level">The log level for log lines (the engine's LogType), else 0.</param>
/// <param name="Text">The log message, the scene name, or null.</param>
public readonly record struct BridgeEvent(long Seq, long UtcTicks, BridgeEventKind Kind, byte Level, string? Text);

/// <summary>Events after a sequence number.</summary>
/// <param name="Events">The events, oldest first.</param>
/// <param name="LastSeq">The newest sequence number recorded (0 when none).</param>
/// <param name="OldestSeq">The oldest sequence number still held.</param>
/// <param name="Truncated">Events after the requested one were overwritten before they were read.</param>
public sealed record BridgeEventPage(IReadOnlyList<BridgeEvent> Events, long LastSeq, long OldestSeq, bool Truncated);

/// <summary>
/// A fixed ring of the editor's recent events. Recording takes a lock and stores into a preallocated slot, so it makes
/// no garbage while nobody waits; it may be called from any thread (log lines come from many).
/// </summary>
public sealed class BridgeEventRing
{
    private readonly object _gate = new();
    private readonly BridgeEvent[] _slots;
    private long _lastSeq;
    private TaskCompletionSource<bool>? _waiters;

    /// <summary>Creates an empty ring.</summary>
    /// <param name="capacity">How many events it holds; older ones are overwritten.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
    public BridgeEventRing(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The event ring needs a positive capacity.");
        }

        _slots = new BridgeEvent[capacity];
    }

    /// <summary>The newest sequence number recorded (0 when none).</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    /// <summary>Records an event.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="level">The log level, or 0.</param>
    /// <param name="text">The message or the scene name, or null.</param>
    public void Append(BridgeEventKind kind, byte level, string? text)
    {
        TaskCompletionSource<bool>? waiters;
        lock (_gate)
        {
            var seq = ++_lastSeq;
            _slots[(int)(seq % _slots.Length)] = new BridgeEvent(seq, DateTime.UtcNow.Ticks, kind, level, text);
            waiters = _waiters;
            _waiters = null;
        }

        waiters?.TrySetResult(true);
    }

    /// <summary>The events after a sequence number that match a filter.</summary>
    /// <param name="afterSeq">Only events newer than this.</param>
    /// <param name="max">At most this many (the oldest first).</param>
    /// <param name="kinds">Only these kinds; null for all.</param>
    /// <returns>The page.</returns>
    public BridgeEventPage Read(long afterSeq, int max, IReadOnlyCollection<BridgeEventKind>? kinds)
    {
        lock (_gate)
        {
            var oldest = Math.Max(1, _lastSeq - _slots.Length + 1);
            var first = Math.Max(afterSeq + 1, oldest);
            var events = new List<BridgeEvent>();
            for (var seq = first; seq <= _lastSeq && events.Count < max; seq++)
            {
                var item = _slots[(int)(seq % _slots.Length)];
                if (kinds is null || Contains(kinds, item.Kind))
                {
                    events.Add(item);
                }
            }

            return new BridgeEventPage(events, _lastSeq, _lastSeq == 0 ? 0 : oldest, afterSeq + 1 < oldest && _lastSeq > 0);
        }
    }

    /// <summary>Like <see cref="Read"/>, but waits up to the given time for a matching event when there is none yet.</summary>
    /// <param name="afterSeq">Only events newer than this.</param>
    /// <param name="max">At most this many.</param>
    /// <param name="kinds">Only these kinds; null for all.</param>
    /// <param name="wait">How long to wait for one.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The page; empty when the wait ended without a match.</returns>
    public async Task<BridgeEventPage> WaitAsync(
        long afterSeq,
        int max,
        IReadOnlyCollection<BridgeEventKind>? kinds,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            Task changed;
            BridgeEventPage page;
            lock (_gate)
            {
                page = Read(afterSeq, max, kinds);
                if (page.Events.Count > 0)
                {
                    return page;
                }

                _waiters ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = _waiters.Task;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return page;
            }

            await Task.WhenAny(changed, Task.Delay(left, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            // Events that do not match move the start on, so the next look reads only what is new.
            afterSeq = Math.Max(afterSeq, page.LastSeq);
        }
    }

    private static bool Contains(IReadOnlyCollection<BridgeEventKind> kinds, BridgeEventKind kind)
    {
        foreach (var candidate in kinds)
        {
            if (candidate == kind)
            {
                return true;
            }
        }

        return false;
    }
}
