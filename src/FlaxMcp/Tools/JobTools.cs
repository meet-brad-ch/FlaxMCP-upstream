using System.ComponentModel;
using FlaxMcp.Flax;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

/// <summary>Long operations as jobs: start one, read its state, wait for it with progress notifications.</summary>
[McpServerToolType]
public sealed class JobTools
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private readonly FlaxJobManager _jobs;
    private readonly IFlaxBridgeClient _bridgeClient;

    public JobTools(FlaxJobManager jobs, IFlaxBridgeClient bridgeClient)
    {
        _jobs = jobs;
        _bridgeClient = bridgeClient;
    }

    [McpServerTool(Name = "editor_job_start", UseStructuredContent = true)]
    [Description(
        "Starts a long editor operation as a job and returns immediately. " +
        "The operations are compile (compile the scripts and wait until the editor reloads them and is idle), play_start, play_stop, play_pause, play_resume and open_scene (path). " +
        "Only one editor job runs at a time. " +
        "A second job fails with job_conflict, which names the running job. " +
        "Use job_wait or job_status to follow the job. " +
        "Its result is the same as the result of editor_compile, editor_play_mode or editor_open_scene."
    )]
    public FlaxJobHandle StartEditorJob(
        [Description("compile, play_start, play_stop, play_pause, play_resume or open_scene.")] string kind,
        [Description("For open_scene: the scene path, absolute or relative to Content/.")] string? path = null)
    {
        if (kind == "open_scene" && string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("open_scene needs a path.");
        }

        Func<FlaxJobProgress, CancellationToken, Task<object>> run = kind switch
        {
            "compile" => async (progress, cancellationToken) => await _bridgeClient.CompileAsync(cancellationToken, progress),
            "play_start" or "play_stop" or "play_pause" or "play_resume" => async (progress, cancellationToken) =>
                await _bridgeClient.SetPlayModeAsync(kind["play_".Length..], cancellationToken, progress),
            "open_scene" => async (progress, cancellationToken) =>
            {
                progress.Report("opening " + path);
                return await _bridgeClient.OpenSceneAsync(path!, cancellationToken);
            }
            ,
            _ => throw new McpException(
                $"Unknown editor job kind '{kind}'. Kinds: compile, play_start, play_stop, play_pause, play_resume, open_scene."),
        };
        return _jobs.Start(kind, exclusive: true, run);
    }

    [McpServerTool(Name = "job_status", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns the state of a job: running, succeeded (with its result), failed or canceled (with the reason), and its latest progress message. " +
        "Jobs come from editor_job_start, build_game and the editor tools that run as jobs."
    )]
    public FlaxJobState GetStatus([Description("The job id, for example job-7.")] string jobId)
    {
        return _jobs.Status(jobId);
    }

    [McpServerTool(Name = "job_wait", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Waits until a job ends, up to the timeout, and returns its state. " +
        "While it waits, the tool sends the progress messages of the job as progress notifications. " +
        "A timeout is a result (Status running), not an error."
    )]
    public Task<FlaxJobState> WaitAsync(
        [Description("The job id, for example job-7.")] string jobId,
        [Description("The maximum time to wait, up to 1800 seconds.")] double timeoutSeconds = 60,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxWait);
        return _jobs.WaitAsync(jobId, timeout, progress, cancellationToken);
    }
}
