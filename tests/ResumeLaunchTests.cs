using System.Reflection;
using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using QavrenSwarm.Services;
using QavrenSwarm.Tools;
using Xunit;
// Aliases, not the namespace: Docker.DotNet.Models has its own JobStatus and SwarmConfig.
using ContainerWaitResponse = Docker.DotNet.Models.ContainerWaitResponse;
using CreateContainerResponse = Docker.DotNet.Models.CreateContainerResponse;
using ImagesListResponse = Docker.DotNet.Models.ImagesListResponse;

namespace QavrenSwarm.Tests;

// resume_job when the replacement's launch goes wrong. A replacement that fails before its container
// has started hands the old job back as Paused, resumable again with a fresh grace window; one that
// starts, is cancelled or times out leaves the old job superseded, as it always did. Docker is a
// stand-in whose steps a test can hold open or fail, the grace window runs on a clock the test moves,
// and every test awaits the replacement's background run, so no assertion depends on timing.
public class ResumeLaunchTests
{
    // When the old job paused, and where every test's clock starts.
    private static readonly DateTimeOffset PausedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(new SwarmConfig().PauseGraceSeconds);
    private static readonly string HungReason = JobRecovery.ClassifyInterruption(userCancelled: false).error;

    // --- the launch fails before the container runs ------------------------ //

    [Theory]
    [InlineData("image")]    // Docker is down: the first call, the image lookup, throws
    [InlineData("create")]
    [InlineData("start")]
    public async Task Replacement_that_fails_before_its_container_starts_hands_the_old_job_back_as_paused(string failingStep)
    {
        Func<CancellationToken, Task> fail = _ => throw new IOException("docker is down");
        var docker = failingStep switch
        {
            "image" => new FakeDocker { Image = fail },
            "create" => new FakeDocker { Create = fail },
            _ => new FakeDocker { Start = fail },
        };
        var clock = new FakeClock();
        var (store, tools) = NewTools(docker, clock: clock);
        var old = Paused(store);
        clock.Advance(TimeSpan.FromMinutes(10));

        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(run);

        Assert.Equal(JobStatus.Failed, fresh.Status);
        Assert.Equal("IOException: docker is down", fresh.Error);
        Assert.Equal(JobStatus.Paused, old.Status);
        // The record says what happened, and the grace window starts again at the hand-back.
        Assert.Equal($"resume as {fresh.Id} never started (IOException: docker is down)", old.Error);
        Assert.Equal(clock.GetUtcNow(), old.PausedUtc);
        Assert.Equal(1, old.HandBacks);
        Assert.Null(old.FinishedUtc);
        // A container that was created but never started is still removed.
        Assert.Equal(failingStep == "start" ? 1 : 0, docker.Removed);

        using var doc = JsonDocument.Parse(tools.CheckSandboxStatus(old.Id));
        Assert.Equal("Paused", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("resumable").GetBoolean());
        Assert.Equal(old.Error, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Old_job_that_was_handed_back_resumes_once_docker_is_back()
    {
        var docker = new FakeDocker { Create = _ => throw new IOException("docker is down") };
        var (store, tools) = NewTools(docker);
        var old = Paused(store);
        var (first, firstRun) = tools.BeginResume(old.Id);
        await Finished(firstRun);

        docker.Create = _ => Task.CompletedTask;
        var (second, secondRun) = tools.BeginResume(old.Id);
        await Finished(secondRun);

        Assert.Equal(JobStatus.Completed, second.Status);
        AssertSuperseded(old, second);
        Assert.Equal(JobStatus.Failed, first.Status);
        Assert.Equal(3, store.All().Count);
        AssertResumeRefused(tools, old);
        Assert.Equal(3, store.All().Count);
    }

    [Fact]
    public async Task Concurrent_resumes_of_a_handed_back_job_create_exactly_one_replacement()
    {
        var docker = new FakeDocker { Create = _ => throw new IOException("docker is down") };
        var (store, tools) = NewTools(docker);
        var old = Paused(store);
        var (_, run) = tools.BeginResume(old.Id);
        await Finished(run);
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
        Assert.Equal(3, store.All().Count);   // the old job, the replacement that never ran, the new one
    }

    [Fact]
    public async Task Hand_back_is_written_to_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qavren-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);
            var clock = new FakeClock();
            var (_, tools) = NewTools(new FakeDocker { Create = _ => throw new IOException("docker is down") }, store, clock);
            var old = Paused(store);
            clock.Advance(TimeSpan.FromMinutes(10));

            var (fresh, run) = tools.BeginResume(old.Id);
            await Finished(run);

            var reloaded = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);
            Assert.True(reloaded.TryGet(old.Id, out var r));
            Assert.Equal(JobStatus.Paused, r.Status);
            Assert.Equal($"resume as {fresh.Id} never started (IOException: docker is down)", r.Error);
            Assert.Equal(clock.GetUtcNow(), r.PausedUtc);
            // The count of hand-backs survives a restart, so a reaped job's reason still says what happened.
            Assert.Equal(1, r.HandBacks);
            Assert.Null(r.ResumedAs);
            Assert.Null(r.FinishedUtc);
            // Whether a launch is undecided is bookkeeping for this process only.
            Assert.DoesNotContain("ResumeUndecided", File.ReadAllText(Path.Combine(dir, old.Id + ".json")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Cancel_after_the_hand_back_discards_the_old_job_for_good()
    {
        var (store, tools) = NewTools(new FakeDocker { Create = _ => throw new IOException("docker is down") });
        var old = Paused(store);
        var (_, run) = tools.BeginResume(old.Id);
        await Finished(run);

        using var doc = JsonDocument.Parse(tools.CancelJob(old.Id));

        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("paused job discarded", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(JobStatus.Failed, old.Status);
        Assert.Equal("paused job discarded", old.Error);
        Assert.NotNull(old.FinishedUtc);
        AssertResumeRefused(tools, old);
        Assert.Equal(2, store.All().Count);
    }

    // --- the grace window after a hand-back -------------------------------- //

    [Fact]
    public async Task Hand_back_just_before_the_deadline_restarts_the_grace_window()
    {
        var starting = new Held();
        var clock = new FakeClock();
        var (store, tools) = NewTools(new FakeDocker { Create = starting.Step }, clock: clock);
        var old = Paused(store);

        clock.Advance(Grace - TimeSpan.FromSeconds(1));   // one second before the old deadline
        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(starting.Reached);
        clock.Advance(TimeSpan.FromSeconds(2));           // the failing launch outlives that deadline
        starting.Fail(new IOException("docker is down"));
        await Finished(run);
        var handedBackAt = clock.GetUtcNow();

        Assert.Equal("Paused", StatusOf(tools, old));     // not reaped at once
        Assert.Equal(handedBackAt, old.PausedUtc);
        clock.Advance(Grace);                             // a full window later it is still resumable
        Assert.Equal("Paused", StatusOf(tools, old));
        clock.Advance(TimeSpan.FromTicks(1));             // and then it is reaped
        Assert.Equal("Failed", StatusOf(tools, old));
        Assert.Equal($"paused job expired: resume as {fresh.Id} never started (IOException: docker is down); " +
                     "not resumed again within the grace window", old.Error);
        Assert.Equal(clock.GetUtcNow(), old.FinishedUtc);
    }

    [Fact]
    public async Task Status_reads_during_a_hand_back_never_reap_the_job_whose_window_it_restarts()
    {
        // The launch fails after the old deadline while a thread keeps reading the job's status. A read
        // that sees the job half written back (Paused, with the old expired PausedUtc) must not reap it.
        for (var round = 0; round < 2000; round++)
        {
            var starting = new Held();
            var clock = new FakeClock();
            var (store, tools) = NewTools(new FakeDocker { Create = starting.Step }, clock: clock);
            var old = Paused(store);
            clock.Advance(Grace - TimeSpan.FromSeconds(1));
            var (_, run) = tools.BeginResume(old.Id);
            await Finished(starting.Reached);
            clock.Advance(TimeSpan.FromSeconds(2));

            using var polling = new ManualResetEventSlim();
            var stop = false;
            var poller = new Thread(() =>
            {
                polling.Set();
                while (!Volatile.Read(ref stop))
                    tools.CheckSandboxStatus(old.Id);
            });
            poller.Start();
            polling.Wait();
            starting.Fail(new IOException("docker is down"));
            await Finished(run);
            Volatile.Write(ref stop, true);
            poller.Join();

            Assert.Equal(JobStatus.Paused, old.Status);
        }
    }

    [Fact]
    public async Task Each_failed_resume_restarts_the_window_and_only_another_resume_extends_it()
    {
        // Only resume_job hands a job back, so the caller is what bounds how often its window
        // restarts: without another call the job is reaped one window after the last hand-back.
        var clock = new FakeClock();
        var (store, tools) = NewTools(new FakeDocker { Create = _ => throw new IOException("docker is down") }, clock: clock);
        var old = Paused(store);
        JobState? fresh = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            clock.Advance(Grace - TimeSpan.FromSeconds(1));   // each resume just before the deadline
            (fresh, var run) = tools.BeginResume(old.Id);
            await Finished(run);

            Assert.Equal("Paused", StatusOf(tools, old));
            Assert.Equal(clock.GetUtcNow(), old.PausedUtc);
            Assert.Equal(attempt, old.HandBacks);
            Assert.Equal($"resume as {fresh.Id} never started (IOException: docker is down)", old.Error);
        }

        clock.Advance(Grace);
        Assert.Equal("Paused", StatusOf(tools, old));
        clock.Advance(TimeSpan.FromTicks(1));
        AssertResumeRefused(tools, old);
        Assert.Equal($"paused job expired: resume as {fresh!.Id} never started (IOException: docker is down); " +
                     "not resumed again within the grace window", old.Error);
    }

    [Fact]
    public void Paused_job_that_was_never_resumed_is_reaped_one_window_after_it_paused_as_before()
    {
        var clock = new FakeClock();
        var (store, tools) = NewTools(new FakeDocker(), clock: clock);
        var job = Paused(store);

        clock.Advance(Grace);
        Assert.Equal("Paused", StatusOf(tools, job));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("Failed", StatusOf(tools, job));

        Assert.Equal("paused job expired (not resumed within grace window)", job.Error);
        Assert.Equal(PausedAt, job.PausedUtc);
        Assert.Equal(clock.GetUtcNow(), job.FinishedUtc);
        Assert.Equal(0, job.HandBacks);
    }

    // --- the launch succeeds: as before ----------------------------------- //

    [Fact]
    public async Task Replacement_that_starts_keeps_the_old_job_superseded()
    {
        var running = new Held();
        var docker = new FakeDocker { Wait = async ct => { await running.Step(ct); return 0; } };
        var (store, tools) = NewTools(docker);
        var old = Paused(store);

        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(running.Reached);

        Assert.Equal(JobStatus.Running, fresh.Status);
        AssertSuperseded(old, fresh);

        running.Release();
        await Finished(run);

        Assert.Equal(JobStatus.Completed, fresh.Status);
        AssertSuperseded(old, fresh);
        Assert.Equal(2, store.All().Count);
    }

    [Theory]
    [InlineData(false, "agent exited 3")]
    [InlineData(true, "IOException: lost the engine")]
    public async Task Replacement_that_fails_after_its_container_started_keeps_the_old_job_superseded(bool waitThrows, string error)
    {
        var docker = new FakeDocker { Wait = _ => waitThrows ? throw new IOException("lost the engine") : Task.FromResult(3L) };
        var (store, tools) = NewTools(docker);
        var old = Paused(store);

        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(run);

        Assert.Equal(JobStatus.Failed, fresh.Status);
        Assert.Equal(error, fresh.Error);
        AssertSuperseded(old, fresh);
        Assert.Equal(1, docker.Removed);
    }

    // --- a second resume while the replacement is starting: as before ------ //

    [Fact]
    public async Task Second_resume_while_the_replacement_is_starting_is_refused_and_creates_nothing()
    {
        var starting = new Held();
        var (store, tools) = NewTools(new FakeDocker { Create = starting.Step });
        var old = Paused(store);

        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(starting.Reached);

        AssertResumeRefused(tools, old);
        Assert.Equal(2, store.All().Count);

        starting.Release();
        await Finished(run);

        Assert.Equal(JobStatus.Completed, fresh.Status);
        AssertSuperseded(old, fresh);
    }

    // --- a cancel or a timeout while the replacement is starting ----------- //

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_of_the_old_job_while_the_replacement_is_starting_is_told_it_is_being_resumed(bool launchThenFails)
    {
        var starting = new Held();
        var (store, tools) = NewTools(new FakeDocker { Create = starting.Step });
        var old = Paused(store);
        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(starting.Reached);

        // Not "already finished": a failed launch would still bring the job back.
        var ex = Assert.Throws<McpException>(() => tools.CancelJob(old.Id));
        Assert.Equal(BeingResumed(old, fresh), ex.Message);
        AssertSuperseded(old, fresh);   // the refused cancel changed nothing

        if (launchThenFails)
            starting.Fail(new IOException("docker is down"));
        else
            starting.Release();
        await Finished(run);

        // Trying again gets an answer that stays true.
        using var doc = JsonDocument.Parse(tools.CancelJob(old.Id));
        if (launchThenFails)
        {
            Assert.Equal("paused job discarded", doc.RootElement.GetProperty("note").GetString());
            Assert.Equal(JobStatus.Failed, old.Status);
            Assert.Equal("paused job discarded", old.Error);
        }
        else
        {
            Assert.Equal("job already finished", doc.RootElement.GetProperty("note").GetString());
            Assert.Equal(JobStatus.Completed, fresh.Status);
            AssertSuperseded(old, fresh);
        }
        AssertResumeRefused(tools, old);
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public async Task Cancel_of_the_old_job_once_the_replacement_has_started_is_already_finished_for_good()
    {
        var running = new Held();
        var (store, tools) = NewTools(new FakeDocker { Wait = async ct => { await running.Step(ct); return 0; } });
        var old = Paused(store);
        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(running.Reached);   // the container has started; the run waits on it

        using var doc = JsonDocument.Parse(tools.CancelJob(old.Id));

        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("job already finished", doc.RootElement.GetProperty("note").GetString());
        running.Release();
        await Finished(run);
        Assert.Equal(JobStatus.Completed, fresh.Status);
        AssertSuperseded(old, fresh);
        AssertResumeRefused(tools, old);
    }

    [Fact]
    public async Task Cancel_racing_a_resume_is_never_told_finished_and_then_undone()
    {
        // Each launch is held until both calls have answered, then fails and hands the job back.
        // Whichever call gets the job first, an acknowledged cancel must hold, and a refused one must
        // be true.
        for (var round = 0; round < 300; round++)
        {
            var starting = new Held();
            var (store, tools) = NewTools(new FakeDocker { Create = starting.Step });
            var old = Paused(store);
            using var start = new Barrier(2);
            Task? run = null;
            string? note = null;
            McpException? refused = null;
            var resumer = new Thread(() =>
            {
                start.SignalAndWait();
                try { run = tools.BeginResume(old.Id).run; }
                catch (McpException) { /* the cancel discarded it first */ }
            });
            var canceller = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    using var doc = JsonDocument.Parse(tools.CancelJob(old.Id));
                    note = doc.RootElement.GetProperty("note").GetString();
                }
                catch (McpException ex) { refused = ex; }
            });
            resumer.Start();
            canceller.Start();
            resumer.Join();
            canceller.Join();
            if (run is not null)
            {
                await Finished(starting.Reached);
                starting.Fail(new IOException("docker is down"));
                await Finished(run);
            }

            if (refused is null)
            {
                Assert.Equal("paused job discarded", note);
                Assert.Equal(JobStatus.Failed, old.Status);
                Assert.Equal("paused job discarded", old.Error);
            }
            else
            {
                Assert.StartsWith($"job {old.Id} is being resumed as ", refused.Message);
                Assert.Equal(JobStatus.Paused, old.Status);   // the launch failed and handed it back
            }
        }
    }

    [Theory]
    [InlineData(true, "cancelled by user")]             // Docker honours the cancel
    [InlineData(false, "IOException: pipe closed")]     // Docker fails some other way after it
    public async Task Cancel_of_the_replacement_while_it_is_starting_keeps_the_old_job_superseded(bool dockerHonoursTheCancel, string error)
    {
        var starting = new Held();
        var docker = new FakeDocker { Create = dockerHonoursTheCancel ? starting.Step : _ => starting.Step(CancellationToken.None) };
        var (store, tools) = NewTools(docker);
        var old = Paused(store);
        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(starting.Reached);

        using var doc = JsonDocument.Parse(tools.CancelJob(fresh.Id));
        if (!dockerHonoursTheCancel)
            starting.Fail(new IOException("pipe closed"));
        await Finished(run);

        Assert.True(doc.RootElement.GetProperty("cancelling").GetBoolean());
        Assert.Equal(JobStatus.Failed, fresh.Status);
        Assert.Equal(error, fresh.Error);
        AssertSuperseded(old, fresh);
        AssertCancelSaysAlreadyFinished(tools, old);
    }

    [Fact]
    public async Task Cancel_of_the_replacement_before_its_launch_has_a_cancellation_handle_stops_it_before_it_starts()
    {
        var docker = new FakeDocker();
        var (store, tools) = NewTools(docker);
        var old = Paused(store);
        var fresh = tools.PrepareResume(old);   // the replacement exists; its launch has not begun
        Assert.Null(fresh.Cts);

        using var doc = JsonDocument.Parse(tools.CancelJob(fresh.Id));
        Assert.True(doc.RootElement.GetProperty("cancelling").GetBoolean());
        await Finished(tools.LaunchResume(old, fresh));

        Assert.Equal(0, docker.Calls);           // not one Docker call: the run never started
        Assert.Equal(JobStatus.Failed, fresh.Status);
        Assert.Equal("cancelled by user", fresh.Error);
        Assert.NotNull(fresh.FinishedUtc);
        AssertSuperseded(old, fresh);
        AssertCancelSaysAlreadyFinished(tools, old);
        AssertResumeRefused(tools, old);
    }

    [Fact]
    public async Task Timeout_while_the_replacement_is_starting_pauses_the_replacement_and_keeps_the_old_job_superseded()
    {
        var starting = new Held();
        var (store, tools) = NewTools(new FakeDocker { Create = starting.Step });
        var old = Paused(store);
        var (fresh, run) = tools.BeginResume(old.Id);
        await Finished(starting.Reached);

        fresh.Cts!.Cancel();   // the wall-clock cap firing: a cancellation nobody asked for
        await Finished(run);

        Assert.Equal(JobStatus.Paused, fresh.Status);
        Assert.Equal(HungReason, fresh.Error);
        AssertSuperseded(old, fresh);
        Assert.Single(store.All(), j => j.Status == JobStatus.Paused);   // one resumable job, not two
        AssertCancelSaysAlreadyFinished(tools, old);
    }

    // --- helpers ---------------------------------------------------------- //

    private static void AssertSuperseded(JobState old, JobState replacement)
    {
        Assert.Equal(JobStatus.Failed, old.Status);
        Assert.Equal($"resumed as {replacement.Id}", old.Error);
        Assert.NotNull(old.FinishedUtc);
    }

    private static void AssertResumeRefused(SwarmTools tools, JobState job)
    {
        var ex = Assert.Throws<McpException>(() => tools.ResumeJob(job.Id));
        Assert.Equal($"job {job.Id} is Failed; only Paused jobs can be resumed", ex.Message);
    }

    // The resume is over without a hand-back, so the old job is finished and a cancel may say so.
    private static void AssertCancelSaysAlreadyFinished(SwarmTools tools, JobState old)
    {
        using var doc = JsonDocument.Parse(tools.CancelJob(old.Id));
        Assert.Equal("job already finished", doc.RootElement.GetProperty("note").GetString());
    }

    private static string BeingResumed(JobState old, JobState replacement) =>
        $"job {old.Id} is being resumed as {replacement.Id}, whose container has not started yet; " +
        $"cancel_job {replacement.Id} stops it";

    // A watchdog only: a step that is never reached fails the test instead of hanging the suite.
    private static Task Finished(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    private static string? StatusOf(SwarmTools tools, JobState job)
    {
        using var doc = JsonDocument.Parse(tools.CheckSandboxStatus(job.Id));
        return doc.RootElement.GetProperty("status").GetString();
    }

    private static JobState Paused(JobStateStore store)
    {
        var job = store.Create("python", "openai", @"C:\ws", "t");
        store.Update(job.Id, j => { j.Status = JobStatus.Paused; j.Error = HungReason; j.PausedUtc = PausedAt; });
        return job;
    }

    private static (JobStateStore store, SwarmTools tools) NewTools(FakeDocker docker, JobStateStore? store = null, FakeClock? clock = null)
    {
        store ??= new JobStateStore(persist: false, dir: "", NullLogger<JobStateStore>.Instance);
        var manager = new DockerLifecycleManager(docker.Client, new SwarmConfig(), NullLogger<DockerLifecycleManager>.Instance);
        return (store, new SwarmTools(store, manager, new SwarmConfig(), NullLogger<SwarmTools>.Instance, clock ?? new FakeClock()));
    }

    /// <summary>The tools' clock: it stands at <see cref="PausedAt"/> until a test moves it.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = PausedAt;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A container step a test holds open: <see cref="Reached"/> completes when the run gets
    /// there, and the step ends when the test releases it, fails it, or the run's token is cancelled.</summary>
    private sealed class Held
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public async Task Step(CancellationToken ct)
        {
            _reached.SetResult();
            await _release.Task.WaitAsync(ct);
        }

        public void Release() => _release.SetResult();

        public void Fail(Exception ex) => _release.SetException(ex);
    }

    /// <summary>A Docker engine stand-in for the calls a run makes: the agent image exists and every
    /// step succeeds at once, with exit code 0 and no output, unless a test replaces the step.</summary>
    private sealed class FakeDocker
    {
        public Func<CancellationToken, Task> Image = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Create = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Start = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<long>> Wait = _ => Task.FromResult(0L);
        public int Removed;
        public int Calls;   // image lookups, creates, starts and waits

        public IDockerClient Client { get; }

        public FakeDocker()
        {
            var images = Stub.Of<IImageOperations>((name, args) => name switch
            {
                nameof(IImageOperations.ListImagesAsync) => ListImages((CancellationToken)args[^1]!),
                _ => throw new NotSupportedException(name),
            });
            var containers = Stub.Of<IContainerOperations>((name, args) => name switch
            {
                nameof(IContainerOperations.CreateContainerAsync) => CreateContainer((CancellationToken)args[^1]!),
                nameof(IContainerOperations.StartContainerAsync) => StartContainer((CancellationToken)args[^1]!),
                nameof(IContainerOperations.WaitContainerAsync) => WaitContainer((CancellationToken)args[^1]!),
                nameof(IContainerOperations.GetContainerLogsAsync) => Task.FromResult(new MultiplexedStream(new MemoryStream(), multiplexed: true)),
                nameof(IContainerOperations.RemoveContainerAsync) => RemoveContainer(),
                _ => throw new NotSupportedException(name),
            });
            Client = Stub.Of<IDockerClient>((name, _) => name switch
            {
                "get_Images" => images,
                "get_Containers" => containers,
                _ => throw new NotSupportedException(name),
            });
        }

        private async Task<IList<ImagesListResponse>> ListImages(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Image(ct);
            return [new ImagesListResponse()];
        }

        private async Task<CreateContainerResponse> CreateContainer(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Create(ct);
            return new CreateContainerResponse { ID = "c1" };
        }

        private async Task<bool> StartContainer(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Start(ct);
            return true;
        }

        private async Task<ContainerWaitResponse> WaitContainer(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new() { StatusCode = await Wait(ct) };
        }

        private Task RemoveContainer()
        {
            Interlocked.Increment(ref Removed);
            return Task.CompletedTask;
        }
    }

    /// <summary>Implements one Docker.DotNet interface by handing every call, by method name, to a
    /// delegate, so the stand-in spells out only the handful of members a run uses.</summary>
    private class Stub : DispatchProxy
    {
        private Func<string, object?[], object?> _handler = null!;

        public static T Of<T>(Func<string, object?[], object?> handler) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!.Name, args ?? []);
    }
}
