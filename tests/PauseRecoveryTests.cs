using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using QavrenSwarm.Services;
using QavrenSwarm.Tools;
using Xunit;

namespace QavrenSwarm.Tests;

// Pause/recovery for a hung container: a wall-clock timeout becomes a recoverable Paused job (not a
// silent dangling Failed), a user cancel stays Failed, and a Paused job is re-spawnable / reaped.
public class PauseRecoveryTests
{
    [Fact]
    public void User_cancel_classifies_as_failed()
    {
        var (status, error) = JobRecovery.ClassifyInterruption(userCancelled: true);
        Assert.Equal(JobStatus.Failed, status);
        Assert.Contains("cancelled", error);
    }

    [Fact]
    public void Bare_timeout_classifies_as_paused()
    {
        var (status, error) = JobRecovery.ClassifyInterruption(userCancelled: false);
        Assert.Equal(JobStatus.Paused, status);
        Assert.Contains("hung", error);
    }

    [Fact]
    public void Pause_expiry_respects_the_grace_window_and_only_applies_to_paused_jobs()
    {
        var now = DateTimeOffset.UtcNow;
        var paused = Job();
        paused.Status = JobStatus.Paused;

        paused.PausedUtc = now - TimeSpan.FromMinutes(31);
        Assert.True(JobRecovery.IsPauseExpired(paused, graceSeconds: 1800, now));

        paused.PausedUtc = now - TimeSpan.FromMinutes(10);
        Assert.False(JobRecovery.IsPauseExpired(paused, graceSeconds: 1800, now));

        paused.PausedUtc = null;
        Assert.False(JobRecovery.IsPauseExpired(paused, graceSeconds: 1800, now));

        var completed = Job();
        completed.Status = JobStatus.Completed;
        completed.PausedUtc = now - TimeSpan.FromDays(1);
        Assert.False(JobRecovery.IsPauseExpired(completed, graceSeconds: 1800, now));
    }

    [Fact]
    public void Resume_creates_a_fresh_job_with_the_same_spawn_params_and_supersedes_the_old_one()
    {
        var (store, tools) = NewTools();
        var paused = store.Create("python", "anthropic", @"C:\ws", "do the thing",
            model: "claude-x", thinkingBudget: 4096, baseUrl: null);
        store.Update(paused.Id, j => { j.Status = JobStatus.Paused; j.PausedUtc = DateTimeOffset.UtcNow; });

        var fresh = tools.PrepareResume(paused);

        Assert.NotEqual(paused.Id, fresh.Id);
        Assert.Equal("python", fresh.Runtime);
        Assert.Equal("anthropic", fresh.Provider);
        Assert.Equal(@"C:\ws", fresh.WorkspacePath);
        Assert.Equal("do the thing", fresh.Task);
        Assert.Equal("claude-x", fresh.Model);
        Assert.Equal(4096, fresh.ThinkingBudget);

        Assert.True(store.TryGet(paused.Id, out var old));
        Assert.Equal(JobStatus.Failed, old.Status);
        Assert.Equal($"resumed as {fresh.Id}", old.Error);
    }

    [Fact]
    public void Resume_rejects_a_non_paused_job()
    {
        var (store, tools) = NewTools();
        var done = store.Create("python", "openai", @"C:\ws", "t");
        store.Update(done.Id, j => j.Status = JobStatus.Completed);

        var ex = Assert.Throws<McpException>(() => tools.ResumeJob(done.Id));
        Assert.Contains("only Paused", ex.Message);
    }

    [Fact]
    public void Expired_paused_job_is_reaped_to_failed_when_its_status_is_read()
    {
        var (store, tools) = NewTools();
        var paused = store.Create("python", "openai", @"C:\ws", "t");
        // Paused long ago → past even the default 30-min grace window.
        store.Update(paused.Id, j => { j.Status = JobStatus.Paused; j.PausedUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(1); });

        using var doc = JsonDocument.Parse(tools.CheckSandboxStatus(paused.Id));
        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("expired", doc.RootElement.GetProperty("error").GetString());
    }

    // --- helpers ---------------------------------------------------------- //

    private static JobState Job() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Runtime = "python",
        Provider = "openai",
        WorkspacePath = @"C:\ws",
        Task = "t",
    };

    private static (JobStateStore store, SwarmTools tools) NewTools()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qavren-pause-" + Guid.NewGuid().ToString("N"));
        var store = new JobStateStore(persist: false, dir, NullLogger<JobStateStore>.Instance);
        var docker = new DockerLifecycleManager(docker: null!, new SwarmConfig(), NullLogger<DockerLifecycleManager>.Instance);
        var tools = new SwarmTools(store, docker, new SwarmConfig(), NullLogger<SwarmTools>.Instance);
        return (store, tools);
    }
}
