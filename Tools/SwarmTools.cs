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
    private readonly TimeProvider _time;

    public SwarmTools(JobStateStore store, DockerLifecycleManager docker, SwarmConfig config, ILogger<SwarmTools> log)
        : this(store, docker, config, log, TimeProvider.System) { }

    // Test seam: a clock the test moves, for the pause grace window.
    internal SwarmTools(JobStateStore store, DockerLifecycleManager docker, SwarmConfig config, ILogger<SwarmTools> log, TimeProvider time)
    {
        _store = store;
        _docker = docker;
        _config = config;
        _log = log;
        _time = time;
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

        var job = _store.Create(runtime, provider, Path.GetFullPath(workspacePath), task, model, thinkingBudget, baseUrl);
        _log.LogInformation("Spawned job {Id} runtime={Runtime} provider={Provider}", job.Id, runtime, provider);

        LaunchRun(job);
        return Json(new { jobId = job.Id, status = job.Status.ToString(), provider, runtime });
    }

    /// <summary>Attach a wall-clock CTS (the handle cancel_job pulls) and fire the container run on a
    /// background task. Shared by spawn_sandbox and resume_job; the run records its own outcome.
    /// <paramref name="onNeverStarted"/> runs when the run failed before its container had started,
    /// unless cancel_job stopped it: a cancelled run is not a failed launch. <paramref name="onSettled"/>
    /// runs once the container has started, and again when the run ends any other way than a failed
    /// launch, so it must be idempotent. Returns the background task, so a test can await it.</summary>
    private Task LaunchRun(JobState job, Action? onNeverStarted = null, Action? onSettled = null)
    {
        // Wall-clock cap so a hung container/model can't pin the job in Running forever.
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.JobTimeoutSeconds));
        job.Cts = cts;
        return Task.Run(async () =>
        {
            try
            {
                var neverStarted = await _docker.RunAgentAsync(_store, job, job.Model, job.ThinkingBudget, job.BaseUrl, cts.Token,
                    onStarted: onSettled);
                if (neverStarted && !job.UserCancelled)
                    onNeverStarted?.Invoke();
                else
                    onSettled?.Invoke();
            }
            finally
            {
                _store.Update(job.Id, j => j.Cts = null);
                cts.Dispose();
            }
        });
    }

    /// <summary>Lazily reap a Paused job that has sat past its grace window: no background timer,
    /// the check runs whenever a tool touches the job. The expiry is judged under the job's lock, so
    /// a resume or discard that claimed the job first, or a hand-back that has just restarted its
    /// window, wins; the reap then does nothing.</summary>
    private void ReapIfPauseExpired(JobState job) =>
        _store.UpdateIf(job.Id, j =>
        {
            var now = _time.GetUtcNow();
            if (!JobRecovery.IsPauseExpired(j, _config.PauseGraceSeconds, now))
                return false;
            j.Fail(JobRecovery.ExpiryReason(j), now);
            return true;
        });

    [McpServerTool(Name = "check_sandbox_status")]
    [Description("Check the status of a spawned sandbox job by its jobId.")]
    public string CheckSandboxStatus(
        [Description("The jobId returned by spawn_sandbox.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        ReapIfPauseExpired(job);

        return Json(new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            provider = job.Provider,
            runtime = job.Runtime,
            exitCode = job.Status.IsTerminal() ? job.ExitCode : (long?)null,
            testsPassed = job.TestsPassed,
            failedHunks = job.FailedHunks,
            hasChanges = job.Diff.Trim().Length > 0,
            // A Paused job is recoverable: resume_job re-spawns it, cancel_job discards it.
            resumable = job.Status == JobStatus.Paused,
            pausedUtc = job.PausedUtc,
            error = job.Error,
        });
    }

    [McpServerTool(Name = "list_jobs")]
    [Description("List every job spawned this session (newest first) so a dropped jobId can be recovered.")]
    public string ListJobs()
    {
        foreach (var j in _store.All())
            ReapIfPauseExpired(j);
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
    [Description("Request cancellation of a Pending/Running job (stops + removes the container, marks " +
                 "it Failed), or discard a Paused (hung) job. A job that resume_job is re-spawning, whose " +
                 "new container has not started yet, is refused with an error that names the new job: " +
                 "try again in a moment, or cancel the new job.")]
    public string CancelJob(
        [Description("The jobId to cancel.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        // One look under the job's lock decides the answer: resume_job, its hand-back and the pause
        // reaper all move the job, so an answer pieced together from separate reads can be false.
        var status = job.Status;
        string? resumingAs = null;
        _store.UpdateIf(jobId, j =>
        {
            status = j.Status;
            if (j.Status == JobStatus.Paused)
            {
                // The run has already ended (container torn down); just finalize the record.
                j.Fail("paused job discarded", _time.GetUtcNow());
                return true;
            }
            if (j.Status.IsTerminal())
            {
                // Superseded by a resume_job whose replacement has not started: not finished yet,
                // since a failed launch hands it back.
                resumingAs = j.SupersededPause?.ReplacementId;
                return false;
            }
            // Mark user-cancel BEFORE cancelling so the run records Failed (cancel), not Paused (hang).
            j.UserCancelled = true;
            return true;
        });
        if (status == JobStatus.Paused)
        {
            _log.LogInformation("Discarded paused job {Id}", jobId);
            return Json(new { jobId, status = JobStatus.Failed.ToString(), note = "paused job discarded" });
        }
        if (resumingAs is not null)
            throw new McpException($"job {jobId} is being resumed as {resumingAs} and its container has not started yet; " +
                                   $"try again in a moment, or cancel_job {resumingAs} to stop the resumed run");
        if (status.IsTerminal())
            return Json(new { jobId, status = status.ToString(), note = "job already finished" });
        try { job.Cts?.Cancel(); }
        catch (ObjectDisposedException) { /* run is already completing */ }
        _log.LogInformation("Cancellation requested for job {Id}", jobId);
        return Json(new { jobId, cancelling = true });
    }

    [McpServerTool(Name = "resume_job")]
    [Description("Resume a Paused (hung) job by re-spawning a fresh container with the same runtime, " +
                 "workspace, task, and provider settings. Returns the new jobId; the old job is marked " +
                 "Failed (superseded). If the new job fails before its container starts (for example " +
                 "Docker is not running), the old job goes back to Paused and can be resumed again; its " +
                 "grace window starts again, at most 3 times. " +
                 "Only Paused jobs can be resumed.")]
    public string ResumeJob(
        [Description("The Paused jobId to resume.")] string jobId)
    {
        var (fresh, _) = BeginResume(jobId);
        return Json(new { resumedFrom = jobId, jobId = fresh.Id, status = fresh.Status.ToString(), provider = fresh.Provider, runtime = fresh.Runtime });
    }

    /// <summary>resume_job without the response: claim the Paused job and launch its replacement,
    /// handing the old job back if the replacement never starts. Returns the replacement with its
    /// background run, so a test can await the outcome.</summary>
    internal (JobState fresh, Task run) BeginResume(string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        ReapIfPauseExpired(job);

        var fresh = PrepareResume(job);
        var run = LaunchRun(fresh, onNeverStarted: () => HandBack(job, fresh), onSettled: () => Settle(job, fresh));
        _log.LogInformation("Resumed job {Old} as {New}", jobId, fresh.Id);
        return (fresh, run);
    }

    /// <summary>The replacement never ran, so the resume took the work nowhere: put the job it
    /// superseded back to Paused, resumable again, with the failed relaunch as its reason and its grace
    /// window restarted (<see cref="JobState.RestorePause"/>). Does nothing unless this replacement's
    /// launch is still the undecided one.</summary>
    private void HandBack(JobState superseded, JobState replacement)
    {
        var restored = false;
        var failure = $"resume as {replacement.Id} never started ({replacement.Error})";
        _store.TryTransition(superseded.Id, [JobStatus.Failed],
            j => restored = j.RestorePause(replacement.Id, failure, _time.GetUtcNow()));
        if (restored)
            _log.LogWarning("Job {New} never started; job {Old} is Paused again and can be resumed", replacement.Id, superseded.Id);
    }

    /// <summary>The replacement's launch is decided without a hand-back: its container started, or a
    /// cancel or a timeout ended the run. The job it superseded is finished for good from here on, so
    /// a cancel_job of it can say so.</summary>
    private void Settle(JobState superseded, JobState replacement) =>
        _store.UpdateIf(superseded.Id, j =>
        {
            j.EndSupersededPause(replacement.Id);
            return false;
        });

    /// <summary>Claim the Paused job, create its replacement and mark the old one superseded — the
    /// state transition only, no container launch, so it is unit-testable without Docker. The claim
    /// is taken under the old job's lock before the replacement exists, so two resume_job calls on
    /// one job cannot both create (and launch) a replacement. Throws when the job is not Paused.</summary>
    internal JobState PrepareResume(JobState paused)
    {
        JobState? fresh = null;
        var claimed = _store.TryTransition(paused.Id, [JobStatus.Paused], j =>
        {
            fresh = _store.Create(j.Runtime, j.Provider, j.WorkspacePath, j.Task,
                j.Model, j.ThinkingBudget, j.BaseUrl);
            j.Supersede(fresh.Id, _time.GetUtcNow());
        });
        if (!claimed)
            throw new McpException($"job {paused.Id} is {paused.Status}; only Paused jobs can be resumed");
        return fresh!;
    }

    [McpServerTool(Name = "retrieve_diff")]
    [Description("Retrieve the unified git diff produced by a completed sandbox job. The diff is " +
                 "advisory only — nothing is written to the host workspace until apply_diff is called.")]
    public string RetrieveDiff(
        [Description("The jobId returned by spawn_sandbox.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        ReapIfPauseExpired(job);
        if (job.Status is JobStatus.Pending or JobStatus.Running)
            return Json(new { jobId, status = job.Status.ToString(), note = "job still running; poll check_sandbox_status" });
        if (job.Status == JobStatus.Paused)
            return Json(new { jobId, status = job.Status.ToString(), note = "job hung and is paused; resume_job to retry or cancel_job to discard" });

        return Json(new
        {
            jobId,
            status = job.Status.ToString(),
            testsPassed = job.TestsPassed,
            failedHunks = job.FailedHunks,
            diff = job.Diff,
        });
    }

    [McpServerTool(Name = "retrieve_logs")]
    [Description("Retrieve a job's captured container stderr (the agent's diagnostics) and error " +
                 "reason — for debugging a Failed run. Empty until the job has started producing output.")]
    public string RetrieveLogs(
        [Description("The jobId returned by spawn_sandbox.")] string jobId)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");

        return Json(new
        {
            jobId,
            status = job.Status.ToString(),
            error = job.Error,
            stderrTail = job.StdErrTail ?? "",
        });
    }

    [McpServerTool(Name = "apply_diff")]
    [Description("Apply a completed sandbox job's diff to the real host workspace via `git apply`. " +
                 "This is the only operation that mutates host files; call it only after reviewing " +
                 "the diff from retrieve_diff. If the patch does not apply as a whole, it is retried " +
                 "hunk-by-hunk: every hunk that still applies is applied, and the rejected hunks are " +
                 "returned as `rejectedDiff` (a unified patch you can apply by hand) — set " +
                 "allowPartial=false for the old all-or-nothing behavior.")]
    public async Task<string> ApplyDiff(
        [Description("The jobId whose diff should be applied.")] string jobId,
        [Description("When true (default), a diff that fails as a whole is applied hunk-by-hunk and " +
                     "the rejected hunks are exported. When false, any failure aborts with no changes.")]
        bool allowPartial = true)
    {
        if (!_store.TryGet(jobId, out var job))
            throw new McpException($"unknown jobId: '{jobId}'");
        if (job.Status != JobStatus.Completed)
            throw new McpException($"job {jobId} is {job.Status}; only Completed jobs can be applied");
        if (string.IsNullOrWhiteSpace(job.Diff))
            return Json(new { jobId, applied = false, note = "job produced no changes to apply" });

        var outcome = await ApplyDiffCore(job.WorkspacePath, job.Diff, allowPartial);

        // allowPartial=false keeps the original strict contract: a non-applying patch is an error.
        if (!outcome.Applied && !allowPartial)
            throw new McpException($"git apply failed: {outcome.Note}");

        if (outcome.Applied && !outcome.Partial)
            _log.LogInformation("Applied diff for job {Id} to {Path}", jobId, job.WorkspacePath);
        else if (outcome.Partial)
            _log.LogWarning("Partial apply for job {Id}: {Applied}/{Total} hunks applied", jobId,
                outcome.AppliedHunks, outcome.AppliedHunks + outcome.FailedHunks);
        else
            _log.LogWarning("Diff for job {Id} did not apply; {Failed} hunks rejected", jobId, outcome.FailedHunks);

        return Json(new
        {
            jobId,
            applied = outcome.Applied,
            partial = outcome.Partial,
            appliedHunks = outcome.AppliedHunks,
            failedHunks = outcome.FailedHunks,
            workspacePath = job.WorkspacePath,
            rejectedHunks = outcome.Hunks.Where(h => !h.Applied)
                .Select(h => new { path = h.Path, hunk = h.Hunk, reason = h.Reason }).ToList(),
            // The rejected hunks as a unified patch — data only; nothing here writes the workspace.
            rejectedDiff = outcome.RejectedDiff,
            note = outcome.Note,
        });
    }

    internal sealed record HunkOutcome(string Path, int Hunk, bool Applied, string? Reason);

    internal sealed record ApplyOutcome(
        bool Applied, bool Partial, int AppliedHunks, int FailedHunks,
        string RejectedDiff, IReadOnlyList<HunkOutcome> Hunks, string? Note);

    /// <summary>Apply <paramref name="diff"/> to <paramref name="workspacePath"/> via <c>git apply</c>,
    /// recovering hunk-by-hunk when the whole patch is rejected. <c>git apply</c> is the ONLY operation
    /// that touches host files here — the per-hunk retry just feeds it smaller, individually-validated
    /// patches; the rejected hunks are returned as data, never written. Internal for unit testing.</summary>
    internal static async Task<ApplyOutcome> ApplyDiffCore(string workspacePath, string diff, bool allowPartial)
    {
        var files = UnifiedDiffSplitter.Parse(diff);
        var totalHunks = UnifiedDiffSplitter.CountHunks(files);

        // 1. Fast path: apply the whole patch atomically (the original, unchanged behavior).
        var (wholeOk, wholeErr) = await CheckAndApply(workspacePath, EnsureTrailingNewline(diff));
        if (wholeOk)
            return new ApplyOutcome(true, false, totalHunks, 0, "", Array.Empty<HunkOutcome>(), null);

        // Whole patch rejected. Either fail (strict mode / unsplittable) or retry hunk-by-hunk.
        if (!allowPartial || files.Count == 0)
            return new ApplyOutcome(false, false, 0, totalHunks, diff, Array.Empty<HunkOutcome>(),
                $"patch does not apply cleanly: {Short(wholeErr)}");

        // 2. Probe each hunk on its own against the (still unmodified) workspace.
        var results = new List<HunkOutcome>();
        var passed = new HashSet<(DiffFile, int)>();
        foreach (var f in files)
        {
            for (var hi = 0; hi < f.Hunks.Count; hi++)
            {
                var patch = EnsureTrailingNewline(UnifiedDiffSplitter.SingleHunkPatch(f, hi));
                var (ok, err) = await CheckPatch(workspacePath, patch);
                if (ok) passed.Add((f, hi));
                results.Add(new HunkOutcome(f.Path, hi, ok, ok ? null : Short(err)));
            }
        }

        var goodPatch = UnifiedDiffSplitter.Recompose(files, (f, i) => passed.Contains((f, i)));
        var rejectedPatch = UnifiedDiffSplitter.Recompose(files, (f, i) => !passed.Contains((f, i)));

        // 3. Apply every passing hunk in ONE git apply so cumulative line offsets stay consistent.
        var appliedHunks = 0;
        string? note = null;
        if (goodPatch.Length > 0)
        {
            var (ok, err) = await CheckAndApply(workspacePath, EnsureTrailingNewline(goodPatch));
            if (ok)
            {
                appliedHunks = passed.Count;
            }
            else
            {
                // Defensive: the good subset unexpectedly failed as a group — apply nothing, export all.
                note = $"partial apply aborted (good subset rejected): {Short(err)}";
                rejectedPatch = diff;
                results = results.Select(r => r with { Applied = false }).ToList();
            }
        }

        var failedHunks = totalHunks - appliedHunks;
        return new ApplyOutcome(
            Applied: appliedHunks > 0,
            Partial: appliedHunks > 0 && failedHunks > 0,
            AppliedHunks: appliedHunks,
            FailedHunks: failedHunks,
            RejectedDiff: failedHunks > 0 ? rejectedPatch : "",
            Hunks: results,
            Note: note);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string EnsureTrailingNewline(string s) => s.Length == 0 || s.EndsWith('\n') ? s : s + "\n";

    private static string Short(string s) => string.IsNullOrEmpty(s) ? s : (s.Length <= 300 ? s.Trim() : s[..300].Trim());

    private static string TempPatchPath() => Path.Combine(Path.GetTempPath(), $"qavren-{Guid.NewGuid():N}.patch");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    /// <summary>`git apply --check` only — does not touch the workspace.</summary>
    private static async Task<(bool ok, string err)> CheckPatch(string workspacePath, string patch)
    {
        var tmp = TempPatchPath();
        // Preserve the patch bytes exactly (incl. CRs) so it matches CRLF host files.
        await File.WriteAllTextAsync(tmp, patch, Utf8NoBom);
        try { return await RunGit(workspacePath, "apply", "--check", "--whitespace=nowarn", tmp); }
        finally { TryDelete(tmp); }
    }

    /// <summary>`git apply --check` then `git apply` — the sole host-file write path.</summary>
    private static async Task<(bool ok, string err)> CheckAndApply(string workspacePath, string patch)
    {
        var tmp = TempPatchPath();
        await File.WriteAllTextAsync(tmp, patch, Utf8NoBom);
        try
        {
            var (checkOk, checkErr) = await RunGit(workspacePath, "apply", "--check", "--whitespace=nowarn", tmp);
            if (!checkOk) return (false, checkErr);
            return await RunGit(workspacePath, "apply", "--whitespace=nowarn", tmp);
        }
        finally { TryDelete(tmp); }
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
