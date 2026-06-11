using Microsoft.Extensions.Logging.Abstractions;
using QavrenSwarm.Services;
using Xunit;

namespace QavrenSwarm.Tests;

public class JobPersistenceTests
{
    [Fact]
    public void Jobs_survive_a_restart_and_inflight_jobs_are_marked_interrupted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qavren-jobtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            // First "process": one finished job, one still running.
            var s1 = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);
            var done = s1.Create("python", "openai", @"C:\x", "done");
            s1.Update(done.Id, j =>
            {
                j.Status = JobStatus.Completed;
                j.Diff = "DIFF-CONTENT";
                j.FinishedUtc = System.DateTimeOffset.UtcNow;
            });
            var running = s1.Create("python", "openai", @"C:\x", "running");
            s1.Update(running.Id, j => j.Status = JobStatus.Running);

            // "Restart": a fresh store over the same directory.
            var s2 = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);

            Assert.True(s2.TryGet(done.Id, out var d));
            Assert.Equal(JobStatus.Completed, d.Status);
            Assert.Equal("DIFF-CONTENT", d.Diff);          // the diff survives → still appliable

            Assert.True(s2.TryGet(running.Id, out var r));
            Assert.Equal(JobStatus.Failed, r.Status);       // in-flight job is interrupted, not stuck
            Assert.Equal("interrupted by server restart", r.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Persistence_disabled_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qavren-jobtest-" + Guid.NewGuid().ToString("N"));
        var s = new JobStateStore(persist: false, dir, NullLogger<JobStateStore>.Instance);
        s.Create("python", "openai", @"C:\x", "t");
        Assert.False(Directory.Exists(dir));
    }
}
