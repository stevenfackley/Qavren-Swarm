using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace QavrenSwarm.Services;

/// <summary>Thread-safe registry of spawned jobs, optionally persisted to disk so jobs (and
/// their diffs) survive a server restart. <c>spawn_sandbox</c> creates a job and returns
/// immediately; a background task mutates it via <see cref="Update"/>.</summary>
public sealed class JobStateStore
{
    // Cap retained jobs so a long-lived session doesn't grow unbounded (each job holds its
    // full diff). Only finished jobs are evicted; Pending/Running are always kept.
    private const int MaxJobs = 200;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly bool _persist;
    private readonly string _dir;
    private readonly ILogger<JobStateStore> _log;

    public JobStateStore(SwarmConfig config, ILogger<JobStateStore> log)
        : this(config.PersistJobs, config.JobsDir, log) { }

    // Test seam: bypass SwarmConfig/env to point at an arbitrary directory.
    internal JobStateStore(bool persist, string dir, ILogger<JobStateStore> log)
    {
        _persist = persist;
        _dir = dir;
        _log = log;
        if (_persist)
        {
            try { Directory.CreateDirectory(_dir); } catch (Exception ex) { _log.LogWarning(ex, "Cannot create jobs dir {Dir}", _dir); }
            LoadFromDisk();
        }
    }

    public JobState Create(string runtime, string provider, string workspacePath, string task,
        string? model = null, int? thinkingBudget = null, string? baseUrl = null)
    {
        EvictFinishedIfFull();
        var job = new JobState
        {
            Id = Guid.NewGuid().ToString("N"),
            Runtime = runtime,
            Provider = provider,
            WorkspacePath = workspacePath,
            Task = task,
            Model = model,
            ThinkingBudget = thinkingBudget,
            BaseUrl = baseUrl,
        };
        _jobs[job.Id] = job;
        Persist(job);
        return job;
    }

    public bool TryGet(string id, out JobState job) => _jobs.TryGetValue(id, out job!);

    /// <summary>Snapshot of every job this session (for list_jobs / recovering a dropped jobId).</summary>
    public IReadOnlyList<JobState> All() => _jobs.Values.ToArray();

    /// <summary>Atomically mutate a job and persist the new state. Mutations are serialized per-job
    /// so the background runner and tool reads never tear a half-written state.</summary>
    public void Update(string id, Action<JobState> mutate)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return;
        lock (job)
        {
            mutate(job);
            Persist(job);
        }
    }

    /// <summary>Like <see cref="Update"/>, but persists the job only when <paramref name="mutate"/> returns
    /// true. For a step that reads the job under its lock to decide what to do, and may leave it as it
    /// was or change only fields that are not serialized. Returns false, without calling
    /// <paramref name="mutate"/>, when the id is unknown.</summary>
    public bool UpdateIf(string id, Func<JobState, bool> mutate)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return false;
        lock (job)
        {
            if (mutate(job))
                Persist(job);
            return true;
        }
    }

    /// <summary>Like <see cref="Update"/>, but only while the job is still in one of
    /// <paramref name="allowedFrom"/>. The status is checked under the same per-job lock as the
    /// mutation, so of two callers racing to move one job only the first acts. Returns false, and
    /// leaves the job alone, when the id is unknown or the status has already moved on.</summary>
    public bool TryTransition(string id, IReadOnlyCollection<JobStatus> allowedFrom, Action<JobState> mutate)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return false;
        lock (job)
        {
            if (!allowedFrom.Contains(job.Status))
                return false;
            mutate(job);
            Persist(job);
            return true;
        }
    }

    private void EvictFinishedIfFull()
    {
        if (_jobs.Count < MaxJobs)
            return;
        var stale = _jobs.Values
            .Where(j => j.Status.IsTerminal())
            .OrderBy(j => j.FinishedUtc ?? j.CreatedUtc)
            .Take(_jobs.Count - MaxJobs + 1)
            .ToList();
        foreach (var j in stale)
        {
            _jobs.TryRemove(j.Id, out _);
            DeleteFile(j.Id);
        }
    }

    private void Persist(JobState job)
    {
        if (!_persist)
            return;
        try
        {
            File.WriteAllText(Path.Combine(_dir, job.Id + ".json"), JsonSerializer.Serialize(job, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist job {Id}", job.Id);
        }
    }

    private void DeleteFile(string id)
    {
        if (!_persist)
            return;
        try { File.Delete(Path.Combine(_dir, id + ".json")); }
        catch (IOException) { /* best effort */ }
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_dir))
            return;
        var now = DateTimeOffset.UtcNow;
        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            JobState? job;
            try { job = JsonSerializer.Deserialize<JobState>(File.ReadAllText(file), JsonOpts); }
            catch (Exception ex) { _log.LogWarning(ex, "Skipping unreadable job file {File}", file); continue; }
            if (job is null || string.IsNullOrEmpty(job.Id))
                continue;

            // A job still Pending/Running when the server stopped can't be resumed: its background
            // task and container are gone. Mark it interrupted so it reads as a clean terminal state.
            if (job.Status is JobStatus.Pending or JobStatus.Running)
            {
                job.Fail("interrupted by server restart", now);
                _jobs[job.Id] = job;
                Persist(job);
            }
            else
            {
                _jobs[job.Id] = job;
            }
            loaded++;
        }
        if (loaded > 0)
            _log.LogInformation("Loaded {Count} persisted jobs from {Dir}", loaded, _dir);
    }
}
