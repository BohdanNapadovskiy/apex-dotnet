using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Writer;

namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Instructions for the writer: what visual overlays to draw on top of the copied
/// source pages so that edits are reflected in the output PDF.
///
/// Overlay kinds:
/// <list type="bullet">
///   <item><see cref="SetTextOverlay"/> — replaces the text inside an existing MCID.</item>
///   <item><see cref="AddParagraphOverlay"/> — draws a new tagged block (fresh MCID + StructElem)
///       under a caller-specified parent.</item>
///   <item><see cref="AddListItemOverlay"/> — atomic LI + Lbl + LBody under an existing L container.</item>
///   <item><see cref="MoveOverlay"/> — §5.5 push-down of an existing block.</item>
///   <item><see cref="DeleteOverlay"/> — removes a BDC/EMC block + prunes matching StructElem.</item>
///   <item><see cref="PathBandOverlay"/> — untagged-graphics push-down band.</item>
/// </list>
/// </summary>
public sealed class EditPlan
{
    public IReadOnlyList<SetTextOverlay> SetTextOverlays { get; }
    public IReadOnlyList<AddParagraphOverlay> AddParagraphOverlays { get; }
    public IReadOnlyList<AddListItemOverlay> AddListItemOverlays { get; }
    public IReadOnlyList<MoveOverlay> MoveOverlays { get; }
    public IReadOnlyList<DeleteOverlay> DeleteOverlays { get; }
    public IReadOnlyList<PathBandOverlay> PathBandOverlays { get; }

    public EditPlan(
        IEnumerable<SetTextOverlay> setText,
        IEnumerable<AddParagraphOverlay> addParagraph,
        IEnumerable<AddListItemOverlay> addListItem,
        IEnumerable<MoveOverlay> moves,
        IEnumerable<DeleteOverlay> deletes,
        IEnumerable<PathBandOverlay> pathBands)
    {
        SetTextOverlays = setText.ToList().AsReadOnly();
        AddParagraphOverlays = addParagraph.ToList().AsReadOnly();
        AddListItemOverlays = addListItem.ToList().AsReadOnly();
        MoveOverlays = moves.ToList().AsReadOnly();
        DeleteOverlays = deletes.ToList().AsReadOnly();
        PathBandOverlays = pathBands.ToList().AsReadOnly();
    }

    public static EditPlan Empty()
        => new(Array.Empty<SetTextOverlay>(), Array.Empty<AddParagraphOverlay>(),
               Array.Empty<AddListItemOverlay>(), Array.Empty<MoveOverlay>(),
               Array.Empty<DeleteOverlay>(), Array.Empty<PathBandOverlay>());

    public bool IsEmpty =>
        SetTextOverlays.Count == 0 && AddParagraphOverlays.Count == 0
        && AddListItemOverlays.Count == 0
        && MoveOverlays.Count == 0 && DeleteOverlays.Count == 0
        && PathBandOverlays.Count == 0;

    public static Builder NewBuilder() => new();

    public sealed class Builder
    {
        private readonly List<SetTextOverlay> _setText = new();
        private readonly List<AddParagraphOverlay> _addParagraph = new();
        private readonly List<AddListItemOverlay> _addListItem = new();
        private readonly List<MoveOverlay> _moves = new();
        private readonly List<DeleteOverlay> _deletes = new();
        private readonly List<PathBandOverlay> _pathBands = new();

        public Builder SetText(SetTextOverlay overlay) { _setText.Add(overlay); return this; }
        public Builder AddParagraph(AddParagraphOverlay overlay) { _addParagraph.Add(overlay); return this; }
        public Builder AddListItem(AddListItemOverlay overlay) { _addListItem.Add(overlay); return this; }
        public Builder Move(MoveOverlay overlay) { _moves.Add(overlay); return this; }
        public Builder Delete(DeleteOverlay overlay) { _deletes.Add(overlay); return this; }
        public Builder PathBand(PathBandOverlay overlay) { _pathBands.Add(overlay); return this; }

        public EditPlan Build() => new(_setText, _addParagraph, _addListItem, _moves, _deletes, _pathBands);
    }
}

/// <summary>
/// Content-stream replacement instruction for a single MCID region. The <see cref="Alignment"/>
/// tells the writer whether the new text should be left-anchored (unchanged X),
/// centered in the original bbox, or right-anchored.
///
/// <see cref="GlyphBaselineY"/> carries the actual glyph Y coordinate from
/// geometry.json.pageMcidWords — the extractor's ground truth for where the source text
/// renders. Used by the writer as an authoritative baseline hint instead of the last-seen
/// Tm operator (which is stale when the source uses Td-based relative moves between BDC
/// blocks). <see cref="double.NaN"/> means "no geometry available — fall back to the
/// writer's existing heuristic".
///
/// <see cref="NextSiblingTopY"/>: PDF-space Y of the top of the closest sibling directly
/// below this MCID on the same page. NaN means "no sibling below on this page — no
/// vertical constraint". Used by shrink-to-fit.
///
/// <see cref="SourceRuns"/>: source's inline text runs (font/size/weight/colour) captured
/// by the resolver, in source order. When any run's exact text substring survives verbatim
/// in <see cref="NewContent"/>, the writer re-emits that substring with the run's original style.
/// </summary>
public sealed record SetTextOverlay(
    int Page,
    double X,
    double Y,
    double Width,
    double Height,
    string NewContent,
    FontStyle Style,
    Alignment Alignment,
    string NodeId,
    int Mcid,
    double GlyphBaselineY,
    double NextSiblingTopY,
    IReadOnlyList<TextRun> SourceRuns)
{
    /// <summary>Back-compat: no NextSiblingTopY, no source runs.</summary>
    public SetTextOverlay(int page, double x, double y, double width, double height,
        string newContent, FontStyle style, Alignment alignment, string nodeId, int mcid,
        double glyphBaselineY)
        : this(page, x, y, width, height, newContent, style, alignment, nodeId, mcid,
               glyphBaselineY, double.NaN, Array.Empty<TextRun>()) { }

    /// <summary>Back-compat: NextSiblingTopY only, no source runs.</summary>
    public SetTextOverlay(int page, double x, double y, double width, double height,
        string newContent, FontStyle style, Alignment alignment, string nodeId, int mcid,
        double glyphBaselineY, double nextSiblingTopY)
        : this(page, x, y, width, height, newContent, style, alignment, nodeId, mcid,
               glyphBaselineY, nextSiblingTopY, Array.Empty<TextRun>()) { }
}

/// <summary>
/// Instruction to draw a brand-new tagged paragraph on <see cref="Page"/> at
/// (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/>),
/// wrapped in a fresh BDC/EMC block whose MCID the writer allocates at draw-time.
///
/// <see cref="DonorMcid"/> is negative when the parent has no existing children to key
/// off of. That path is out of scope for the first cut and the engine currently rejects
/// it with a clear error.
/// </summary>
public sealed record AddParagraphOverlay(
    int Page,
    double X,
    double Y,
    double Width,
    double Height,
    string Tag,
    string NewContent,
    FontStyle Style,
    Alignment Alignment,
    string NewNodeId,
    int DonorPage,
    int DonorMcid);

/// <summary>
/// Instruction to draw a brand-new list item — an LI StructElem containing an Lbl and
/// an LBody — as a child of an existing L container.
///
/// The Lbl and LBody sit at the same Y (single-row list item), inherit X/width from the
/// donor LI's corresponding children (bullets and text stay column-aligned), and each
/// carry a fresh MCID allocated by the writer at draw-time.
///
/// <see cref="DonorLiMcid"/> points at any MCID inside the donor LI's subtree (typically
/// its Lbl or LBody). The writer walks up from that MCID's StructElem to find the L
/// container and attaches the new LI there.
/// </summary>
public sealed record AddListItemOverlay(
    int Page,
    double LblX, double LblY, double LblWidth, double LblHeight, string LabelText,
    double BodyX, double BodyY, double BodyWidth, double BodyHeight, string BodyText,
    FontStyle LblStyle,
    FontStyle BodyStyle,
    Alignment Alignment,
    string NewLiId, string NewLblId, string NewLBodyId,
    int DonorPage, int DonorLiMcid)
{
    /// <summary>Back-compat: single style used for both Lbl and LBody.</summary>
    public AddListItemOverlay(int page,
        double lblX, double lblY, double lblWidth, double lblHeight, string labelText,
        double bodyX, double bodyY, double bodyWidth, double bodyHeight, string bodyText,
        FontStyle style,
        Alignment alignment,
        string newLiId, string newLblId, string newLBodyId,
        int donorPage, int donorLiMcid)
        : this(page, lblX, lblY, lblWidth, lblHeight, labelText,
               bodyX, bodyY, bodyWidth, bodyHeight, bodyText,
               style, style, alignment,
               newLiId, newLblId, newLBodyId, donorPage, donorLiMcid) { }
}

/// <summary>
/// Instruction to shift the rendered position of an existing MCID block by (Dx, Dy).
/// The writer wraps the target BDC ... EMC pair in <c>q 1 0 0 1 dx dy cm ... Q</c> so
/// downstream siblings visually slide down to make room for a newly-inserted paragraph
/// — the §5.5 push-down rule.
///
/// The tag tree is untouched: MCIDs are preserved, so the tag-tree → content association
/// survives and screen readers continue to see each block under its original StructElem
/// parent.
/// </summary>
public sealed record MoveOverlay(int Page, int Mcid, double Dx, double Dy);

/// <summary>
/// Instruction to remove the target BDC ... EMC block from <see cref="Page"/>'s content
/// stream and prune the matching StructElem (with its MCR kid) from the copied
/// StructTreeRoot.
///
/// POC scope: the vacated page area is left empty — no pull-up of surrounding content.
/// Subsequent siblings retain their original y coordinates.
/// </summary>
public sealed record DeleteOverlay(
    int Page, int Mcid, string NodeId,
    double X, double Y, double Width, double Height)
{
    /// <summary>Back-compat: bbox unknown — all zeros. Diff overlay skips zero-area regions.</summary>
    public DeleteOverlay(int page, int mcid, string nodeId)
        : this(page, mcid, nodeId, 0, 0, 0, 0) { }
}

/// <summary>
/// Untagged-graphics push-down band. Instructs the writer to translate every path-drawing
/// op (currently: rectangles via <c>re</c>) on <see cref="Page"/> that isn't inside an
/// MCID-bearing BDC/EMC block, using this rule:
/// <list type="bullet">
///   <item>rect entirely below <see cref="BandTopY"/> (i.e., y + h ≤ bandTopY) — translate y by dy;</item>
///   <item>rect straddles bandTopY — grow the bottom by |dy| so the outer border keeps
///         enclosing the shifted content;</item>
///   <item>rect entirely above bandTopY — unchanged.</item>
/// </list>
/// </summary>
public sealed record PathBandOverlay(int Page, double BandTopY, double Dy);
