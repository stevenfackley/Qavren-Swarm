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
/// <see cref="JobStateStore.Update"/> or <see cref="JobStateStore.TryTransition"/> under the
/// store's per-job lock.</summary>
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
    /// <summary>Set when the job entered <see cref="JobStatus.Paused"/>; drives the pause grace timeout.
    /// A hand-back sets it again (<see cref="RestorePause"/>), which restarts the window.</summary>
    public DateTimeOffset? PausedUtc { get; set; }
    /// <summary>How many times resume_job handed this job back because its replacement never started.
    /// Bounds the grace-window restarts (<see cref="JobRecovery.MaxGraceRestarts"/>), so it is persisted.</summary>
    public int HandBacks { get; set; }

    /// <summary>True once cancel_job asked this run to stop, so a cancellation is recorded as a user
    /// cancel (Failed) rather than mistaken for a hang (Paused). Not serialized.</summary>
    [JsonIgnore]
    public bool UserCancelled { get; set; }

    /// <summary>Cancellation handle for this job's background run — drives the wall-clock
    /// timeout and the cancel_job tool. Not serialized; nulled + disposed once the run ends.</summary>
    [JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>The pause that resume_job superseded, and the replacement it launched, kept so that a
    /// replacement which never starts can hand this job back (<see cref="RestorePause"/>). Null once it
    /// has been handed back, or a cancel_job has given the hand-back up. Not serialized.</summary>
    [JsonIgnore]
    public (string ReplacementId, DateTimeOffset? PausedUtc)? SupersededPause { get; set; }

    /// <summary>Move to the terminal <see cref="JobStatus.Failed"/> state. The status, the reason and
    /// the finish time are always written together.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        Status = JobStatus.Failed;
        Error = error;
        FinishedUtc = now;
    }

    /// <summary>Mark a Paused job superseded by its resume_job replacement: <see cref="JobStatus.Failed"/>,
    /// with the pause it leaves remembered in <see cref="SupersededPause"/>.</summary>
    public void Supersede(string replacementId, DateTimeOffset now)
    {
        SupersededPause = (replacementId, PausedUtc);
        Fail($"resumed as {replacementId}", now);
    }

    /// <summary>Undo <see cref="Supersede"/> after a failed relaunch: <see cref="JobStatus.Paused"/> again,
    /// with <paramref name="failure"/> as the reason and a grace window that starts again at
    /// <paramref name="now"/>. Only the first <see cref="JobRecovery.MaxGraceRestarts"/> hand-backs restart
    /// it; after that the job keeps the window it had, so failed resumes cannot keep it alive forever.
    /// Returns false, and changes nothing, when no superseded pause is left.</summary>
    public bool RestorePause(string failure, DateTimeOffset now)
    {
        if (SupersededPause is not { } pause)
            return false;
        HandBacks++;
        var restart = HandBacks <= JobRecovery.MaxGraceRestarts;
        Status = JobStatus.Paused;
        Error = restart
            ? failure
            : $"{failure}; grace window not restarted (already restarted {JobRecovery.MaxGraceRestarts} times)";
        PausedUtc = restart ? now : pause.PausedUtc;
        FinishedUtc = null;
        SupersededPause = null;
        return true;
    }
}

/// <summary>Pure decision helpers for hung-container recovery — no IO, fully unit-testable.</summary>
public static class JobRecovery
{
    /// <summary>How many hand-backs restart a job's grace window. Each one follows a resume whose launch
    /// failed, and a launch ends within the wall-clock cap, so a job stays resumable for at most
    /// <c>MaxGraceRestarts + 1</c> grace windows plus the launches in between.</summary>
    public const int MaxGraceRestarts = 3;

    /// <summary>True for <see cref="JobStatus.Completed"/> and <see cref="JobStatus.Failed"/>: the run is
    /// over and nothing moves the job on. <see cref="JobStatus.Paused"/> is not terminal, since a
    /// Paused job can still be resumed or discarded. The one way back out of Failed is
    /// <see cref="JobState.RestorePause"/>, for a job whose resume_job replacement never started.</summary>
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed;

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

    /// <summary>The reason an expired Paused job is reaped with. A job that was handed back is Paused
    /// because its last relaunch failed, which its <see cref="JobState.Error"/> says, so that is kept.</summary>
    public static string ExpiryReason(JobState job) =>
        job.HandBacks == 0
            ? "paused job expired (not resumed within grace window)"
            : $"paused job expired: {job.Error}; not resumed again within the grace window";
}
