using System.Text;
using Apex.PdfEdit.Core.Edit;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Drops target <c>BDC ... EMC</c> blocks from a page's content stream and prunes
/// the matching <see cref="PdfStructElem"/> + <see cref="PdfMcr"/> from the copied
/// <see cref="PdfStructTreeRoot"/>. Two-part surgery — the content stream side kills
/// what the eye sees, the tag-tree side kills what screen readers see. Doing only
/// one leaves either invisible-but-tagged ghost content or visible-but-orphaned content.
///
/// POC scope: the vacated space is <b>left empty</b>. Surrounding content is not
/// pulled up.
///
/// Same content-stream limitations as <c>ContentStreamMcidReplacer</c>: nested BDCs
/// and property-dict-referenced MCIDs are not handled — the target must sit as a
/// top-level <c>/Role &lt;&lt;/MCID N&gt;&gt; BDC</c> entry.
/// </summary>
internal sealed class ContentStreamMcidDeleter : PdfCanvasProcessor
{
    private readonly ILogger _log;
    private readonly HashSet<int> _targetMcids;
    private PdfOutputStream _out = null!;
    private int _activeTargetMcid = -1;

    /// <summary>
    /// Text-state operators seen INSIDE the currently-active deleted block, keyed by
    /// op name so the last write wins. Emitted after the block's EMC so downstream MCIDs
    /// that rely on the state persisting see the same values they would have without
    /// the delete. Uses insertion-order enumeration (Dictionary in .NET Core keeps
    /// insertion order in practice; Java uses LinkedHashMap explicitly).
    /// </summary>
    private readonly Dictionary<string, IList<PdfObject>> _preservedStateOps = new(StringComparer.Ordinal);

    /// <summary>
    /// Net q/Q balance seen inside the currently-active deleted block. Positive means
    /// more q than Q (we owe compensating Q's after the block); negative means more
    /// Q than q (we owe compensating Q's to keep the stack balanced).
    /// </summary>
    private int _qBalanceInsideBlock;

    /// <summary>
    /// Cumulative text-matrix offset (Td/TD/T*) applied inside the currently-active
    /// deleted block. Replayed after EMC as a single <c>dx dy Td</c> so downstream MCIDs
    /// sharing the outer BT/ET see the same text matrix they would have without the delete.
    /// </summary>
    private double _dxInsideBlock;

    private double _dyInsideBlock;

    private bool _tmInsideBlock;

    /// <summary>Current leading, tracked only for T* inside the deleted block.</summary>
    private double? _leadingInsideBlock;

    private bool _tStarWithUnknownLeading;

    /// <summary>
    /// BT/ET nesting depth. Zero means we're not inside a text object; incremented on BT,
    /// decremented on ET. Snapshot at BDC entry so we know whether the block is inside an
    /// outer BT (compensation matters) or opens its own (doesn't).
    /// </summary>
    private int _btDepth;

    private int _btDepthAtBlockEntry;

    private ContentStreamMcidDeleter(HashSet<int> targetMcids, ILogger log)
        : base(new ContentStreamHelpers.NoOpListener())
    {
        _targetMcids = targetMcids;
        _log = log;
    }

    /// <summary>
    /// Apply deletes on <paramref name="page"/>: rewrite the content stream (drop target
    /// BDC/EMC blocks) and prune the tag tree (remove the matching StructElem + MCR).
    /// </summary>
    internal static void Apply(PdfPage page, IReadOnlyList<DeleteOverlay>? deletes, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (deletes is null || deletes.Count == 0) return;
        var log = logger ?? NullLogger.Instance;

        var targetMcids = new HashSet<int>();
        foreach (var d in deletes) targetMcids.Add(d.Mcid);

        // Content-stream side.
        RewriteContent(page, targetMcids, log);

        // Tag-tree side.
        var doc = page.GetDocument();
        var root = doc.GetStructTreeRoot();
        foreach (var d in deletes)
        {
            var removed = RemoveStructElem(root, page, d.Mcid, log);
            if (removed is null)
            {
                log.LogWarning(
                    "deleteNode {NodeId} on page {Page}: no StructElem found for MCID {Mcid} — content-stream drop applied but tag tree unchanged",
                    d.NodeId, doc.GetPageNumber(page), d.Mcid);
            }
        }
    }

    private static void RewriteContent(PdfPage page, HashSet<int> targetMcids, ILogger log)
    {
        var originalBytes = page.GetContentBytes();
        if (originalBytes is null || originalBytes.Length == 0) return;

        var resources = page.GetResources();
        var freshContent = new PdfStream();
        page.GetPdfObject().Put(PdfName.Contents, freshContent);
        page.GetPdfObject().SetModified();
        ContentStreamHelpers.StripAppleHashKeys(page);

        var proc = new ContentStreamMcidDeleter(targetMcids, log)
        {
            _out = freshContent.GetOutputStream()
        };
        proc.ProcessContent(originalBytes, resources);
    }

    protected override void InvokeOperator(PdfLiteral op, IList<PdfObject> operands)
    {
        var opName = op.ToString();

        if ("BDC".Equals(opName, StringComparison.Ordinal) && operands.Count >= 3)
        {
            int mcid = ExtractInlineMcid(operands[1]);
            if (_activeTargetMcid < 0 && _targetMcids.Contains(mcid))
            {
                // Enter skip mode — drop BDC, everything inside, and the closing EMC.
                _activeTargetMcid = mcid;
                _preservedStateOps.Clear();
                _qBalanceInsideBlock = 0;
                _dxInsideBlock = 0;
                _dyInsideBlock = 0;
                _tmInsideBlock = false;
                _leadingInsideBlock = null;
                _tStarWithUnknownLeading = false;
                _btDepthAtBlockEntry = _btDepth;
                return;
            }
            WriteOperandsAndOperator(operands);
            return;
        }
        if ("EMC".Equals(opName, StringComparison.Ordinal) && _activeTargetMcid >= 0)
        {
            // End of the block we're skipping — emit preserved text-state ops so downstream
            // MCIDs that inherit state (font, render mode, spacing) still work. Then replay
            // the block's cumulative text-matrix offset and emit compensating q/Q so the
            // graphics-state stack balance is preserved.
            int deletedMcid = _activeTargetMcid;
            _activeTargetMcid = -1;
            foreach (var stateOp in _preservedStateOps.Values)
            {
                WriteOperandsAndOperator(stateOp);
            }
            _preservedStateOps.Clear();
            EmitTextMatrixCompensation(deletedMcid);
            if (_qBalanceInsideBlock > 0)
            {
                for (int i = 0; i < _qBalanceInsideBlock; i++) WriteOperatorLiteral("q");
            }
            else if (_qBalanceInsideBlock < 0)
            {
                for (int i = 0; i < -_qBalanceInsideBlock; i++) WriteOperatorLiteral("Q");
            }
            _qBalanceInsideBlock = 0;
            return;
        }
        if (_activeTargetMcid >= 0)
        {
            // Inside a target BDC/EMC — track state ops for post-block replay, then drop.
            if (IsPersistentTextStateOp(opName))
            {
                _preservedStateOps[opName] = new List<PdfObject>(operands);
            }
            if ("q".Equals(opName, StringComparison.Ordinal)) _qBalanceInsideBlock++;
            else if ("Q".Equals(opName, StringComparison.Ordinal)) _qBalanceInsideBlock--;
            else if ("BT".Equals(opName, StringComparison.Ordinal))
            {
                _btDepth++;
                // Self-contained BT inside the block resets the text matrix on ET,
                // so any Td/TD/T* accumulated here won't leak downstream. Clear.
                _dxInsideBlock = 0;
                _dyInsideBlock = 0;
                _tmInsideBlock = false;
                _leadingInsideBlock = null;
                _tStarWithUnknownLeading = false;
            }
            else if ("ET".Equals(opName, StringComparison.Ordinal))
            {
                _btDepth--;
                if (_btDepth <= _btDepthAtBlockEntry)
                {
                    _dxInsideBlock = 0;
                    _dyInsideBlock = 0;
                    _tmInsideBlock = false;
                    _leadingInsideBlock = null;
                    _tStarWithUnknownLeading = false;
                }
            }
            else if (_btDepth == _btDepthAtBlockEntry && _btDepth > 0)
            {
                // Only track text-matrix moves at the SAME BT-depth as at block entry.
                TrackTextMatrixOp(opName, operands);
            }
            return;
        }
        if ("BT".Equals(opName, StringComparison.Ordinal)) _btDepth++;
        else if ("ET".Equals(opName, StringComparison.Ordinal)) _btDepth--;
        WriteOperandsAndOperator(operands);
    }

    /// <summary>
    /// Update cumulative Td/TD/T* offset and leading tracking for text-matrix ops seen
    /// inside the deleted block at the same BT-depth as at block entry.
    /// </summary>
    private void TrackTextMatrixOp(string opName, IList<PdfObject> operands)
    {
        switch (opName)
        {
            case "Td":
                if (operands.Count >= 2)
                {
                    _dxInsideBlock += ((PdfNumber)operands[0]).DoubleValue();
                    _dyInsideBlock += ((PdfNumber)operands[1]).DoubleValue();
                }
                break;
            case "TD":
                if (operands.Count >= 2)
                {
                    double tx = ((PdfNumber)operands[0]).DoubleValue();
                    double ty = ((PdfNumber)operands[1]).DoubleValue();
                    _dxInsideBlock += tx;
                    _dyInsideBlock += ty;
                    _leadingInsideBlock = -ty;
                }
                break;
            case "T*":
                if (_leadingInsideBlock is { } lead)
                {
                    _dyInsideBlock -= lead;
                }
                else
                {
                    _tStarWithUnknownLeading = true;
                }
                break;
            case "Tm":
                _tmInsideBlock = true;
                break;
            case "TL":
                if (operands.Count > 0)
                {
                    _leadingInsideBlock = ((PdfNumber)operands[0]).DoubleValue();
                }
                break;
        }
    }

    /// <summary>
    /// Emit <c>dx dy Td</c> to replay the deleted block's cumulative text-matrix shift
    /// for downstream MCIDs sharing the outer BT/ET. Skips compensation (with a warning)
    /// when Tm was used inside the block or when T* fired without a known leading.
    /// Skips silently when there's no offset to replay or we're not inside an outer BT.
    /// </summary>
    private void EmitTextMatrixCompensation(int deletedMcid)
    {
        bool insideOuterBt = _btDepth > 0;
        if (!insideOuterBt) return;
        if (_tmInsideBlock)
        {
            _log.LogWarning(
                "deleteNode MCID {Mcid}: Tm inside deleted block — skipping text-matrix compensation, downstream text position may drift",
                deletedMcid);
            return;
        }
        if (_tStarWithUnknownLeading)
        {
            _log.LogWarning(
                "deleteNode MCID {Mcid}: T* inside deleted block without a known leading — skipping text-matrix compensation, downstream text position may drift",
                deletedMcid);
            return;
        }
        if (_dxInsideBlock == 0 && _dyInsideBlock == 0) return;
        WriteTdOp(_dxInsideBlock, _dyInsideBlock);
    }

    private void WriteTdOp(double dx, double dy)
    {
        _out.Write(new PdfNumber(dx));
        _out.WriteSpace();
        _out.Write(new PdfNumber(dy));
        _out.WriteSpace();
        _out.WriteBytes(Encoding.ASCII.GetBytes("Td"));
        _out.WriteNewLine();
    }

    /// <summary>
    /// Graphics/text-state operators whose effects persist past their enclosing text
    /// object and marked-content block. Two groups:
    /// <list type="bullet">
    ///   <item>Text state: Tc, Tw, Tz, TL, Tf, Tr, Ts.</item>
    ///   <item>Colour state (non-stroking + stroking): rg/g/k/scn/sc/cs and uppercase counterparts.</item>
    /// </list>
    /// </summary>
    private static bool IsPersistentTextStateOp(string name) => name switch
    {
        "Tc" or "Tw" or "Tz" or "TL" or "Tf" or "Tr" or "Ts" => true,
        "rg" or "g" or "k" or "scn" or "sc" or "cs" => true,
        "RG" or "G" or "K" or "SCN" or "SC" or "CS" => true,
        _ => false
    };

    private void WriteOperandsAndOperator(IList<PdfObject> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (i > 0) _out.WriteSpace();
            _out.Write(operands[i]);
        }
        _out.WriteNewLine();
    }

    /// <summary>
    /// Emit a bare operator (no operands) — used to write compensating q/Q after a
    /// deleted block whose q/Q count didn't balance.
    /// </summary>
    private void WriteOperatorLiteral(string opName)
    {
        _out.WriteBytes(Encoding.ASCII.GetBytes(opName));
        _out.WriteNewLine();
    }

    /// <summary>
    /// Walk the tag tree DFS looking for a <see cref="PdfMcr"/> matching (page, mcid).
    /// Remove the enclosing <see cref="PdfStructElem"/> from its parent's kids array
    /// (the MCR goes with it). Returns the removed element for logging, or null if
    /// not found.
    /// </summary>
    internal static PdfStructElem? RemoveStructElem(PdfStructTreeRoot root, PdfPage page, int mcid,
        ILogger? logger = null)
    {
        var donor = FindStructElemContainingMcid(root, page, mcid);
        if (donor is null) return null;
        var parent = donor.GetParent();
        if (parent is PdfStructElem parentSe)
        {
            parentSe.RemoveKid(donor);
            return donor;
        }
        // Root-level StructElem: PdfStructTreeRoot exposes only AddKid, not RemoveKid.
        // Rare in the corpus. For the POC we log and skip the tag-tree side — the
        // content stream is still cleared, which is what the eye sees.
        (logger ?? NullLogger.Instance).LogWarning(
            "deleteNode: MCID {Mcid} on page {Page} is a root-level StructElem; tag-tree prune skipped",
            mcid, page.GetDocument().GetPageNumber(page));
        return null;
    }

    private static PdfStructElem? FindStructElemContainingMcid(IStructureNode node, PdfPage page, int mcid)
    {
        var kids = node.GetKids();
        if (kids is null) return null;
        foreach (var kid in kids)
        {
            if (kid is PdfMcr mcr)
            {
                if (mcr.GetMcid() == mcid && SameIndirect(mcr, page))
                {
                    return node as PdfStructElem;
                }
            }
            else if (kid is PdfStructElem child)
            {
                var found = FindStructElemContainingMcid(child, page, mcid);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static bool SameIndirect(PdfMcr mcr, PdfPage page)
    {
        var mcrRef = mcr.GetPageIndirectReference();
        var pageRef = page.GetPdfObject().GetIndirectReference();
        return mcrRef is not null && mcrRef.Equals(pageRef);
    }

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
