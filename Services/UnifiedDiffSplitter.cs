using System.Text;

namespace QavrenSwarm.Services;

/// <summary>One file section of a unified <c>git diff</c>: the raw header block
/// (<c>diff --git …</c> through the <c>+++ </c> line) plus its ordered <c>@@</c> hunks.
/// All strings keep their original bytes — CR included — so a reconstructed sub-patch
/// applies to a CRLF host file exactly as the whole patch would.</summary>
public sealed class DiffFile
{
    public required string Path { get; init; }
    public required string Header { get; init; }
    public required IReadOnlyList<string> Hunks { get; init; }
}

/// <summary>Pure (no IO, no git) splitter that decomposes a unified diff into per-file,
/// per-hunk pieces and recomposes an arbitrary subset of hunks back into a valid patch.
/// Used by <c>apply_diff</c> to retry a rejected multi-hunk diff hunk-by-hunk and to export
/// the rejected hunks for manual application — neither of which writes any host file itself.</summary>
public static class UnifiedDiffSplitter
{
    /// <summary>Split <paramref name="diff"/> into its file sections. A malformed or empty
    /// diff yields an empty list (callers fall back to whole-patch handling).</summary>
    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(diff))
            return files;

        var header = new StringBuilder();
        var hunks = new List<string>();
        StringBuilder? hunk = null;
        string? plusPath = null;
        string? minusPath = null;
        var inFile = false;

        void FlushHunk()
        {
            if (hunk is not null) { hunks.Add(hunk.ToString()); hunk = null; }
        }

        void FlushFile()
        {
            FlushHunk();
            if (inFile && hunks.Count > 0)
            {
                // Prefer the new-side path; fall back to the old side for deletions (+++ /dev/null).
                var path = plusPath is null or "/dev/null" ? minusPath ?? "" : plusPath;
                files.Add(new DiffFile { Path = path, Header = header.ToString(), Hunks = hunks });
            }
            header = new StringBuilder();
            hunks = new List<string>();
            plusPath = minusPath = null;
            inFile = false;
        }

        foreach (var line in SplitKeepEol(diff))
        {
            var content = line.TrimEnd('\n').TrimEnd('\r');

            if (content.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                inFile = true;
                header.Append(line);
            }
            else if (inFile && content.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                hunk = new StringBuilder();
                hunk.Append(line);
            }
            else if (hunk is not null)
            {
                // Body line (context/+/-/"\ No newline…") belongs to the current hunk.
                hunk.Append(line);
            }
            else if (inFile)
            {
                header.Append(line);
                if (content.StartsWith("+++ ", StringComparison.Ordinal))
                    plusPath = StripSide(content[4..]);
                else if (content.StartsWith("--- ", StringComparison.Ordinal))
                    minusPath = StripSide(content[4..]);
            }
            // Lines before the first "diff --git" (preamble) are ignored.
        }
        FlushFile();
        return files;
    }

    /// <summary>Recompose a patch from only the hunks for which <paramref name="includeHunk"/>
    /// returns true. Files with no included hunk are dropped entirely (header omitted), so the
    /// result is always a clean, appliable — or empty — patch.</summary>
    public static string Recompose(IEnumerable<DiffFile> files, Func<DiffFile, int, bool> includeHunk)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var any = false;
            for (var i = 0; i < f.Hunks.Count; i++)
            {
                if (!includeHunk(f, i)) continue;
                if (!any) { sb.Append(f.Header); any = true; }
                sb.Append(f.Hunks[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>A standalone single-hunk patch: this file's header plus exactly one hunk.</summary>
    public static string SingleHunkPatch(DiffFile file, int hunkIndex) =>
        file.Header + file.Hunks[hunkIndex];

    /// <summary>Total hunk count across all files.</summary>
    public static int CountHunks(IEnumerable<DiffFile> files) => files.Sum(f => f.Hunks.Count);

    // "b/path" / "a/path" → "path"; "/dev/null" passes through unchanged.
    private static string StripSide(string s)
    {
        s = s.Trim();
        if (s is "/dev/null") return s;
        if (s.StartsWith("a/", StringComparison.Ordinal) || s.StartsWith("b/", StringComparison.Ordinal))
            return s[2..];
        return s;
    }

    // Split preserving each line's terminator (\n or \r\n); a final unterminated line is kept.
    private static IEnumerable<string> SplitKeepEol(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                yield return s[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < s.Length)
            yield return s[start..];
    }
}
