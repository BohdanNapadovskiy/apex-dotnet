using System.Globalization;
using Apex.PdfEdit.Core.Edit;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Walks a page's content stream and shifts every <c>BDC ... EMC</c> block whose
/// inline <c>/MCID</c> matches a shift instruction by the requested (dx, dy).
/// Implements the push-down half of the §5.5 rule: after <c>AddParagraphStamper</c>
/// makes room for a new paragraph, this mover slides downstream siblings down by
/// the new paragraph's height.
///
/// <b>Why not q/cm/Q</b> around each BDC: PDF §8.5.2 forbids q/Q inside a text
/// object (BT..ET). Strict renderers (Chrome, Adobe Acrobat) silently drop them,
/// so the target BDC renders at its original position.
///
/// <b>Why not just Td</b>: Td's operands are pre-multiplied by the current text-line
/// matrix, so with a common <c>10 0 0 10 e f Tm</c> in effect a <c>0 -14 Td</c>
/// actually shifts by 14 × 10 = 140 pt.
///
/// <b>How the shift works now</b>: track the source's Tm and Tlm through Tm/Td/TD/T*/BT
/// as we walk the stream. On a shift-target BDC, emit an <i>absolute</i>
/// <c>a b c d e (f + dy) Tm</c> using the tracked position — the shift is applied
/// in <b>user space</b>. Every Tm inside the shifted BDC is rewritten the same way
/// so mid-block position resets still land shifted. Relative moves (Td/TD/T*) pass
/// through unchanged — they compose off the shifted position naturally.
/// </summary>
internal sealed class ContentStreamMcidMover : PdfCanvasProcessor
{
    private readonly IReadOnlyDictionary<int, MoveOverlay> _byMcid;
    private PdfOutputStream _out = null!;

    // Current text matrix (Tm) and text-line matrix (Tlm), tracked through the stream.
    // Both reset to identity at each BT. See PDF spec §9.4.
    private double _tmA = 1, _tmB, _tmC, _tmD = 1, _tmE, _tmF;
    private double _tlmA = 1, _tlmB, _tlmC, _tlmD = 1, _tlmE, _tlmF;

    /// <summary>Text leading (TL) — the vertical offset used by T* and single-quote ops.</summary>
    private double _leading;

    /// <summary>dx to apply to Tm operators inside the current shift-target BDC. 0 outside.</summary>
    private double _activeTmDx;

    /// <summary>dy to apply to Tm operators inside the current shift-target BDC. 0 outside.</summary>
    private double _activeTmDy;

    /// <summary>
    /// True while inside a BT/ET text object. Tm is only legal in a text object,
    /// so we must not emit our shift Tm while outside one.
    /// </summary>
    private bool _insideTextObject;

    /// <summary>
    /// When a shift-target BDC opens OUTSIDE a text object (per-MCID BT layout — the
    /// source's BT comes AFTER the BDC), defer the shift Tm until BT arrives.
    /// </summary>
    private MoveOverlay? _pendingShiftForNextBt;

    private ContentStreamMcidMover(IReadOnlyDictionary<int, MoveOverlay> byMcid)
        : base(new ContentStreamHelpers.NoOpListener())
    {
        _byMcid = byMcid;
    }

    /// <summary>
    /// Rewrite <paramref name="page"/>'s content stream, applying the given shifts.
    /// Reads the current /Contents (which may already be a fresh stream if
    /// <c>ContentStreamMcidReplacer</c> ran first) and writes a new single stream in
    /// its place.
    /// </summary>
    internal static void Apply(PdfPage page, IReadOnlyList<MoveOverlay>? shifts)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (shifts is null || shifts.Count == 0) return;

        var byMcid = new Dictionary<int, MoveOverlay>(shifts.Count);
        foreach (var o in shifts) byMcid[o.Mcid] = o;

        var originalBytes = page.GetContentBytes();
        if (originalBytes is null || originalBytes.Length == 0) return;

        var resources = page.GetResources();
        var freshContent = new PdfStream();
        page.GetPdfObject().Put(PdfName.Contents, freshContent);
        page.GetPdfObject().SetModified();
        ContentStreamHelpers.StripAppleHashKeys(page);

        var proc = new ContentStreamMcidMover(byMcid)
        {
            _out = freshContent.GetOutputStream()
        };
        proc.ProcessContent(originalBytes, resources);
    }

    protected override void InvokeOperator(PdfLiteral op, IList<PdfObject> operands)
    {
        var opName = op.ToString();

        // ---- Text-object boundaries: BT resets Tm/Tlm to identity per §9.4.1 ----
        if ("BT".Equals(opName, StringComparison.Ordinal))
        {
            _tmA = _tmD = _tlmA = _tlmD = 1.0;
            _tmB = _tmC = _tmE = _tmF = 0.0;
            _tlmB = _tlmC = _tlmE = _tlmF = 0.0;
            _insideTextObject = true;
            WriteOperandsAndOperator(operands);
            // Flush any pending shift Tm from a BDC that opened just outside BT
            // (per-MCID BT layout: BDC-first, BT-inside).
            if (_pendingShiftForNextBt is { } shift)
            {
                _out.WriteString(
                    Fmt(_tmA) + " " + Fmt(_tmB) + " " + Fmt(_tmC) + " " + Fmt(_tmD) + " " +
                    Fmt(_tmE + shift.Dx) + " " + Fmt(_tmF + shift.Dy) + " Tm\n");
                _tmE = _tlmE = _tmE + shift.Dx;
                _tmF = _tlmF = _tmF + shift.Dy;
                _pendingShiftForNextBt = null;
            }
            return;
        }
        if ("ET".Equals(opName, StringComparison.Ordinal))
        {
            _insideTextObject = false;
            WriteOperandsAndOperator(operands);
            return;
        }

        // ---- Text-state / positioning ops we need to track ----
        if ("TL".Equals(opName, StringComparison.Ordinal) && operands.Count >= 1)
        {
            _leading = NumOrZero(operands[0]);
            WriteOperandsAndOperator(operands);
            return;
        }
        if ("Tm".Equals(opName, StringComparison.Ordinal) && operands.Count >= 6)
        {
            double a = NumOrZero(operands[0]);
            double b = NumOrZero(operands[1]);
            double c = NumOrZero(operands[2]);
            double d = NumOrZero(operands[3]);
            double e = NumOrZero(operands[4]);
            double f = NumOrZero(operands[5]);
            // Track the source-stream Tm (unshifted).
            _tmA = _tlmA = a;
            _tmB = _tlmB = b;
            _tmC = _tlmC = c;
            _tmD = _tlmD = d;
            _tmE = _tlmE = e;
            _tmF = _tlmF = f;
            if (_activeTmDx != 0.0 || _activeTmDy != 0.0)
            {
                _out.WriteString(
                    Fmt(a) + " " + Fmt(b) + " " + Fmt(c) + " " + Fmt(d) + " " +
                    Fmt(e + _activeTmDx) + " " + Fmt(f + _activeTmDy) + " Tm\n");
            }
            else
            {
                WriteOperandsAndOperator(operands);
            }
            return;
        }
        if (("Td".Equals(opName, StringComparison.Ordinal) || "TD".Equals(opName, StringComparison.Ordinal))
            && operands.Count >= 2)
        {
            double tx = NumOrZero(operands[0]);
            double ty = NumOrZero(operands[1]);
            _tlmE = tx * _tlmA + ty * _tlmC + _tlmE;
            _tlmF = tx * _tlmB + ty * _tlmD + _tlmF;
            _tmE = _tlmE;
            _tmF = _tlmF;
            if ("TD".Equals(opName, StringComparison.Ordinal)) _leading = -ty;
            WriteOperandsAndOperator(operands);
            return;
        }
        if ("T*".Equals(opName, StringComparison.Ordinal))
        {
            double ty = -_leading;
            _tlmE = ty * _tlmC + _tlmE;
            _tlmF = ty * _tlmD + _tlmF;
            _tmE = _tlmE;
            _tmF = _tlmF;
            WriteOperandsAndOperator(operands);
            return;
        }

        // ---- Marked-content structural ops ----
        if ("BDC".Equals(opName, StringComparison.Ordinal) && operands.Count >= 3)
        {
            int mcid = ExtractInlineMcid(operands[1]);
            WriteOperandsAndOperator(operands);
            if (mcid >= 0 && _byMcid.TryGetValue(mcid, out var shift))
            {
                _activeTmDx = shift.Dx;
                _activeTmDy = shift.Dy;
                if (!_insideTextObject)
                {
                    // Per-MCID BT layout: BDC-first, BT-inside. Defer until BT.
                    _pendingShiftForNextBt = shift;
                    return;
                }
                // Outer-BT layout: BDC nested inside a wrapping BT — emit the shift Tm right away.
                _out.WriteString(
                    Fmt(_tmA) + " " + Fmt(_tmB) + " " + Fmt(_tmC) + " " + Fmt(_tmD) + " " +
                    Fmt(_tmE + shift.Dx) + " " + Fmt(_tmF + shift.Dy) + " Tm\n");
                _tmE = _tlmE = _tmE + shift.Dx;
                _tmF = _tlmF = _tmF + shift.Dy;
            }
            return;
        }
        if ("EMC".Equals(opName, StringComparison.Ordinal))
        {
            WriteOperandsAndOperator(operands);
            _activeTmDx = 0.0;
            _activeTmDy = 0.0;
            return;
        }
        WriteOperandsAndOperator(operands);
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

    private static int ExtractInlineMcid(PdfObject props)
    {
        if (props is PdfDictionary d)
        {
            var v = d.Get(PdfName.MCID);
            if (v is PdfNumber n) return n.IntValue();
        }
        return -1;
    }
}
