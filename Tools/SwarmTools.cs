using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using QavrenSwarm.Services;

namespace QavrenSwarm.Tools;

/// <summary>The MCP tool surface: spawn an ephemeral sandbox, poll it, retrieve its diff,
/// and explicitly apply that diff back to the host workspace.</summary>
[McpServerToolType]
public sealed class SwarmTools
{
    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase) { "node", "python" };
    private static readonly HashSet<string> Providers = new(StringComparer.OrdinalIgnoreCase) { "anthropic", "openai", "claude-code" };

    private readonly JobStateStore _store;
    private readonly DockerLifecycleManager _docker;
    private readonly SwarmConfig _config;
    private readonly ILogger<SwarmTools> _log;

    public SwarmTools(JobStateStore store, DockerLifecycleManager docker, SwarmConfig config, ILogger<SwarmTools> log)
    {
        _store = store;
        _docker = docker;
        _config = config;
        _log = log;
    }

    [McpServerTool(Name = "spawn_sandbox")]
    [Description("Spawn an ephemeral Docker container that runs a coding agent against a copy of " +
                 "the given host workspace and produces a git diff. Returns a jobId immediately; " +
                 "poll with check_sandbox_status. The host workspace is mounted read-only and is " +
                 "never modified until you call apply_diff.")]
    public string SpawnSandbox(
        [Description("Runtime image: 'node' (node:22-alpine) or 'python' (python:3.12-slim).")] string runtime,
        [Description("Absolute Windows path to the workspace to operate on, e.g. C:\\\\Projects\\\\App.")] string workspacePath,
        [Description("Natural-language coding task for the agent to perform.")] string task,
        [Description("Backend: 'anthropic', 'openai', or 'claude-code'. Defaults to the server default (claude-code).")] string? provider = null,
        [Description("Optional model id override for the chosen provider.")] string? model = null,
        [Description("Optional extended-thinking token budget (anthropic provider only).")] int? thinkingBudget = null,
        [Description("Optional OpenAI-compatible base URL override for the 'openai' provider only " +
                     "(e.g. http://host.docker.internal:1234/v1). Ignored for other providers.")] string? baseUrl = null)
    {
        runtime = (runtime ?? "").Trim().ToLowerInvariant();
        provider = string.IsNullOrWhiteSpace(provider) ? _config.DefaultProvider : provider.Trim().ToLowerInvariant();
        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();

        if (!Runtimes.Contains(runtime))
            throw new McpException($"invalid runtime '{runtime}' (expected node|python)");
        if (!Providers.Contains(provider))
            throw new McpException($"invalid provider '{provider}' (expected anthropic|openai|claude-code)");
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            throw new McpException($"workspacePath does not exist: '{workspacePath}'");
        if (string.IsNullOrWhiteSpace(task))
            throw new McpException("task must not be empty");
        if (baseUrl is not null && !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"baseUrl must be an http(s) URL: '{baseUrl}'");

        var job = _store.Create(runtime, provider, Path.GetFullPath(workspacePath), task);
        _log.LogInformation("Spawned job {Id} runtime={Runtime} provider={Provider}", job.Id, runtime, provider);

        // Wall-clock cap so a hung container/model can't pin the job in Running forever;
        // this CTS is also the handle cancel_job pulls. Disposed once the run completes.
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.JobTimeoutSeconds));
        job.Cts = cts;
        _ = Task.Run(async () =>
        {
            try { await _docker.RunAgentAsync(_store, job, model, thinkingBudget, baseUrl, cts.Token); }
            finally
            {
                _store.Update(job.Id, j => j.Cts = null);
                cts.Dispose();
            }
        });

        return Json(new { jobId = job.Id, status = job.Status.ToString(), provider, runtime });
    }

    [McpServerTool(Name = "check_sandbox_status")]
    [Description("Check the status of a spawned sandbox job by its jobId.")]
    public string CheckSandboxStatus(
        [Description("The jobId returned by spawn_sandbox.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");

        return Json(new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            provider = job.Provider,
            runtime = job.Runtime,
            exitCode = job.Status is JobStatus.Completed or JobStatus.Failed ? job.ExitCode : (long?)null,
            testsPassed = job.TestsPassed,
            failedHunks = job.FailedHunks,
            hasChanges = job.Diff.Trim().Length > 0,
            error = job.Error,
        });
    }

    [McpServerTool(Name = "list_jobs")]
    [Description("List every job spawned this session (newest first) so a dropped jobId can be recovered.")]
    public string ListJobs()
    {
        var jobs = _store.All()
            .OrderByDescending(j => j.CreatedUtc)
            .Select(j => new
            {
                jobId = j.Id,
                status = j.Status.ToString(),
                runtime = j.Runtime,
                provider = j.Provider,
                task = j.Task.Length > 80 ? j.Task[..80] + "…" : j.Task,
                createdUtc = j.CreatedUtc,
                hasChanges = j.Diff.Trim().Length > 0,
                testsPassed = j.TestsPassed,
            })
            .ToList();
        return Json(new { count = jobs.Count, jobs });
    }

    [McpServerTool(Name = "cancel_job")]
    [Description("Request cancellation of a Pending/Running job: stops + removes the container and marks the job Failed.")]
    public string CancelJob(
        [Description("The jobId to cancel.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        if (job.Status is JobStatus.Completed or JobStatus.Failed)
            return Json(new { jobId, status = job.Status.ToString(), note = "job already finished" });
        try { job.Cts?.Cancel(); }
        catch (ObjectDisposedException) { /* run is already completing */ }
        _log.LogInformation("Cancellation requested for job {Id}", jobId);
        return Json(new { jobId, cancelling = true });
    }

    [McpServerTool(Name = "retrieve_diff")]
    [Description("Retrieve the unified git diff produced by a completed sandbox job. The diff is " +
                 "advisory only — nothing is written to the host workspace until apply_diff is called.")]
    public string RetrieveDiff(
        [Description("The jobId returned by spawn_sandbox.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        if (job.Status is JobStatus.Pending or JobStatus.Running)
            return Json(new { jobId, status = job.Status.ToString(), note = "job still running; poll check_sandbox_status" });

        return Json(new
        {
            jobId,
            status = job.Status.ToString(),
            testsPassed = job.TestsPassed,
            failedHunks = job.FailedHunks,
            diff = job.Diff,
        });
    }

    [McpServerTool(Name = "apply_diff")]
    [Description("Apply a completed sandbox job's diff to the real host workspace via `git apply`. " +
                 "This is the only operation that mutates host files; call it only after reviewing " +
                 "the diff from retrieve_diff.")]
    public async Task<string> ApplyDiff(
        [Description("The jobId whose diff should be applied.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        if (job.Status != JobStatus.Completed)
            throw new McpException($"job {jobId} is {job.Status}; only Completed jobs can be applied");
        if (string.IsNullOrWhiteSpace(job.Diff))
            return Json(new { jobId, applied = false, note = "job produced no changes to apply" });

        var tmp = Path.Combine(Path.GetTempPath(), $"qavren-{jobId}.patch");
        // Preserve the diff bytes exactly (incl. CRs) so the patch matches CRLF host files.
        var patch = job.Diff.EndsWith('\n') ? job.Diff : job.Diff + "\n";
        await File.WriteAllTextAsync(tmp, patch, new UTF8Encoding(false));

        try
        {
            var (checkOk, checkErr) = await RunGit(job.WorkspacePath, "apply", "--check", "--whitespace=nowarn", tmp);
            if (!checkOk)
                throw new McpException($"git apply --check failed (patch does not apply cleanly): {checkErr.Trim()}");

            var (applyOk, applyErr) = await RunGit(job.WorkspacePath, "apply", "--whitespace=nowarn", tmp);
            if (!applyOk)
                throw new McpException($"git apply failed: {applyErr.Trim()}");

            _log.LogInformation("Applied diff for job {Id} to {Path}", jobId, job.WorkspacePath);
            return Json(new { jobId, applied = true, workspacePath = job.WorkspacePath });
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<(bool ok, string stderr)> RunGit(string workspacePath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardInput = true,   // never let git inherit the MCP stdio pipe
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workspacePath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();

        // Drain BOTH streams concurrently so a full pipe buffer can never deadlock,
        // and cap the whole operation so a wedged git can't hang the tool call.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, "git timed out after 60s");
        }
        var stderr = await stderrTask;
        await stdoutTask;
        return (proc.ExitCode == 0, stderr);
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);
}
