using System.Collections.Concurrent;
using ModelContextProtocol;

namespace FlaxMcp.Flax;

/// <summary>
/// Long operations as jobs: a call starts one and gets its id back at once, then reads its state or waits for it
/// (with progress notifications) in later calls. Jobs belong to the server process: they outlive the call that
/// started them and the editor's script reloads. Editor operations (compile, play mode, scene) are exclusive: one at a
/// time, because each one changes the state the others wait for.
/// </summary>
public sealed class FlaxJobManager
{
    private const int KeptFinishedJobs = 256;
    private readonly ConcurrentDictionary<string, FlaxJob> _jobs = new();
    private readonly object _gate = new();
    private int _nextId;

    /// <summary>Starts work in the background and returns its handle at once.</summary>
    /// <param name="kind">What it does, for example compile.</param>
    /// <param name="exclusive">An editor operation: refused while another exclusive job runs.</param>
    /// <param name="run">The work: it reports progress and returns the result object.</param>
    /// <returns>The job's handle.</returns>
    /// <exception cref="McpException">Another exclusive job is running (job_conflict).</exception>
    public FlaxJobHandle Start(string kind, bool exclusive, Func<FlaxJobProgress, CancellationToken, Task<object>> run)
    {
        FlaxJob job;
        lock (_gate)
        {
            if (exclusive && _jobs.Values.FirstOrDefault(other => other.Exclusive && !other.Task.IsCompleted) is { } running)
            {
                throw new McpException(
                    $"job_conflict: {running.Handle.JobId} ({running.Handle.Kind}) is still running; wait for it with job_wait.");
            }

            var handle = new FlaxJobHandle($"job-{++_nextId}", kind, DateTime.UtcNow);
            var progress = new FlaxJobProgress();
            // Task.Run: the work's synchronous start runs off the calling thread, and its failure becomes the job's.
            job = new FlaxJob(handle, exclusive, progress, Task.Run(() => run(progress, CancellationToken.None)));
            _jobs[handle.JobId] = job;
            EvictFinished();
        }

        return job.Handle;
    }

    /// <summary>A job's state now.</summary>
    /// <param name="jobId">The job's id.</param>
    /// <returns>Its state.</returns>
    /// <exception cref="McpException">No job has the id.</exception>
    public FlaxJobState Status(string jobId)
    {
        return Find(jobId).State();
    }

    /// <summary>Waits for a job to end, up to a timeout, reporting its progress messages.</summary>
    /// <param name="jobId">The job's id.</param>
    /// <param name="timeout">How long to wait; the state is returned either way (Status running when it timed out).</param>
    /// <param name="progress">Receives each new progress message; may be null.</param>
    /// <param name="cancellationToken">Stops the wait, not the job.</param>
    /// <returns>The job's state.</returns>
    /// <exception cref="McpException">No job has the id.</exception>
    public async Task<FlaxJobState> WaitAsync(
        string jobId,
        TimeSpan timeout,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var job = Find(jobId);
        var deadline = DateTime.UtcNow + timeout;
        var reported = 0;
        while (!job.Task.IsCompleted)
        {
            var latest = job.Progress.Latest();
            if (progress is not null && latest.Count > reported && latest.Message is not null)
            {
                reported = latest.Count;
                progress.Report(new ProgressNotificationValue { Progress = latest.Count, Message = latest.Message });
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            await Task.WhenAny(job.Task, Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, left.TotalMilliseconds)), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        return job.State();
    }

    private FlaxJob Find(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : throw new McpException($"Unknown job id '{jobId}'.");
    }

    private void EvictFinished()
    {
        var finished = _jobs.Values.Where(job => job.Task.IsCompleted).OrderBy(job => job.Handle.StartedUtc).ToList();
        foreach (var job in finished.Take(Math.Max(0, finished.Count - KeptFinishedJobs)))
        {
            _jobs.TryRemove(job.Handle.JobId, out _);
        }
    }

    private sealed class FlaxJob(FlaxJobHandle handle, bool exclusive, FlaxJobProgress progress, Task<object> task)
    {
        private DateTime? _endedUtc;

        public FlaxJobHandle Handle { get; } = handle;

        public bool Exclusive { get; } = exclusive;

        public FlaxJobProgress Progress { get; } = progress;

        public Task<object> Task { get; } = task;

        public FlaxJobState State()
        {
            var message = Progress.Latest().Message;
            if (!Task.IsCompleted)
            {
                return new FlaxJobState(Handle.JobId, Handle.Kind, "running", Handle.StartedUtc, null, message, null, null);
            }

            _endedUtc ??= DateTime.UtcNow;
            return Task.Status switch
            {
                TaskStatus.RanToCompletion =>
                    new FlaxJobState(Handle.JobId, Handle.Kind, "succeeded", Handle.StartedUtc, _endedUtc, message, Task.Result, null),
                TaskStatus.Canceled =>
                    new FlaxJobState(Handle.JobId, Handle.Kind, "canceled", Handle.StartedUtc, _endedUtc, message, null, "The job was canceled."),
                _ =>
                    new FlaxJobState(Handle.JobId, Handle.Kind, "failed", Handle.StartedUtc, _endedUtc, message, null,
                        (Task.Exception!.InnerException ?? Task.Exception).Message),
            };
        }
    }
}

/// <summary>Progress a running job reports: its latest message and how many it has sent.</summary>
public sealed class FlaxJobProgress : IProgress<string>
{
    private readonly object _gate = new();
    private int _count;
    private string? _message;

    /// <summary>Records a progress message.</summary>
    /// <param name="value">What the job is doing now.</param>
    public void Report(string value)
    {
        lock (_gate)
        {
            _count++;
            _message = value;
        }
    }

    /// <summary>How many messages were reported, and the latest.</summary>
    /// <returns>The count and the message (null before the first).</returns>
    public FlaxJobProgressSnapshot Latest()
    {
        lock (_gate)
        {
            return new FlaxJobProgressSnapshot(_count, _message);
        }
    }
}

/// <summary>A job's progress at one moment.</summary>
/// <param name="Count">How many messages it has reported.</param>
/// <param name="Message">The latest one; null before the first.</param>
public sealed record FlaxJobProgressSnapshot(int Count, string? Message);

/// <summary>A started job.</summary>
/// <param name="JobId">Its id, for example job-7.</param>
/// <param name="Kind">What it does.</param>
/// <param name="StartedUtc">When it started.</param>
public sealed record FlaxJobHandle(string JobId, string Kind, DateTime StartedUtc);

/// <summary>A job's state.</summary>
/// <param name="JobId">Its id.</param>
/// <param name="Kind">What it does.</param>
/// <param name="Status">running, succeeded, failed or canceled.</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="EndedUtc">When it was first seen ended; null while running.</param>
/// <param name="Message">Its latest progress message.</param>
/// <param name="Result">What it returned, once succeeded.</param>
/// <param name="Error">Why it failed or was canceled.</param>
public sealed record FlaxJobState(
    string JobId,
    string Kind,
    string Status,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    string? Message,
    object? Result,
    string? Error);
