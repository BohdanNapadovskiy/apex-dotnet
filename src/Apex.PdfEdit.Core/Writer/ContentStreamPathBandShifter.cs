using System.Globalization;
using Apex.PdfEdit.Core.Edit;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Push-down pass for <b>untagged</b> vector graphics — specifically <c>re</c>
/// (rectangle) operators that live outside any MCID-bearing <c>BDC/EMC</c> block.
/// Sibling of <see cref="ContentStreamMcidMover"/>: the mover handles tagged MCID blocks;
/// this class handles the leftover decorative artwork (page frames, section borders,
/// dividers) that would otherwise stay put and clip the shifted content.
///
/// <b>Rule per rect vs. band top</b> (<see cref="PathBandOverlay.BandTopY"/>):
/// <list type="bullet">
///   <item><c>y + h ≤ bandTopY</c> (entirely below): translate — <c>y_new = y + dy</c>.</item>
///   <item><c>y ≥ bandTopY</c>     (entirely above): unchanged.</item>
///   <item>otherwise (straddling): grow bottom — <c>y_new = y + dy, h_new = h - dy</c>
///       (recall <c>dy &lt; 0</c> for push-down, so <c>h</c> increases). Keeps the top
///       edge pinned so an enclosing border still contains the pre-band content above
///       plus the shifted content below.</item>
/// </list>
///
/// <b>Skip conditions</b> — a <c>re</c> is passed through unchanged when the processor
/// is inside at least one BDC that carries an inline <c>/MCID</c>. Untagged BMC blocks
/// (<c>/Artifact BMC</c>, plain <c>/Foo BMC</c>) do NOT count as "tagged"; their content
/// is shift-eligible.
///
/// <b>Scope caveat</b> — line/curve paths (<c>m/l/c/v/y</c>) and image draws
/// (<c>Do</c> / inline <c>BI...EI</c>) are NOT handled by this first cut.
/// </summary>
internal sealed class ContentStreamPathBandShifter : PdfCanvasProcessor
{
    private readonly IReadOnlyList<PathBandOverlay> _bands;

    /// <summary>
    /// Marked-content stack — one entry per open BMC/BDC, true iff the entry carries
    /// an inline /MCID. EMC pops the top.
    /// </summary>
    private readonly Stack<bool> _mcStack = new();

    /// <summary>Output stream to write the rewritten content stream into.</summary>
    private PdfOutputStream _out = null!;

    private ContentStreamPathBandShifter(IReadOnlyList<PathBandOverlay> bands)
        : base(new ContentStreamHelpers.NoOpListener())
    {
        _bands = bands;
    }

    /// <summary>
    /// Rewrite <paramref name="page"/>'s content stream, applying the given band shifts to
    /// every unshielded <c>re</c> op. Runs AFTER <see cref="ContentStreamMcidMover"/>
    /// so shifts on tagged content are already committed to /Contents before we walk
    /// the stream a second time.
    /// </summary>
    internal static void Apply(PdfPage page, IReadOnlyList<PathBandOverlay>? bands)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (bands is null || bands.Count == 0) return;

        var originalBytes = page.GetContentBytes();
        if (originalBytes is null || originalBytes.Length == 0) return;

        var resources = page.GetResources();
        var freshContent = new PdfStream();
        page.GetPdfObject().Put(PdfName.Contents, freshContent);
        page.GetPdfObject().SetModified();
        ContentStreamHelpers.StripAppleHashKeys(page);

        var proc = new ContentStreamPathBandShifter(bands)
        {
            _out = freshContent.GetOutputStream()
        };
        proc.ProcessContent(originalBytes, resources);
    }

    protected override void InvokeOperator(PdfLiteral op, IList<PdfObject> operands)
    {
        var opName = op.ToString();
        switch (opName)
        {
            case "BDC":
                {
                    bool hasMcid = operands.Count >= 3 && HasInlineMcid(operands[1]);
                    _mcStack.Push(hasMcid);
                    WriteOperandsAndOperator(operands);
                    return;
                }
            case "BMC":
                // BMC never has an inline properties dict — always shift-eligible.
                _mcStack.Push(false);
                WriteOperandsAndOperator(operands);
                return;
            case "EMC":
                if (_mcStack.Count > 0) _mcStack.Pop();
                WriteOperandsAndOperator(operands);
                return;
            case "re":
                {
                    if (operands.Count >= 4 && !InsideTaggedMcid())
                    {
                        double x = NumOrZero(operands[0]);
                        double y = NumOrZero(operands[1]);
                        double w = NumOrZero(operands[2]);
                        double h = NumOrZero(operands[3]);
                        var shifted = ApplyBands(x, y, w, h);
                        // Only rewrite if a band actually changed (y, h). x/w never move.
                        // Pass-through preserves iText's original number formatting when
                        // the rect is above every band — makes the output diff smaller.
                        if (shifted.Y != y || shifted.H != h)
                        {
                            _out.WriteString(
                                Fmt(shifted.X) + " " + Fmt(shifted.Y) + " " +
                                Fmt(shifted.W) + " " + Fmt(shifted.H) + " re\n");
                            return;
                        }
                    }
                    WriteOperandsAndOperator(operands);
                    return;
                }
            default:
                WriteOperandsAndOperator(operands);
                return;
        }
    }

    private bool InsideTaggedMcid()
    {
        foreach (var b in _mcStack) if (b) return true;
        return false;
    }

    /// <summary>
    /// Apply every band's per-rect rule in order. Returns (x, y, w, h) after all
    /// applicable bands. Multiple bands on the same page compose.
    /// </summary>
    private (double X, double Y, double W, double H) ApplyBands(double x, double y, double w, double h)
    {
        double curY = y, curH = h;
        foreach (var band in _bands)
        {
            double top = curY + curH;
            if (curY >= band.BandTopY) continue;         // entirely above — unchanged
            if (top <= band.BandTopY)                     // entirely below — shift
            {
                curY += band.Dy;
            }
            else                                          // straddles — grow bottom
            {
                curY += band.Dy;
                curH -= band.Dy;
            }
        }
        return (x, curY, w, curH);
    }

    private void WriteOperandsAndOperator(IList<PdfObject> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (i > 0) _out.WriteSpace();
            _out.Write(operands[i]);
        }
        _out.WriteNewLine();
    }

    private static double NumOrZero(PdfObject o) => o is PdfNumber n ? n.DoubleValue() : 0.0;

    private static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static bool HasInlineMcid(PdfObject props)
    {
        if (props is PdfDictionary d)
        {
            var v = d.Get(PdfName.MCID);
            return v is PdfNumber;
        }
        return false;
    }
}
