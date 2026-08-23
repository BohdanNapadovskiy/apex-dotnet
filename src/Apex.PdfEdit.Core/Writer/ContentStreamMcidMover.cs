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

    /// <summary>
    /// (dx, dy) currently baked into the OUTPUT stream's live text state by a previously
    /// emitted shift Tm. When the next shift-target BDC wants the same offset, no Tm is
    /// emitted at all — the block composes relatively off the already-shifted cursor.
    /// Critical for continuation blocks that open with a text-show op (inline-link
    /// tail: P → Link → P on one line): an absolute Tm there rewinds X to the line
    /// start, overprinting the link, because glyph advances are not tracked here.
    /// Reset to 0 whenever a source Tm passes through unshifted or BT resets the matrix.
    /// </summary>
    private double _appliedDx;
    private double _appliedDy;

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

        // SUM per-mcid: sequential push-downs each emit their own overlay for the same
        // block (form-40x p2: two list inserts moved a heading -32 then -64; last-wins
        // dropped the first shift while path bands composed both — decorations overshot
        // their text by exactly the lost dy).
        var byMcid = new Dictionary<int, MoveOverlay>(shifts.Count);
        foreach (var o in shifts)
        {
            byMcid[o.Mcid] = byMcid.TryGetValue(o.Mcid, out var prev)
                ? prev with { Dx = prev.Dx + o.Dx, Dy = prev.Dy + o.Dy }
                : o;
        }

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
            _appliedDx = _appliedDy = 0.0;
            WriteOperandsAndOperator(operands);
            // Flush any pending shift Tm from a BDC that opened just outside BT
            // (per-MCID BT layout: BDC-first, BT-inside).
            if (_pendingShiftForNextBt is { } shift)
            {
                _out.WriteString(
                    Fmt(_tmA) + " " + Fmt(_tmB) + " " + Fmt(_tmC) + " " + Fmt(_tmD) + " " +
                    Fmt(_tmE + shift.Dx) + " " + Fmt(_tmF + shift.Dy) + " Tm\n");
                // Tracker stays on the UNSHIFTED source position — folding the shift in
                // makes every later shift-target BDC add its dy on top of the previous
                // one (2026 Proxy p3: −31pt became −31/−63/−94/... down the page).
                // Diverges from Java, which has the same latent bug — see PORTING_PLAN §9.
                _appliedDx = shift.Dx;
                _appliedDy = shift.Dy;
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
                _appliedDx = _activeTmDx;
                _appliedDy = _activeTmDy;
            }
            else
            {
                WriteOperandsAndOperator(operands);
                _appliedDx = _appliedDy = 0.0;
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
                if (_appliedDx == shift.Dx && _appliedDy == shift.Dy)
                {
                    // The live output state already carries this exact shift (previous
                    // block in the chain) — composing relatively preserves intra-line
                    // glyph advances that an absolute Tm would destroy.
                    return;
                }
                // Outer-BT layout: BDC nested inside a wrapping BT — emit the shift Tm right away.
                // Tracker stays on the UNSHIFTED source position (see BT-flush note above).
                _out.WriteString(
                    Fmt(_tmA) + " " + Fmt(_tmB) + " " + Fmt(_tmC) + " " + Fmt(_tmD) + " " +
                    Fmt(_tmE + shift.Dx) + " " + Fmt(_tmF + shift.Dy) + " Tm\n");
                _appliedDx = shift.Dx;
                _appliedDy = shift.Dy;
            }
            return;
        }
        if ("EMC".Equals(opName, StringComparison.Ordinal))
        {
            WriteOperandsAndOperator(operands);
            _activeTmDx = 0.0;
            _activeTmDy = 0.0;
            // A shift deferred for this block's BT must die with the block. A BT-less
            // target block otherwise leaks its shift Tm into the next unrelated BT —
            // Bessemer p2's untagged chart labels picked up a stray −50.143 Tm this way.
            _pendingShiftForNextBt = null;
            return;
        }
        // Vector paths inside a shift-target block ride along with the text — a Figure's
        // border rect (Bessemer p2 chart frame, drawn inside the tagged BDC) otherwise
        // stays pinned while the block's text and the untagged artwork below both move.
        if ((_activeTmDx != 0.0 || _activeTmDy != 0.0) && ShiftPathOp(opName, operands))
        {
            return;
        }
        WriteOperandsAndOperator(operands);
    }

    /// <summary>
    /// Rewrite a path-construction op's coordinates by the active block shift.
    /// Returns false for non-path ops (caller writes them unchanged).
    /// </summary>
    private bool ShiftPathOp(string opName, IList<PdfObject> operands)
    {
        (int Pairs, bool Ok) spec = opName switch
        {
            "m" or "l" => (1, operands.Count >= 3),
            "v" or "y" => (2, operands.Count >= 5),
            "c" => (3, operands.Count >= 7),
            "re" => (1, operands.Count >= 5),
            _ => (0, false)
        };
        if (!spec.Ok) return false;
        for (int p = 0; p < spec.Pairs; p++)
        {
            _out.WriteString(Fmt(NumOrZero(operands[p * 2]) + _activeTmDx) + " " +
                             Fmt(NumOrZero(operands[p * 2 + 1]) + _activeTmDy) + " ");
        }
        if ("re".Equals(opName, StringComparison.Ordinal))
        {
            _out.WriteString(Fmt(NumOrZero(operands[2])) + " " + Fmt(NumOrZero(operands[3])) + " ");
        }
        _out.WriteString(opName + "\n");
        return true;
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
