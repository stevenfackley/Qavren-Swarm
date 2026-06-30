using QavrenSwarm.Services;
using Xunit;

namespace QavrenSwarm.Tests;

// The splitter is the pure, load-bearing core of hunk-by-hunk apply recovery and the rejected-patch
// export: it must decompose a unified diff and recompose any subset of hunks WITHOUT mangling bytes
// (CRs included), or a reconstructed sub-patch would no longer apply to a CRLF host file.
public class UnifiedDiffSplitterTests
{
    private const string TwoFiles =
        "diff --git a/a.txt b/a.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/a.txt\n" +
        "+++ b/a.txt\n" +
        "@@ -1,3 +1,3 @@\n" +
        " l1\n-l2\n+L2\n l3\n" +
        "@@ -7,3 +7,3 @@\n" +
        " l7\n-l8\n+L8\n l9\n" +
        "diff --git a/b.txt b/b.txt\n" +
        "index 3333333..4444444 100644\n" +
        "--- a/b.txt\n" +
        "+++ b/b.txt\n" +
        "@@ -1 +1 @@\n" +
        "-beta\n+BETA\n";

    [Fact]
    public void Parses_files_paths_and_hunk_counts()
    {
        var files = UnifiedDiffSplitter.Parse(TwoFiles);

        Assert.Equal(2, files.Count);
        Assert.Equal("a.txt", files[0].Path);
        Assert.Equal(2, files[0].Hunks.Count);
        Assert.Equal("b.txt", files[1].Path);
        Assert.Single(files[1].Hunks);
        Assert.Equal(3, UnifiedDiffSplitter.CountHunks(files));
    }

    [Fact]
    public void Header_excludes_hunks_and_single_hunk_patch_is_header_plus_one_hunk()
    {
        var file = UnifiedDiffSplitter.Parse(TwoFiles)[0];

        Assert.StartsWith("diff --git a/a.txt b/a.txt\n", file.Header);
        Assert.EndsWith("+++ b/a.txt\n", file.Header);
        Assert.DoesNotContain("@@", file.Header);

        var patch = UnifiedDiffSplitter.SingleHunkPatch(file, 1);
        Assert.Equal(file.Header + file.Hunks[1], patch);
        Assert.Contains("+L8", patch);
        Assert.DoesNotContain("+L2", patch); // only the requested hunk's body
    }

    [Fact]
    public void Recompose_of_all_hunks_round_trips_the_original_bytes()
    {
        var files = UnifiedDiffSplitter.Parse(TwoFiles);
        var rebuilt = UnifiedDiffSplitter.Recompose(files, (_, _) => true);
        Assert.Equal(TwoFiles, rebuilt);
    }

    [Fact]
    public void Recompose_of_a_subset_keeps_only_selected_hunks_and_drops_empty_files()
    {
        var files = UnifiedDiffSplitter.Parse(TwoFiles);

        // Keep only the first hunk of a.txt; b.txt has no selected hunk → dropped entirely.
        var subset = UnifiedDiffSplitter.Recompose(files, (f, i) => f.Path == "a.txt" && i == 0);

        Assert.Contains("+L2", subset);
        Assert.DoesNotContain("+L8", subset);
        Assert.DoesNotContain("b.txt", subset); // header omitted when no hunk is included
        Assert.Equal("", UnifiedDiffSplitter.Recompose(files, (_, _) => false));
    }

    [Fact]
    public void Preserves_CR_bytes_in_hunk_bodies()
    {
        var crlf =
            "diff --git a/x b/x\r\n" +
            "--- a/x\r\n+++ b/x\r\n" +
            "@@ -1 +1 @@\r\n-a\r\n+b\r\n";

        var files = UnifiedDiffSplitter.Parse(crlf);

        Assert.Single(files);
        Assert.Contains("\r\n", files[0].Hunks[0]); // CRLF survives → git apply still matches the host file
        Assert.Equal(crlf, UnifiedDiffSplitter.Recompose(files, (_, _) => true));
    }

    [Fact]
    public void New_file_path_uses_b_side_deletion_uses_a_side()
    {
        var created =
            "diff --git a/new.txt b/new.txt\nnew file mode 100644\nindex 0000000..aaaaaaa\n" +
            "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+hi\n";
        var deleted =
            "diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\nindex aaaaaaa..0000000\n" +
            "--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-bye\n";

        Assert.Equal("new.txt", UnifiedDiffSplitter.Parse(created)[0].Path);
        Assert.Equal("gone.txt", UnifiedDiffSplitter.Parse(deleted)[0].Path);
    }

    [Fact]
    public void Empty_or_garbage_input_yields_no_files()
    {
        Assert.Empty(UnifiedDiffSplitter.Parse(""));
        Assert.Empty(UnifiedDiffSplitter.Parse("not a diff at all\njust noise\n"));
    }
}
