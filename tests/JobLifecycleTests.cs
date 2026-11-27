using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using QavrenSwarm.Services;
using QavrenSwarm.Tools;
using Xunit;

namespace QavrenSwarm.Tests;

// Job lifecycle transitions as the tools and the store write them: cancel, discard, resume, reap,
// restart, eviction and a run that throws. Each test pins the status, the reason and the timestamps
// a transition leaves behind, so the writes cannot drift apart.
public class JobLifecycleTests
{
    // --- cancel_job ------------------------------------------------------- //

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public void Cancel_of_a_finished_job_reports_already_finished_and_changes_nothing(JobStatus finished)
    {
        var (store, tools) = NewTools();
        var job = store.Create("python", "openai", @"C:\ws", "t");
        store.Update(job.Id, j => { j.Status = finished; j.Error = "earlier reason"; });

        using var doc = JsonDocument.Parse(tools.CancelJob(job.Id));

        Assert.Equal(finished.ToString(), doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("job already finished", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(finished, job.Status);
        Assert.Equal("earlier reason", job.Error);
        Assert.False(job.UserCancelled);
    }

    [Fact]
    public void Cancel_of_a_paused_job_discards_it_as_failed_with_a_finish_time()
    {
        var (store, tools) = NewTools();
        var job = Paused(store, DateTimeOffset.UtcNow);

        using var doc = JsonDocument.Parse(tools.CancelJob(job.Id));

        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("paused job discarded", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("paused job discarded", job.Error);
        Assert.NotNull(job.FinishedUtc);
    }

    [Fact]
    public void Cancel_of_a_pending_job_flags_a_user_cancel_and_leaves_the_status_to_the_run()
    {
        var (store, tools) = NewTools();
        var job = store.Create("python", "openai", @"C:\ws", "t");

        using var doc = JsonDocument.Parse(tools.CancelJob(job.Id));

        Assert.True(doc.RootElement.GetProperty("cancelling").GetBoolean());
        Assert.True(job.UserCancelled);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.FinishedUtc);
    }

    [Fact]
    public void Cancel_while_the_replacement_has_not_started_is_refused_and_keeps_the_superseded_record()
    {
        var (store, tools) = NewTools();
        var old = Paused(store, DateTimeOffset.UtcNow);
        var fresh = tools.PrepareResume(old);   // claimed, and the replacement not launched yet

        var ex = Assert.Throws<McpException>(() => tools.CancelJob(old.Id));

        Assert.Equal($"job {old.Id} is being resumed as {fresh.Id}, whose container has not started yet; " +
                     $"cancel_job {fresh.Id} stops it", ex.Message);
        Assert.Equal(JobStatus.Failed, old.Status);
        Assert.Equal($"resumed as {fresh.Id}", old.Error);
    }

    // --- resume_job ------------------------------------------------------- //

    [Fact]
    public void Resume_finishes_the_old_job_and_a_second_resume_of_it_is_refused()
    {
        var (store, tools) = NewTools();
        var old = Paused(store, DateTimeOffset.UtcNow);

        var fresh = tools.PrepareResume(old);

        Assert.Equal(JobStatus.Failed, old.Status);
        Assert.NotNull(old.FinishedUtc);
        Assert.Equal(JobStatus.Pending, fresh.Status);
        Assert.Equal(2, store.All().Count);

        var ex = Assert.Throws<McpException>(() => tools.ResumeJob(old.Id));
        Assert.Equal($"job {old.Id} is Failed; only Paused jobs can be resumed", ex.Message);
        Assert.Equal(2, store.All().Count);   // the refused resume created nothing
    }

    // --- pause reaping ---------------------------------------------------- //

    [Fact]
    public void Unexpired_paused_job_stays_resumable_when_its_status_is_read()
    {
        var (store, tools) = NewTools();
        var job = Paused(store, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        using var doc = JsonDocument.Parse(tools.CheckSandboxStatus(job.Id));

        Assert.Equal("Paused", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("resumable").GetBoolean());
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.Null(job.FinishedUtc);
    }

    [Fact]
    public void Expired_paused_job_is_reaped_with_a_reason_and_finish_time_by_list_jobs()
    {
        var (store, tools) = NewTools();
        var job = Paused(store, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));

        using var doc = JsonDocument.Parse(tools.ListJobs());

        Assert.Equal("Failed", doc.RootElement.GetProperty("jobs")[0].GetProperty("status").GetString());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("paused job expired (not resumed within grace window)", job.Error);
        Assert.NotNull(job.FinishedUtc);
    }

    [Fact]
    public void Expired_paused_job_cannot_be_resumed()
    {
        var (store, tools) = NewTools();
        var job = Paused(store, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));

        var ex = Assert.Throws<McpException>(() => tools.ResumeJob(job.Id));

        Assert.Equal($"job {job.Id} is Failed; only Paused jobs can be resumed", ex.Message);
        Assert.Equal("paused job expired (not resumed within grace window)", job.Error);
        Assert.Single(store.All());
    }

    // --- check_sandbox_status exit code ----------------------------------- //

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Running, false)]
    [InlineData(JobStatus.Paused, false)]
    public void Exit_code_is_reported_only_for_finished_jobs(JobStatus status, bool reported)
    {
        var (store, tools) = NewTools();
        var job = store.Create("python", "openai", @"C:\ws", "t");
        // PausedUtc = now keeps a Paused job inside its grace window, so the read does not reap it.
        store.Update(job.Id, j => { j.Status = status; j.ExitCode = 3; j.PausedUtc = DateTimeOffset.UtcNow; });

        using var doc = JsonDocument.Parse(tools.CheckSandboxStatus(job.Id));
        var exitCode = doc.RootElement.GetProperty("exitCode");

        if (reported)
            Assert.Equal(3, exitCode.GetInt64());
        else
            Assert.Equal(JsonValueKind.Null, exitCode.ValueKind);
    }

    // --- store: eviction and restart -------------------------------------- //

    [Fact]
    public void Eviction_removes_only_finished_jobs_oldest_first()
    {
        var store = NewStore(persist: false, out _);
        var now = DateTimeOffset.UtcNow;
        var jobs = Enumerable.Range(0, 200).Select(i => store.Create("python", "openai", @"C:\ws", $"t{i}")).ToList();
        store.Update(jobs[0].Id, j => { j.Status = JobStatus.Completed; j.FinishedUtc = now - TimeSpan.FromHours(2); });
        store.Update(jobs[1].Id, j => { j.Status = JobStatus.Failed; j.FinishedUtc = now - TimeSpan.FromHours(1); });
        store.Update(jobs[2].Id, j => { j.Status = JobStatus.Paused; j.PausedUtc = now - TimeSpan.FromHours(3); });
        store.Update(jobs[3].Id, j => j.Status = JobStatus.Running);

        store.Create("python", "openai", @"C:\ws", "201");   // full: evicts the oldest finished job
        Assert.False(store.TryGet(jobs[0].Id, out _));
        Assert.True(store.TryGet(jobs[1].Id, out _));

        store.Create("python", "openai", @"C:\ws", "202");   // evicts the next finished job
        Assert.False(store.TryGet(jobs[1].Id, out _));

        store.Create("python", "openai", @"C:\ws", "203");   // nothing finished left: nothing evicted
        Assert.Equal(201, store.All().Count);
        Assert.True(store.TryGet(jobs[2].Id, out _));         // Paused is not finished
        Assert.True(store.TryGet(jobs[3].Id, out _));         // nor is Running
    }

    [Fact]
    public void Restart_stamps_a_finish_time_on_interrupted_jobs_and_keeps_paused_jobs_paused()
    {
        var s1 = NewStore(persist: true, out var dir);
        try
        {
            var pausedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            var pending = s1.Create("python", "openai", @"C:\x", "pending");
            var paused = s1.Create("python", "openai", @"C:\x", "paused");
            s1.Update(paused.Id, j => { j.Status = JobStatus.Paused; j.PausedUtc = pausedAt; });

            var s2 = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);

            Assert.True(s2.TryGet(pending.Id, out var p));
            Assert.Equal(JobStatus.Failed, p.Status);
            Assert.Equal("interrupted by server restart", p.Error);
            Assert.NotNull(p.FinishedUtc);

            Assert.True(s2.TryGet(paused.Id, out var q));
            Assert.Equal(JobStatus.Paused, q.Status);
            Assert.Equal(pausedAt, q.PausedUtc);
            Assert.Null(q.FinishedUtc);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // --- a run that throws ------------------------------------------------ //

    [Fact]
    public async Task Run_that_throws_is_recorded_as_failed_with_the_exception_and_a_finish_time()
    {
        var store = NewStore(persist: false, out _);
        // No Docker client: the first Docker call throws, which is the run's generic failure path.
        var docker = new DockerLifecycleManager(docker: null!, new SwarmConfig(), NullLogger<DockerLifecycleManager>.Instance);
        var job = store.Create("python", "openai", @"C:\ws", "t");

        await docker.RunAgentAsync(store, job, model: null, thinkingBudget: null, baseUrl: null, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith("NullReferenceException: ", job.Error);
        Assert.NotNull(job.FinishedUtc);
        Assert.Null(job.PausedUtc);
    }

    // --- the transition helpers ------------------------------------------ //

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Running, false)]
    [InlineData(JobStatus.Paused, false)]
    public void IsTerminal_is_true_only_for_completed_and_failed(JobStatus status, bool terminal) =>
        Assert.Equal(terminal, status.IsTerminal());

    [Fact]
    public void Fail_sets_status_reason_and_finish_time_together()
    {
        var job = new JobState { Id = "j", Runtime = "python", Provider = "openai", WorkspacePath = @"C:\ws", Task = "t" };
        var now = DateTimeOffset.UtcNow;

        job.Fail("why", now);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("why", job.Error);
        Assert.Equal(now, job.FinishedUtc);
    }

    [Fact]
    public void TryTransition_mutates_and_persists_when_the_status_is_allowed()
    {
        var store = NewStore(persist: true, out var dir);
        try
        {
            var job = Paused(store, DateTimeOffset.UtcNow);

            Assert.True(store.TryTransition(job.Id, [JobStatus.Paused], j => j.Fail("x", DateTimeOffset.UtcNow)));

            Assert.Equal(JobStatus.Failed, job.Status);
            var reloaded = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);
            Assert.True(reloaded.TryGet(job.Id, out var r));
            Assert.Equal(JobStatus.Failed, r.Status);
            Assert.Equal("x", r.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryTransition_refuses_and_leaves_the_job_alone_when_the_status_has_moved()
    {
        var store = NewStore(persist: false, out _);
        var job = store.Create("python", "openai", @"C:\ws", "t");
        store.Update(job.Id, j => { j.Status = JobStatus.Failed; j.Error = "resumed as other"; });
        var called = false;

        Assert.False(store.TryTransition(job.Id, [JobStatus.Paused], _ => called = true));

        Assert.False(called);
        Assert.Equal("resumed as other", job.Error);
    }

    [Fact]
    public void TryTransition_on_an_unknown_job_returns_false_without_calling_the_mutation()
    {
        var store = NewStore(persist: false, out _);
        var called = false;

        Assert.False(store.TryTransition("missing", [JobStatus.Paused], _ => called = true));
        Assert.False(called);
    }

    // --- races the guard closes ------------------------------------------- //

    [Fact]
    public void Two_resumes_that_both_passed_the_guard_create_only_one_replacement()
    {
        // Both resume_job calls read Paused before either claimed the job: the second claim must fail.
        var (store, tools) = NewTools();
        var old = Paused(store, DateTimeOffset.UtcNow);

        var fresh = tools.PrepareResume(old);
        var ex = Assert.Throws<McpException>(() => tools.PrepareResume(old));

        Assert.Equal($"job {old.Id} is Failed; only Paused jobs can be resumed", ex.Message);
        Assert.Equal($"resumed as {fresh.Id}", old.Error);
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void Concurrent_resumes_of_one_paused_job_create_exactly_one_replacement()
    {
        var (store, tools) = NewTools();
        var old = Paused(store, DateTimeOffset.UtcNow);
        const int callers = 8;
        using var start = new Barrier(callers);
        var winners = 0;

        var threads = Enumerable.Range(0, callers).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            try { tools.PrepareResume(old); Interlocked.Increment(ref winners); }
            catch (McpException) { /* lost the claim */ }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, winners);
        Assert.Equal(2, store.All().Count);
    }

    // --- helpers ---------------------------------------------------------- //

    private static JobState Paused(JobStateStore store, DateTimeOffset pausedUtc)
    {
        var job = store.Create("python", "openai", @"C:\ws", "t");
        store.Update(job.Id, j => { j.Status = JobStatus.Paused; j.PausedUtc = pausedUtc; });
        return job;
    }

    private static JobStateStore NewStore(bool persist, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "qavren-lifecycle-" + Guid.NewGuid().ToString("N"));
        return new JobStateStore(persist, dir, NullLogger<JobStateStore>.Instance);
    }

    private static (JobStateStore store, SwarmTools tools) NewTools()
    {
        var store = NewStore(persist: false, out _);
        var docker = new DockerLifecycleManager(docker: null!, new SwarmConfig(), NullLogger<DockerLifecycleManager>.Instance);
        var tools = new SwarmTools(store, docker, new SwarmConfig(), NullLogger<SwarmTools>.Instance);
        return (store, tools);
    }
}
