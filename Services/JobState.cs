using System.Text.Json.Serialization;

namespace QavrenSwarm.Services;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    /// <summary>The container hung past its wall-clock cap and was torn down, but the job is kept
    /// recoverable (resume_job re-spawns it) instead of being lost as a dangling Failed run.
    /// Lazily reaped to Failed once its grace window elapses.</summary>
    Paused,
}

/// <summary>Mutable record of one ephemeral container run. Mutated only via
/// <see cref="JobStateStore.Update"/> under the store's lock.</summary>
public sealed class JobState
{
    public required string Id { get; init; }
    public required string Runtime { get; init; }       // node | python
    public required string Provider { get; init; }      // anthropic | openai | claude-code
    public required string WorkspacePath { get; init; }
    public required string Task { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? ContainerId { get; set; }
    public long ExitCode { get; set; }
    public string Diff { get; set; } = "";
    public bool? TestsPassed { get; set; }
    public int FailedHunks { get; set; }
    public string? StdErrTail { get; set; }
    public string? Error { get; set; }

    // Spawn parameters retained so a hung (Paused) job can be re-spawned faithfully by resume_job.
    public string? Model { get; set; }
    public int? ThinkingBudget { get; set; }
    public string? BaseUrl { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    /// <summary>Set when the job entered <see cref="JobStatus.Paused"/>; drives the pause grace timeout.</summary>
    public DateTimeOffset? PausedUtc { get; set; }

    /// <summary>True once cancel_job asked this run to stop, so a cancellation is recorded as a user
    /// cancel (Failed) rather than mistaken for a hang (Paused). Not serialized.</summary>
    [JsonIgnore]
    public bool UserCancelled { get; set; }

    /// <summary>Cancellation handle for this job's background run — drives the wall-clock
    /// timeout and the cancel_job tool. Not serialized; nulled + disposed once the run ends.</summary>
    [JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }
}

/// <summary>Pure decision helpers for hung-container recovery — no IO, fully unit-testable.</summary>
public static class JobRecovery
{
    /// <summary>Classify why a run's awaits were cancelled. A user-requested cancel is a terminal
    /// <see cref="JobStatus.Failed"/>; a wall-clock timeout with no user cancel is a hang, which
    /// becomes a recoverable <see cref="JobStatus.Paused"/>.</summary>
    public static (JobStatus status, string error) ClassifyInterruption(bool userCancelled) =>
        userCancelled
            ? (JobStatus.Failed, "cancelled by user")
            : (JobStatus.Paused, "container hung: wall-clock timeout elapsed — paused for recovery (resume_job to retry, cancel_job to discard)");

    /// <summary>True when a Paused job has sat past its grace window and should be reaped to Failed.</summary>
    public static bool IsPauseExpired(JobState job, int graceSeconds, DateTimeOffset now) =>
        job.Status == JobStatus.Paused
        && job.PausedUtc is { } paused
        && now - paused > TimeSpan.FromSeconds(graceSeconds);
}
