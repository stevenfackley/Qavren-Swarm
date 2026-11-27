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
    public void A_write_that_fails_halfway_leaves_the_previous_job_file_whole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qavren-jobtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var failHalfway = false;
            // The store's file write; when told to, it writes half the text and then fails, as a crash
            // or a full disk would.
            void Write(string path, string text)
            {
                if (!failHalfway)
                {
                    File.WriteAllText(path, text);
                    return;
                }
                File.WriteAllText(path, text[..(text.Length / 2)]);
                throw new IOException("disk full");
            }
            var s1 = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance, Write);
            var job = s1.Create("python", "openai", @"C:\x", "t");
            s1.Update(job.Id, j => { j.Status = JobStatus.Completed; j.Diff = "DIFF-CONTENT"; });

            failHalfway = true;
            s1.Update(job.Id, j => j.Diff = "NEWER-DIFF");   // the failure is logged and swallowed
            // What a process killed between the write and the move leaves behind.
            File.WriteAllText(Path.Combine(dir, job.Id + "." + Guid.NewGuid().ToString("N") + ".tmp"), "{\"Id\":");

            var s2 = new JobStateStore(persist: true, dir, NullLogger<JobStateStore>.Instance);
            Assert.True(s2.TryGet(job.Id, out var reloaded));
            Assert.Equal(JobStatus.Completed, reloaded.Status);
            Assert.Equal("DIFF-CONTENT", reloaded.Diff);
            Assert.Equal([job.Id + ".json"], Directory.GetFiles(dir).Select(Path.GetFileName));   // no half-written leftovers
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
