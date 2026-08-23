using System.Globalization;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Apex.PdfEdit.Core.Edit;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Applies <see cref="PathBandOverlay"/> vertical shifts to untagged page content:
/// vector paths (<c>re m l c v y</c>) and text shown outside any MCID-bearing
/// <c>BDC/EMC</c> block (chart labels, artifacts). Sibling of
/// <see cref="ContentStreamMcidMover"/>: the mover handles tagged MCID blocks;
/// this class handles the leftover decorative artwork and untagged text that would
/// otherwise stay put and detach from the shifted content.
///
/// <b>CTM-aware</b>: chart artwork is routinely drawn under accumulated <c>cm</c>
/// translations (Bessemer p2 positions bar-chart labels via <c>Td</c> under a cm
/// chain that nets back to identity), so raw operand coordinates are meaningless —
/// the shifter tracks q/Q/cm and evaluates DEVICE-space positions. Shifts are only
/// applied while the CTM is translation+scale (no rotation/skew) with positive
/// scale; anything else passes through unchanged.
///
/// <b>Whole-path rule</b> (device-space bbox of the buffered path vs
/// <see cref="PathBandOverlay.BandTopY"/>):
/// <list type="bullet">
///   <item>entirely below the band top: translate every construction op.</item>
///   <item>entirely above: unchanged.</item>
///   <item>straddling: single-<c>re</c> paths keep the legacy grow-bottom rule
///       (top edge pinned so an enclosing border still contains both halves);
///       multi-op paths pass through unchanged so closed artwork (donut charts)
///       doesn't tear.</item>
/// </list>
///
/// <b>Untagged text rule</b>: the first positioning op of each <c>BT</c>
/// (an absolute <c>Tm</c>, or the first <c>Td</c>/<c>TD</c> which acts absolutely
/// on the BT-reset identity matrix) is shifted when its device-space baseline is
/// below the band top. Later relative <c>Td</c>s inherit the shift naturally.
/// Text inside a tagged BDC is never touched — the mover owns it.
///
/// <b>Scope caveat</b> — image draws (<c>Do</c> / inline <c>BI...EI</c>) are NOT handled.
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

    // ---- CTM tracking (a b c d e f) — row-vector convention, cm premultiplies. ----
    private readonly Stack<Matrix2D> _ctmStack = new();
    private Matrix2D _ctm = Matrix2D.Identity;

    // ---- Path buffering ----
    private readonly List<(string Op, List<PdfObject> Operands)> _pathBuffer = new();
    private bool _inPath;

    // ---- Text state ----
    private bool _insideText;
    private bool _firstTextPosDone;

    private readonly record struct Matrix2D(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix2D Identity = new(1, 0, 0, 1, 0, 0);

        public Matrix2D Premultiply(Matrix2D m) => new(
            m.A * A + m.B * C,
            m.A * B + m.B * D,
            m.C * A + m.D * C,
            m.C * B + m.D * D,
            m.E * A + m.F * C + E,
            m.E * B + m.F * D + F);

        public (double X, double Y) Apply(double x, double y)
            => (A * x + C * y + E, B * x + D * y + F);

        /// <summary>
        /// Translation and axis-aligned positive scale only. Epsilon-tolerant: producers
        /// emit near-identity matrices like <c>0.999997 -0.000001 -0.000002 1 … cm</c>
        /// (Bessemer) — an exact zero test would disable shifting for the whole page.
        /// </summary>
        public bool IsShiftSafe => Math.Abs(B) <= 1e-3 && Math.Abs(C) <= 1e-3 && A > 0 && D > 0;
    }

    private ContentStreamPathBandShifter(IReadOnlyList<PathBandOverlay> bands)
        : base(new ContentStreamHelpers.NoOpListener())
    {
        _bands = bands;
    }

    /// <summary>
    /// Rewrite <paramref name="page"/>'s content stream, applying the given band shifts.
    /// Runs AFTER <see cref="ContentStreamMcidMover"/> so shifts on tagged content are
    /// already committed to /Contents before we walk the stream a second time.
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

    private static readonly HashSet<string> PathConstructionOps = new(StringComparer.Ordinal)
    {
        "m", "l", "c", "v", "y", "re", "h"
    };

    private static readonly HashSet<string> PathPaintingOps = new(StringComparer.Ordinal)
    {
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"
    };

    protected override void InvokeOperator(PdfLiteral op, IList<PdfObject> operands)
    {
        var opName = op.ToString();

        if (PathConstructionOps.Contains(opName))
        {
            _inPath = true;
            _pathBuffer.Add((opName, new List<PdfObject>(operands)));
            return;
        }
        if (_inPath && (opName == "W" || opName == "W*"))
        {
            // Clip modifier between construction and paint — keep with the path.
            _pathBuffer.Add((opName, new List<PdfObject>(operands)));
            return;
        }
        if (_inPath && PathPaintingOps.Contains(opName))
        {
            FlushPath(operands);
            return;
        }

        switch (opName)
        {
            case "q":
                _ctmStack.Push(_ctm);
                break;
            case "Q":
                if (_ctmStack.Count > 0) _ctm = _ctmStack.Pop();
                break;
            case "cm":
                if (operands.Count >= 7)
                {
                    _ctm = _ctm.Premultiply(new Matrix2D(
                        NumOrZero(operands[0]), NumOrZero(operands[1]),
                        NumOrZero(operands[2]), NumOrZero(operands[3]),
                        NumOrZero(operands[4]), NumOrZero(operands[5])));
                }
                break;
            case "BDC":
                _mcStack.Push(operands.Count >= 3 && HasInlineMcid(operands[1]));
                break;
            case "BMC":
                // BMC never has an inline properties dict — always shift-eligible.
                _mcStack.Push(false);
                break;
            case "EMC":
                if (_mcStack.Count > 0) _mcStack.Pop();
                break;
            case "BT":
                _insideText = true;
                _firstTextPosDone = false;
                break;
            case "ET":
                _insideText = false;
                break;
            case "Tm":
                if (_insideText && !InsideTaggedMcid() && operands.Count >= 7 && _ctm.IsShiftSafe)
                {
                    WriteShiftedTextPos(operands, xIdx: 4, yIdx: 5, opName);
                    _firstTextPosDone = true;
                    return;
                }
                _firstTextPosDone = true;
                break;
            case "Td":
            case "TD":
                if (_insideText && !_firstTextPosDone)
                {
                    _firstTextPosDone = true;
                    // First Td after BT acts on the BT-reset identity matrix — absolute.
                    if (!InsideTaggedMcid() && operands.Count >= 3 && _ctm.IsShiftSafe)
                    {
                        WriteShiftedTextPos(operands, xIdx: 0, yIdx: 1, opName);
                        return;
                    }
                }
                break;
        }
        WriteOperandsAndOperator(operands);
    }

    /// <summary>
    /// Emit a Tm/Td/TD with its y operand shifted when the device-space position falls
    /// below a band top. Non-number operands pass through unchanged.
    /// </summary>
    private void WriteShiftedTextPos(IList<PdfObject> operands, int xIdx, int yIdx, string opName)
    {
        double lx = NumOrZero(operands[xIdx]);
        double ly = NumOrZero(operands[yIdx]);
        var (devX, devY) = _ctm.Apply(lx, ly);
        double devDy = ComposePointShift(devX, devY);
        // Never push untagged text off the page bottom (footers, page numbers).
        if (devDy == 0 || devY + devDy < 0)
        {
            WriteOperandsAndOperator(operands);
            return;
        }
        var rewritten = new List<PdfObject>(operands);
        rewritten[yIdx] = new PdfNumber(ly + devDy / _ctm.D);
        WriteOperandsAndOperator(rewritten);
    }

    /// <summary>
    /// Compose all bands' point rule: device y below a band top (and device x within
    /// the band's column span) picks up its dy.
    /// </summary>
    private double ComposePointShift(double devX, double devY)
    {
        double total = 0;
        double cur = devY;
        foreach (var band in _bands)
        {
            if (devX < band.LeftX || devX > band.RightX) continue;
            if (cur < band.BandTopY)
            {
                total += band.Dy;
                cur += band.Dy;
            }
        }
        return total;
    }

    /// <summary>
    /// Decide and emit the buffered path followed by its painting op.
    /// </summary>
    private void FlushPath(IList<PdfObject> paintOperands)
    {
        _inPath = false;
        var buffered = _pathBuffer;
        try
        {
            if (!InsideTaggedMcid() && _ctm.IsShiftSafe && TryComputeDeviceBbox(buffered, out var bbox))
            {
                bool singleRe = buffered.Count == 1 && buffered[0].Op == "re";
                double devDy = 0, growDevH = 0;
                double curBottom = bbox.MinY, curTop = bbox.MaxY;
                foreach (var band in _bands)
                {
                    if (bbox.MaxX < band.LeftX || bbox.MinX > band.RightX) continue; // other column
                    if (curBottom >= band.BandTopY) continue;      // entirely above — unchanged
                    if (curTop <= band.BandTopY)                    // entirely below — translate
                    {
                        devDy += band.Dy;
                        curBottom += band.Dy;
                        curTop += band.Dy;
                    }
                    else if (singleRe)                              // straddles — grow bottom
                    {
                        devDy += band.Dy;
                        growDevH -= band.Dy;
                        curBottom += band.Dy;
                    }
                    // multi-op straddling paths: unchanged (closed artwork must not tear)
                }
                if (devDy != 0 || growDevH != 0)
                {
                    EmitShiftedPath(buffered, devDy / _ctm.D, growDevH / _ctm.D);
                    WriteOperandsAndOperator(paintOperands);
                    return;
                }
            }
            foreach (var (_, ops) in buffered) WriteOperandsAndOperator(ops);
            WriteOperandsAndOperator(paintOperands);
        }
        finally
        {
            _pathBuffer.Clear();
        }
    }

    /// <summary>Device-space bbox over every coordinate pair in the buffered path.</summary>
    private bool TryComputeDeviceBbox(List<(string Op, List<PdfObject> Operands)> path,
        out (double MinX, double MaxX, double MinY, double MaxY) bbox)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var (op, ops) in path)
        {
            switch (op)
            {
                case "m" or "l" when ops.Count >= 3:
                    Acc(NumOrZero(ops[0]), NumOrZero(ops[1]));
                    break;
                case "c" when ops.Count >= 7:
                    Acc(NumOrZero(ops[0]), NumOrZero(ops[1]));
                    Acc(NumOrZero(ops[2]), NumOrZero(ops[3]));
                    Acc(NumOrZero(ops[4]), NumOrZero(ops[5]));
                    break;
                case "v" or "y" when ops.Count >= 5:
                    Acc(NumOrZero(ops[0]), NumOrZero(ops[1]));
                    Acc(NumOrZero(ops[2]), NumOrZero(ops[3]));
                    break;
                case "re" when ops.Count >= 5:
                    {
                        double x = NumOrZero(ops[0]), y = NumOrZero(ops[1]);
                        double w = NumOrZero(ops[2]), h = NumOrZero(ops[3]);
                        Acc(x, y);
                        Acc(x + w, y + h);
                        break;
                    }
            }
        }
        bbox = (minX, maxX, minY, maxY);
        return !double.IsPositiveInfinity(minY);

        void Acc(double x, double y)
        {
            var (devX, devY) = _ctm.Apply(x, y);
            if (devX < minX) minX = devX;
            if (devX > maxX) maxX = devX;
            if (devY < minY) minY = devY;
            if (devY > maxY) maxY = devY;
        }
    }

    /// <summary>Re-emit the buffered path with every y coordinate moved by <paramref name="localDy"/>.</summary>
    private void EmitShiftedPath(List<(string Op, List<PdfObject> Operands)> path,
        double localDy, double localGrowH)
    {
        foreach (var (op, ops) in path)
        {
            switch (op)
            {
                case "m" or "l" when ops.Count >= 3:
                    _out.WriteString(Fmt(NumOrZero(ops[0])) + " " + Fmt(NumOrZero(ops[1]) + localDy) + " " + op + "\n");
                    break;
                case "c" when ops.Count >= 7:
                    _out.WriteString(
                        Fmt(NumOrZero(ops[0])) + " " + Fmt(NumOrZero(ops[1]) + localDy) + " " +
                        Fmt(NumOrZero(ops[2])) + " " + Fmt(NumOrZero(ops[3]) + localDy) + " " +
                        Fmt(NumOrZero(ops[4])) + " " + Fmt(NumOrZero(ops[5]) + localDy) + " c\n");
                    break;
                case "v" or "y" when ops.Count >= 5:
                    _out.WriteString(
                        Fmt(NumOrZero(ops[0])) + " " + Fmt(NumOrZero(ops[1]) + localDy) + " " +
                        Fmt(NumOrZero(ops[2])) + " " + Fmt(NumOrZero(ops[3]) + localDy) + " " + op + "\n");
                    break;
                case "re" when ops.Count >= 5:
                    _out.WriteString(
                        Fmt(NumOrZero(ops[0])) + " " + Fmt(NumOrZero(ops[1]) + localDy) + " " +
                        Fmt(NumOrZero(ops[2])) + " " + Fmt(NumOrZero(ops[3]) + localGrowH) + " re\n");
                    break;
                default:
                    WriteOperandsAndOperator(ops);
                    break;
            }
        }
    }

    private bool InsideTaggedMcid()
    {
        foreach (var b in _mcStack) if (b) return true;
        return false;
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
