using System.Text.Json.Serialization;

namespace QavrenSwarm.Services;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
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

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>Cancellation handle for this job's background run — drives the wall-clock
    /// timeout and the cancel_job tool. Not serialized; nulled + disposed once the run ends.</summary>
    [JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }
}
