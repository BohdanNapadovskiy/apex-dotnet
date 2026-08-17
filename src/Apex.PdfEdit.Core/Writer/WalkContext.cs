using Apex.PdfEdit.Core.Model;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Immutable-per-write-pass state carried through <c>OcrRevectorizeWriter.WalkTree</c>
/// recursion. Extracted from an 11-parameter <c>WalkTree</c> signature so recursive hops
/// carry only the two things that actually change — the current <see cref="TreeNode"/>
/// and the mutable <see cref="iText.Kernel.Pdf.Tagutils.TagTreePointer"/> — instead of
/// re-plumbing every collection reference on each call.
///
/// Kept as a plain class (not a record) because <see cref="NextAddedMcidByPage"/> is a
/// mutable counter map that gets read + written by the caller. All fields are readonly
/// references but their underlying collections are used mutably.
/// </summary>
internal sealed class WalkContext
{
    /// <summary>
    /// Destination <see cref="PdfDocument"/> being written to. Fresh doc per write pass;
    /// not shared with the source.
    /// </summary>
    internal PdfDocument Pdf { get; }

    /// <summary>
    /// Per-page <see cref="PdfCanvas"/> instances, kept open for the whole DFS so
    /// interleaved page visits don't re-open canvases. Keyed by 1-based page number.
    /// </summary>
    internal IDictionary<int, PdfCanvas> CanvasByPage { get; }

    /// <summary>
    /// Extractor geometry data — per-glyph positions and per-MCID word bboxes.
    /// May be null when the caller didn't supply it.
    /// </summary>
    internal GeometryJson? Geometry { get; }

    /// <summary>
    /// <c>mcidKey(page, mcid)</c> for every edited MCID in the plan — the
    /// <c>IsEdited(node)</c> check gates whether a leaf gets vector-only invisible
    /// overlay or a rasterised stamp.
    /// </summary>
    internal ISet<long> EditedMcids { get; }

    /// <summary>
    /// Pre-computed word-wrapped layout keyed by <c>TreeNode.Id</c> for every
    /// edited / added leaf.
    /// </summary>
    internal IDictionary<string, WrapResult> WrapByNodeId { get; }

    /// <summary>
    /// <c>doc.Tree</c> indexed by parent id — DFS traverses this to walk the structure.
    /// Each list is sorted by <c>TreeNode.Order</c> once at build time.
    /// </summary>
    internal IDictionary<string, IList<TreeNode>> ChildrenByParent { get; }

    /// <summary>
    /// Source-side StructElem attribute snapshot (Alt / ActualText / Lang) keyed by
    /// (page, canonicalMcid, role).
    /// </summary>
    internal SourceStructAttrIndex SourceAttrs { get; }

    /// <summary>
    /// Highest <c>Mcid</c> in source's document.json per page — floor for allocating
    /// fresh MCIDs to added leaves so pinned + added never collide.
    /// </summary>
    internal IDictionary<int, int> MaxSourceMcidByPage { get; }

    /// <summary>
    /// Mutable per-page counter for added-leaf MCID allocation.
    /// </summary>
    internal IDictionary<int, int> NextAddedMcidByPage { get; }

    internal WalkContext(
        PdfDocument pdf,
        IDictionary<int, PdfCanvas> canvasByPage,
        GeometryJson? geometry,
        ISet<long> editedMcids,
        IDictionary<string, WrapResult> wrapByNodeId,
        IDictionary<string, IList<TreeNode>> childrenByParent,
        SourceStructAttrIndex sourceAttrs,
        IDictionary<int, int> maxSourceMcidByPage,
        IDictionary<int, int> nextAddedMcidByPage)
    {
        Pdf = pdf;
        CanvasByPage = canvasByPage;
        Geometry = geometry;
        EditedMcids = editedMcids;
        WrapByNodeId = wrapByNodeId;
        ChildrenByParent = childrenByParent;
        SourceAttrs = sourceAttrs;
        MaxSourceMcidByPage = maxSourceMcidByPage;
        NextAddedMcidByPage = nextAddedMcidByPage;
    }
}
