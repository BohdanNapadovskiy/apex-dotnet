using System.Text;
using Apex.PdfEdit.Core.Model;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Word-level diff between a source MCID's per-word geometry (from
/// <c>geometry.json.pageMcidWords[page][mcid]</c>) and a new content string,
/// producing a sequence of <see cref="WordDiffSegment"/>s the writer uses to decide
/// per-word what to redact and what to stamp.
///
/// Rationale: the customer's edit intent is "correct misspellings within a
/// line, not extend beyond the boundary box". Whole-MCID redact-and-repaint
/// wipes 50+ unchanged words to replace 1 misspelled one — the surrounding
/// scanned raster is the ground truth and shouldn't be regenerated from our
/// fonts. Word-level diff lets us keep the source raster intact wherever the
/// new content matches the old, and only redact + stamp the changed spans.
///
/// Fragment reunion: ABBYY / other OCR engines sometimes split a word at
/// odd character boundaries (e.g. "appointm" + "ent") even though the raster
/// shows one contiguous word. A naive word-by-word LCS treats these as a
/// substitution against the correctly-spelled new content — causing a
/// false-positive redact. Post-LCS, any Replace whose source-side concat
/// (no spaces, case-folded) equals the new-side concat gets promoted to a
/// Keep spanning the fragments' combined bbox.
///
/// Punctuation-only tail changes ("e:" vs "e.") still fall out as a Replace —
/// deliberate, since the visual glyph really differs.
/// </summary>
public static class WordLevelDiff
{
    /// <summary>Diff <paramref name="sourceGlyphs"/> against <paramref name="newContent"/>.</summary>
    public static WordDiffResult Diff(IReadOnlyList<Glyph> sourceGlyphs, string? newContent)
    {
        ArgumentNullException.ThrowIfNull(sourceGlyphs);
        var source = new List<WordBox>(sourceGlyphs.Count);
        foreach (var g in sourceGlyphs)
        {
            if (g?.Text is null || g.Text.Length == 0) continue;
            source.Add(WordBox.From(g));
        }
        var newWords = Tokenize(newContent);

        var segments = LcsSegments(source, newWords);
        segments = ReuniteFragments(segments);

        int changedNewWords = 0;
        int totalNewWords = Math.Max(1, newWords.Count);
        foreach (var s in segments)
        {
            if (s is WordDiffSegment.Replace r)
            {
                changedNewWords += r.NewRange.Count;
            }
        }
        float ratio = Math.Min(1f, (float)changedNewWords / totalNewWords);
        return new WordDiffResult(segments, ratio);
    }

    /// <summary>Split on any whitespace, dropping empties.</summary>
    private static List<string> Tokenize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return new List<string>();
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new List<string>(parts);
    }

    /// <summary>Classic LCS + backtrack.</summary>
    private static List<WordDiffSegment> LcsSegments(List<WordBox> source, List<string> newWords)
    {
        int n = source.Count;
        int m = newWords.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                if (string.Equals(source[i - 1].Text, newWords[j - 1], StringComparison.Ordinal))
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        var matches = new List<(int SrcIdx, int NewIdx)>();
        {
            int i = n, j = m;
            while (i > 0 && j > 0)
            {
                if (string.Equals(source[i - 1].Text, newWords[j - 1], StringComparison.Ordinal))
                {
                    matches.Add((i - 1, j - 1));
                    i--;
                    j--;
                }
                else if (dp[i - 1, j] >= dp[i, j - 1])
                {
                    i--;
                }
                else
                {
                    j--;
                }
            }
        }
        matches.Reverse();

        var segments = new List<WordDiffSegment>();
        int srcCursor = 0;
        int newCursor = 0;
        foreach (var (srcIdx, newIdx) in matches)
        {
            if (srcCursor < srcIdx || newCursor < newIdx)
            {
                var srcRange = source.GetRange(srcCursor, srcIdx - srcCursor);
                var newRange = newWords.GetRange(newCursor, newIdx - newCursor);
                if (srcRange.Count > 0 || newRange.Count > 0)
                {
                    segments.Add(new WordDiffSegment.Replace(srcRange, newRange));
                }
            }
            segments.Add(new WordDiffSegment.Keep(source[srcIdx]));
            srcCursor = srcIdx + 1;
            newCursor = newIdx + 1;
        }
        if (srcCursor < n || newCursor < m)
        {
            var srcRange = source.GetRange(srcCursor, n - srcCursor);
            var newRange = newWords.GetRange(newCursor, m - newCursor);
            if (srcRange.Count > 0 || newRange.Count > 0)
            {
                segments.Add(new WordDiffSegment.Replace(srcRange, newRange));
            }
        }
        return segments;
    }

    /// <summary>
    /// Post-process the LCS output: any Replace whose source-side words concatenate
    /// (no whitespace) to the same string as the new-side words concatenate is really
    /// an OCR fragmentation. Convert to a Keep whose bbox is the union of the fragment
    /// bboxes. Case-folded compare.
    /// </summary>
    private static List<WordDiffSegment> ReuniteFragments(List<WordDiffSegment> input)
    {
        var output = new List<WordDiffSegment>(input.Count);
        foreach (var s in input)
        {
            if (s is WordDiffSegment.Replace r
                && r.SourceRange.Count > 0
                && r.NewRange.Count > 0)
            {
                var srcConcat = ConcatFold(r.SourceRange.Select(b => b.Text));
                var newConcat = ConcatFold(r.NewRange);
                if (string.Equals(srcConcat, newConcat, StringComparison.Ordinal))
                {
                    var union = r.SourceRange[0];
                    for (int i = 1; i < r.SourceRange.Count; i++)
                    {
                        union = union.Union(r.SourceRange[i]);
                    }
                    output.Add(new WordDiffSegment.Keep(union));
                    continue;
                }
            }
            output.Add(s);
        }
        return output;
    }

    private static string ConcatFold(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p);
        return sb.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// A source word plus its bounding box (PDF user-space, Y-up, bottom-left origin
/// as reported in geometry.json). Wraps <see cref="Glyph"/> into a value type
/// the writer can index without pulling the Jackson/System.Text.Json-annotated
/// class around.
/// </summary>
public sealed record WordBox(string Text, double X, double Y, double Width, double Height)
{
    public static WordBox From(Glyph g)
        => new(g.Text ?? string.Empty, g.X, g.Y, g.Width, g.Height);

    /// <summary>Union of two adjacent boxes' extents.</summary>
    public WordBox Union(WordBox other)
    {
        double minX = Math.Min(X, other.X);
        double minY = Math.Min(Y, other.Y);
        double maxX = Math.Max(X + Width, other.X + other.Width);
        double maxY = Math.Max(Y + Height, other.Y + other.Height);
        return new WordBox(Text + other.Text, minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>
/// One aligned span between source and new content.
/// <list type="bullet">
///   <item><see cref="Keep"/> — a single source word survives unchanged.</item>
///   <item><see cref="Replace"/> — the source range at <see cref="Replace.SourceRange"/>
///         gets redacted and stamped with <see cref="Replace.NewRange"/>. Either side
///         may be empty.</item>
/// </list>
/// </summary>
public abstract record WordDiffSegment
{
    public sealed record Keep(WordBox Source) : WordDiffSegment;
    public sealed record Replace(IReadOnlyList<WordBox> SourceRange, IReadOnlyList<string> NewRange) : WordDiffSegment;
}

/// <summary>Diff result: the segment sequence plus the change ratio (changedWords / totalNewWords).</summary>
public sealed record WordDiffResult(IReadOnlyList<WordDiffSegment> Segments, float ChangeRatio);
