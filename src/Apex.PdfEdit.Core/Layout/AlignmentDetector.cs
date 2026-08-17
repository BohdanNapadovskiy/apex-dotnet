using Apex.PdfEdit.Core.Model;

namespace Apex.PdfEdit.Core.Layout;

/// <summary>
/// Classifies each content-bearing tree node as LEFT / CENTER / RIGHT / JUSTIFIED
/// from its bbox X-coordinates. Uses per-page median left/right edges as the
/// reference, so the detector adapts to different page sizes and column widths
/// without a hard-coded margin.
///
/// Reference edges are derived by <b>mode</b> (most common bucketed value at 1-point
/// granularity), not by statistical median: in real pages many paragraphs share a common
/// left margin, so the mode correctly recovers that margin even when other paragraphs sit
/// at wholly different X-coordinates. Median gets skewed by mixed alignments.
///
/// Heuristic (per page, points, default tolerance 3):
/// <list type="bullet">
///   <item><c>leftAligned</c> = |node.X - modeLeft| ≤ tolerance</item>
///   <item><c>rightAligned</c> = |(node.X + node.Width) - modeRight| ≤ tolerance</item>
///   <item><c>centerAligned</c> = |node.centerX - (modeLeft + modeRight) / 2| ≤ tolerance</item>
/// </list>
/// Classification priority (first match wins):
/// <list type="number">
///   <item><c>JUSTIFIED</c> — left AND right aligned AND width covers most of the median span (&gt;70 %).</item>
///   <item><c>CENTER</c> — centerAligned AND not (leftAligned AND rightAligned).</item>
///   <item><c>RIGHT</c> — rightAligned AND not leftAligned.</item>
///   <item><c>LEFT</c> — leftAligned.</item>
///   <item><c>UNKNOWN</c> — none of the above.</item>
/// </list>
/// </summary>
public sealed class AlignmentDetector
{
    private readonly double _tolerance;

    public AlignmentDetector() : this(3.0) { }

    public AlignmentDetector(double tolerance)
    {
        _tolerance = tolerance;
    }

    public Dictionary<string, Alignment> Detect(DocumentJson document)
    {
        var byPage = document.Tree
            .Where(n => n.Mcid >= 0)
            .Where(n => !n.IsArtifact)
            .Where(n => !string.IsNullOrEmpty(n.Content))
            .Where(n => n.Width > 0)
            .Where(n => n.Id is not null)
            .GroupBy(n => n.Page)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<string, Alignment>();
        foreach (var pageNodes in byPage.Values)
        {
            ClassifyPage(pageNodes, result);
        }
        return result;
    }

    private void ClassifyPage(List<TreeNode> pageNodes, Dictionary<string, Alignment> result)
    {
        if (pageNodes.Count == 0) return;
        var (modeLeft, modeRight) = PageEdges(pageNodes);
        foreach (var n in pageNodes)
        {
            if (n.Id is null) continue;
            result[n.Id] = Classify(n, modeLeft, modeRight);
        }
    }

    /// <summary>
    /// Classify a single <paramref name="donor"/> using only nodes in the same visual column (X-overlap
    /// with the donor's bbox). The per-page <see cref="Detect"/> path lumps every column together,
    /// which misclassifies body paragraphs as <c>RIGHT</c> in multi-column layouts — TOC entries
    /// dominate modeLeft while the body column shares a distinct right margin, so the
    /// body paragraphs match only modeRight. Column-scoped modes fix that.
    /// </summary>
    public Alignment ClassifyInColumn(TreeNode? donor, IReadOnlyList<TreeNode> pageNodes)
    {
        if (donor is null || donor.Width <= 0) return Alignment.Unknown;
        double dLeft = donor.X;
        double dRight = donor.X + donor.Width;
        var sameColumn = pageNodes
            .Where(n => n.Width > 0)
            .Where(n => Math.Max(n.X, dLeft) < Math.Min(n.X + n.Width, dRight))
            .ToList();
        if (sameColumn.Count == 0) return Alignment.Left;
        var (modeLeft, modeRight) = PageEdges(sameColumn);
        return Classify(donor, modeLeft, modeRight);
    }

    private Alignment Classify(TreeNode node, double modeLeft, double modeRight)
    {
        double left = node.X;
        double right = node.X + node.Width;
        double center = (left + right) / 2.0;
        double pageCenter = (modeLeft + modeRight) / 2.0;
        double span = modeRight - modeLeft;

        bool leftAligned = Math.Abs(left - modeLeft) <= _tolerance;
        bool rightAligned = Math.Abs(right - modeRight) <= _tolerance;
        bool centerAligned = Math.Abs(center - pageCenter) <= _tolerance;
        bool coversMost = span > 0 && (node.Width / span) >= 0.7;

        if (leftAligned && rightAligned && coversMost) return Alignment.Justified;
        if (centerAligned && !(leftAligned && rightAligned)) return Alignment.Center;
        if (rightAligned && !leftAligned) return Alignment.Right;
        if (leftAligned) return Alignment.Left;
        return Alignment.Unknown;
    }

    /// <summary>
    /// Recover the "common left margin" and "common right margin" for a page by taking the
    /// mode of left / right edges bucketed at 1-point granularity. Falls back to min/max
    /// if the histograms are flat (no shared value).
    /// </summary>
    public static (double Left, double Right) PageEdges(IReadOnlyList<TreeNode> pageNodes)
    {
        var valid = pageNodes.Where(n => n.Width > 0).ToList();
        if (valid.Count == 0) return (0.0, 0.0);

        double modeLeft = Mode(valid.Select(n => n.X));
        double modeRight = Mode(valid.Select(n => n.X + n.Width));
        return (modeLeft, modeRight);
    }

    /// <summary>Mode of a double sequence bucketed at 1-point granularity. Ties broken by first-seen.</summary>
    private static double Mode(IEnumerable<double> values)
    {
        var counts = new Dictionary<int, int>();
        var sumInBucket = new Dictionary<int, double>();
        foreach (var v in values)
        {
            int bucket = (int)Math.Round(v);
            counts.TryGetValue(bucket, out var c);
            counts[bucket] = c + 1;
            sumInBucket.TryGetValue(bucket, out var s);
            sumInBucket[bucket] = s + v;
        }
        if (counts.Count == 0) throw new InvalidOperationException("Mode called with no values");

        int bestBucket = 0;
        int bestCount = -1;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                bestBucket = kv.Key;
            }
        }
        // Return the average of all raw values that fell in the winning bucket — more precise
        // than the bucket integer when values cluster near a half-integer boundary.
        return sumInBucket[bestBucket] / counts[bestBucket];
    }
}
