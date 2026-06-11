using System.Collections.Concurrent;

namespace QavrenSwarm.Services;

/// <summary>Thread-safe registry of spawned jobs. <c>spawn_sandbox</c> creates a
/// job and returns immediately; a background task mutates it via <see cref="Update"/>.</summary>
public sealed class JobStateStore
{
    // Cap retained jobs so a long-lived session doesn't grow unbounded (each job holds its
    // full diff). Only finished jobs are evicted; Pending/Running are always kept.
    private const int MaxJobs = 200;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new();

    public JobState Create(string runtime, string provider, string workspacePath, string task)
    {
        EvictFinishedIfFull();
        var job = new JobState
        {
            Id = Guid.NewGuid().ToString("N"),
            Runtime = runtime,
            Provider = provider,
            WorkspacePath = workspacePath,
            Task = task,
        };
        _jobs[job.Id] = job;
        return job;
    }

    private void EvictFinishedIfFull()
    {
        if (_jobs.Count < MaxJobs)
            return;
        var stale = _jobs.Values
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed)
            .OrderBy(j => j.FinishedUtc ?? j.CreatedUtc)
            .Take(_jobs.Count - MaxJobs + 1);
        foreach (var j in stale)
            _jobs.TryRemove(j.Id, out _);
    }

    public bool TryGet(string id, out JobState job) => _jobs.TryGetValue(id, out job!);

    /// <summary>Snapshot of every job this session (for list_jobs / recovering a dropped jobId).</summary>
    public IReadOnlyList<JobState> All() => _jobs.Values.ToArray();

    /// <summary>Atomically mutate a job. Mutations are serialized per-job so the
    /// background runner and tool reads never tear a half-written state.</summary>
    public void Update(string id, Action<JobState> mutate)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return;
        lock (job)
        {
            mutate(job);
        }
    }
}
