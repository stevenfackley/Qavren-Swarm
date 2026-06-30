using System.Diagnostics;
using System.Text;
using QavrenSwarm.Tools;
using Xunit;

namespace QavrenSwarm.Tests;

// Real-git integration for the hunk-by-hunk apply recovery. These prove the SECURITY INVARIANT that
// `git apply` stays the ONLY thing that writes host files: the per-hunk retry just hands git smaller,
// individually-checked patches, and a hunk git refuses (e.g. one targeting .git/) is exported as data,
// never written. Requires `git` on PATH (present on CI runners and dev boxes).
public class ApplyDiffTests : IDisposable
{
    private readonly string _repo;

    public ApplyDiffTests()
    {
        // Keep the throwaway repo on the same (NTFS, owned) volume as the test binaries so git does
        // not refuse it with "dubious ownership" — Path.GetTempPath() can resolve to a volume that
        // doesn't record ownership, which would break every git call.
        _repo = Path.Combine(AppContext.BaseDirectory, "qavren-applytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        Git("config", "user.email", "t@t.local");
        Git("config", "user.name", "t");
        Git("config", "core.autocrlf", "false");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Whole_patch_applies_cleanly_when_workspace_matches()
    {
        Write("a.txt", "alpha\n");
        Git("add", "-A"); Git("commit", "-q", "-m", "base");
        var diff = StagedDiffFor(("a.txt", "ALPHA\n"));

        var outcome = await SwarmTools.ApplyDiffCore(_repo, diff, allowPartial: true);

        Assert.True(outcome.Applied);
        Assert.False(outcome.Partial);
        Assert.Equal(1, outcome.AppliedHunks);
        Assert.Equal(0, outcome.FailedHunks);
        Assert.Equal("ALPHA\n", Read("a.txt"));
    }

    [Fact]
    public async Task Rejected_multi_hunk_diff_applies_good_hunks_and_exports_the_rest()
    {
        Write("a.txt", "alpha\n");
        Write("b.txt", "beta\n");
        Git("add", "-A"); Git("commit", "-q", "-m", "base");
        var diff = StagedDiffFor(("a.txt", "ALPHA\n"), ("b.txt", "BETA\n"));

        // Diverge b.txt so its hunk no longer applies; a.txt still matches. The WHOLE patch now fails.
        Write("b.txt", "PERTURBED\n");

        var outcome = await SwarmTools.ApplyDiffCore(_repo, diff, allowPartial: true);

        Assert.True(outcome.Applied);
        Assert.True(outcome.Partial);
        Assert.Equal(1, outcome.AppliedHunks);
        Assert.Equal(1, outcome.FailedHunks);
        Assert.Equal("ALPHA\n", Read("a.txt"));        // good hunk landed
        Assert.Equal("PERTURBED\n", Read("b.txt"));     // bad hunk did NOT touch the file
        Assert.Contains("b.txt", outcome.RejectedDiff); // exported for manual application
        Assert.DoesNotContain("a.txt", outcome.RejectedDiff);
        Assert.Contains(outcome.Hunks, h => h.Path == "b.txt" && !h.Applied);
    }

    [Fact]
    public async Task A_malicious_git_targeting_hunk_is_rejected_not_written_even_in_partial_mode()
    {
        Write("foo.txt", "hello\n");
        Git("add", "-A"); Git("commit", "-q", "-m", "base");

        // One legitimate hunk + one hunk that creates a file under .git/ (a classic post-checkout RCE).
        var good = StagedDiffFor(("foo.txt", "HELLO\n"));
        var evil =
            "diff --git a/.git/hooks/post-commit b/.git/hooks/post-commit\n" +
            "new file mode 100755\n" +
            "--- /dev/null\n" +
            "+++ b/.git/hooks/post-commit\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+#!/bin/sh\n" +
            "+echo pwned\n";

        var outcome = await SwarmTools.ApplyDiffCore(_repo, good + evil, allowPartial: true);

        // git apply (the sole writer) refuses the .git/ path; only the legit hunk lands.
        Assert.True(outcome.Partial);
        Assert.Equal(1, outcome.AppliedHunks);
        Assert.Equal(1, outcome.FailedHunks);
        Assert.Equal("HELLO\n", Read("foo.txt"));
        Assert.False(File.Exists(Path.Combine(_repo, ".git", "hooks", "post-commit"))); // never written
        Assert.Contains("post-commit", outcome.RejectedDiff);
    }

    [Fact]
    public async Task Strict_mode_applies_nothing_when_the_whole_patch_is_rejected()
    {
        Write("a.txt", "alpha\n");
        Write("b.txt", "beta\n");
        Git("add", "-A"); Git("commit", "-q", "-m", "base");
        var diff = StagedDiffFor(("a.txt", "ALPHA\n"), ("b.txt", "BETA\n"));
        Write("b.txt", "PERTURBED\n");

        var outcome = await SwarmTools.ApplyDiffCore(_repo, diff, allowPartial: false);

        Assert.False(outcome.Applied);
        Assert.Equal(0, outcome.AppliedHunks);
        Assert.Equal("alpha\n", Read("a.txt"));     // all-or-nothing: a.txt untouched too
        Assert.Equal("PERTURBED\n", Read("b.txt"));
        Assert.NotNull(outcome.Note);
    }

    // --- helpers ---------------------------------------------------------- //

    // Stage the given file edits, capture `git diff --cached`, then hard-reset back to baseline so the
    // returned diff is a clean patch against the committed tree (not yet applied).
    private string StagedDiffFor(params (string path, string content)[] edits)
    {
        foreach (var (path, content) in edits) Write(path, content);
        Git("add", "-A");
        var diff = GitCapture("diff", "--cached");
        Git("reset", "-q", "--hard", "HEAD");
        return diff;
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_repo, rel));

    private void Git(params string[] args)
    {
        var (code, _, err) = RunGit(args);
        if (code != 0)
            throw new Xunit.Sdk.XunitException($"git {string.Join(' ', args)} exited {code}: {err.Trim()}");
    }

    private string GitCapture(params string[] args)
    {
        var (code, stdout, err) = RunGit(args);
        if (code != 0)
            throw new Xunit.Sdk.XunitException($"git {string.Join(' ', args)} exited {code}: {err.Trim()}");
        return stdout;
    }

    private (int code, string stdout, string stderr) RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(_repo);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close(); // never let git block on inherited stdin
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new Xunit.Sdk.XunitException($"git {string.Join(' ', args)} hung > 30s");
        }
        stdout.Wait(); stderr.Wait();
        return (proc.ExitCode, stdout.Result, stderr.Result);
    }
}
