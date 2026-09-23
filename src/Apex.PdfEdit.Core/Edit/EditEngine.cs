using System.Globalization;
using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Model;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Edit;


/// <summary>
/// Applies <see cref="EditsJson"/> operations to a <see cref="DocumentJson"/>, mutating
/// the tree in place and building an <see cref="EditPlan"/> that the writer uses to stamp
/// visual overlays.
///
/// Op support:
/// <list type="bullet">
///   <item><see cref="SetTextOp"/> — fully implemented.</item>
///   <item><see cref="AddParagraphOp"/> — implemented in "no reflow" mode with push-down.</item>
///   <item><see cref="AddListItemOp"/> — implemented in "no reflow" mode with push-down.</item>
///   <item><see cref="DeleteNodeOp"/> — implemented in "no pull-up" mode: vacated visual space is left empty.</item>
/// </list>
///
/// Ops are applied top-to-bottom. Per-op failure records an <see cref="EditIssue"/>; other
/// ops still run. Deliberately more forgiving than "abort whole run".
/// </summary>
public sealed class EditEngine
{
    private readonly ILogger _log;

    private static readonly FontStyle FallbackStyle = new(null, 10.0f, "regular", "#000000");

    private static readonly HashSet<string> AddParagraphParentWhitelist = new(StringComparer.Ordinal)
    {
        "Document", "Sect", "Div", "Art", "BlockQuote", "TOC", "TOCI", "Caption", "Note"
    };

    private static readonly HashSet<string> AddParagraphTagWhitelist = new(StringComparer.Ordinal)
    {
        "P", "H1", "H2", "H3", "H4", "H5", "H6", "Lbl", "LBody", "Span"
    };

    private static readonly HashSet<string> ListItemChildTags = new(StringComparer.Ordinal) { "Lbl", "LBody" };

    private static readonly HashSet<string> ListContainerTags = new(StringComparer.Ordinal) { "L", "List" };

    private static readonly HashSet<string> AddListItemParentWhitelist = new(StringComparer.Ordinal) { "L", "List" };

    private static readonly HashSet<string> DeleteNodeTagWhitelist = new(StringComparer.Ordinal)
    {
        "P", "H1", "H2", "H3", "H4", "H5", "H6", "Lbl", "Span"
    };

    private const double DefaultLeadingMultiplier = 1.2;

    /// <summary>
    /// Must stay in sync with the stampers' AdobeTlCompensation — see rationale there.
    /// </summary>
    private const double AdobeTlCompensation = 1.02;

    private const double AvgCharAdvanceRatio = 0.55;

    private static double EffectiveLeadingMultiplier(FontStyle? style)
    {
        if (style is not null && style.LeadingRatio > 0f) return style.LeadingRatio;
        return DefaultLeadingMultiplier;
    }

    private readonly SourcePdfFontResolver? _fontResolver;

    /// <summary>
    /// When true, setText pre-flight uses the widened
    /// <see cref="SourcePdfFontResolver.FirstUnrenderableCodePointForOcrRaster"/> check.
    /// Set by the OCR edit route.
    /// </summary>
    private readonly bool _allowExtractedGlyphs;

    public EditEngine(SourcePdfFontResolver? fontResolver)
        : this(fontResolver, false, null) { }

    public EditEngine(SourcePdfFontResolver? fontResolver, bool allowExtractedGlyphs)
        : this(fontResolver, allowExtractedGlyphs, null) { }

    public EditEngine(SourcePdfFontResolver? fontResolver, bool allowExtractedGlyphs, ILogger<EditEngine>? logger)
    {
        _fontResolver = fontResolver;
        _allowExtractedGlyphs = allowExtractedGlyphs;
        _log = logger ?? NullLogger<EditEngine>.Instance;
    }

    public EditResult Apply(DocumentJson doc, EditsJson edits) => Apply(doc, null, edits);

    public EditResult Apply(DocumentJson doc, GeometryJson? geom, EditsJson edits)
    {
        var byId = new Dictionary<string, TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (n.Id is not null) byId[n.Id] = n;
        }

        var alignmentByNode = new AlignmentDetector().Detect(doc);

        var nodesByPage = new Dictionary<int, List<TreeNode>>();
        foreach (var n in doc.Tree)
        {
            if (n.Mcid < 0 || n.IsArtifact) continue;
            if (!nodesByPage.TryGetValue(n.Page, out var list))
            {
                list = new List<TreeNode>();
                nodesByPage[n.Page] = list;
            }
            list.Add(n);
        }

        var plan = EditPlan.NewBuilder();
        var applied = new List<string>();
        var issues = new List<EditIssue>();
        // nodeId -> (page, ORIGINAL bbox, cumulative dy) across every push-down op — the
        // writer pairs untagged decorations with their owner and moves them by exactly
        // the owner's total dy (band composition overshot when op chains differed).
        var decor = new Dictionary<string, (int Page, double X, double Y, double W, double H, double Dy)>();
        // Deleted nodes leave their visual space EMPTY ("no pull-up") but still occupied —
        // the box border/rule artifacts stay behind. Later adds must not land there
        // (2026 Proxy p4: a paragraph appended after a delete sat on the deleted
        // call-out box's border).
        var tombstones = new List<(string Parent, int Page, double Y)>();

        foreach (var op in edits.Operations)
        {
            try
            {
                switch (op)
                {
                    case SetTextOp s:
                        ApplySetText(s, byId, alignmentByNode, geom, nodesByPage, plan);
                        break;
                    case AddParagraphOp a:
                        ApplyAddParagraph(a, doc, geom, byId, BuildChildrenByParent(doc), plan, decor, tombstones);
                        break;
                    case AddListItemOp a:
                        ApplyAddListItem(a, doc, geom, byId, BuildChildrenByParent(doc), plan, decor, tombstones);
                        break;
                    case DeleteNodeOp d:
                        ApplyDeleteNode(d, doc, byId, BuildChildrenByParent(doc), plan, tombstones);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown op type: " + op.GetType().Name);
                }
                applied.Add(op.Id);
            }
            catch (ArgumentException e)
            {
                // Only user-facing input-validation failures become EditIssues. NRE, cast errors,
                // and internal "should not happen" InvalidOperationException guards intentionally
                // propagate so bugs aren't silently hidden as per-op warnings.
                issues.Add(new EditIssue(op.Id, op.Type, e.Message));
            }
        }

        foreach (var d in decor.Values)
        {
            plan.DecorShift(new DecorShiftOverlay(d.Page, d.X, d.Y, d.W, d.H, d.Dy));
        }

        return new EditResult(plan.Build(), applied, issues);
    }

    private void ApplySetText(SetTextOp op, Dictionary<string, TreeNode> byId,
        IReadOnlyDictionary<string, Alignment> alignmentByNode,
        GeometryJson? geom,
        Dictionary<int, List<TreeNode>> nodesByPage,
        EditPlan.Builder plan)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            throw new ArgumentException("setText.target is required");
        }
        if (op.NewContent is null)
        {
            throw new ArgumentException("setText.newContent is required");
        }
        if (!byId.TryGetValue(op.Target, out var target))
        {
            throw new ArgumentException($"target node id={op.Target} not found in document.json");
        }
        if (target.Mcid < 0)
        {
            throw new ArgumentException($"target id={op.Target} is a structural node (mcid<0); setText only applies to leaf text nodes");
        }
        if (target.IsArtifact)
        {
            throw new ArgumentException($"target id={op.Target} is an artifact; refusing to edit");
        }
        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentException($"target id={op.Target} has zero bbox; cannot stamp overlay");
        }

        CheckFontCoverage("setText.newContent", $"target id={op.Target}", target.Page, target.Mcid, op.NewContent);

        CheckPage(op.Page, target.Page, "setText", $"target id={target.Id}");

        target.Content = op.NewContent;

        var style = ResolveStyle(target.Page, target.Mcid);
        var alignment = alignmentByNode.TryGetValue(op.Target, out var a) ? a : Alignment.Left;
        double glyphBaselineY = GlyphBaselineY(geom, target.Page, target.Mcid);
        double nextSiblingTopY = NextSiblingTopBelow(nodesByPage, target);
        var sourceRuns = _fontResolver is null
            ? (IReadOnlyList<Writer.TextRun>)Array.Empty<Writer.TextRun>()
            : _fontResolver.ResolveRuns(target.Page, target.Mcid);

        plan.SetText(new SetTextOverlay(
            target.Page,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            op.NewContent,
            style,
            alignment,
            op.Target,
            target.Mcid,
            glyphBaselineY,
            nextSiblingTopY,
            sourceRuns,
            SourceLineGap(geom, target.Page, target.Mcid)));
    }

    /// <summary>
    /// Observed baseline-to-baseline gap of the source block's glyph lines, or 0 when the
    /// block is single-line or the gaps are irregular. Lets the writer re-wrap multi-line
    /// replacements at the SOURCE spacing instead of the font's natural leading (PLATO p1:
    /// a double-spaced worksheet paragraph collapsed into tight lines).
    ///
    /// Row-clustering tolerance is derived from the SMALLEST glyph height on the block
    /// rather than a fixed 1pt. Inline styling — bold-italic runs that grow the glyph bbox,
    /// superscripts, mixed sizes — shifts the bbox-bottom (which is what geometry.json's
    /// <c>Y</c> field reports) by several points WITHIN a single visual line; a 1pt cutoff
    /// mistook that shift for a new line (Feedback 1.1 TCC page 4: bold-italic "illicit
    /// discharge" at Y=517.29 vs surrounding regular text at Y=519.51 registered as 2.22pt
    /// "leading", so the writer stacked wrapped lines 2.22pt apart and rendered them on
    /// top of each other).
    /// </summary>
    private static double SourceLineGap(GeometryJson? geom, int page, int mcid)
    {
        if (geom is null) return 0;
        double minHeight = double.PositiveInfinity;
        var raw = new List<double>();
        foreach (var g in geom.GlyphsFor(page, mcid))
        {
            raw.Add(g.Y);
            if (g.Height > 0 && g.Height < minHeight) minHeight = g.Height;
        }
        if (raw.Count == 0) return 0;
        // Tolerance: half the smallest glyph height, floored at 1pt. A true new line
        // sits at least a full glyph height below the previous one; anything closer is
        // intra-line box-bottom drift from mixed fonts.
        double tol = double.IsPositiveInfinity(minHeight)
            ? 1.0
            : Math.Max(1.0, minHeight * 0.5);
        var ys = new List<double>();
        foreach (var y in raw)
        {
            bool seen = false;
            foreach (var existing in ys)
            {
                if (Math.Abs(existing - y) < tol) { seen = true; break; }
            }
            if (!seen) ys.Add(y);
        }
        if (ys.Count < 2) return 0;
        ys.Sort();
        ys.Reverse();
        double first = ys[0] - ys[1];
        double sum = 0;
        for (int i = 1; i < ys.Count; i++)
        {
            double gap = ys[i - 1] - ys[i];
            if (gap <= 0 || Math.Abs(gap - first) > 1.5) return 0;
            sum += gap;
        }
        return sum / (ys.Count - 1);
    }

    /// <summary>
    /// Annotation-backed tree nodes (form-field widgets, links) draw via page /Annots, not
    /// /Contents — a push-down must translate their annotation /Rect alongside the moved
    /// text. Emitted per shifted node (pre-shift bbox) so only annotations whose content
    /// actually moved follow; a band-wide sweep dragged unrelated TOC links (UDO p2).
    /// </summary>
    private static void AccumulateDecorShift(
        Dictionary<string, (int Page, double X, double Y, double W, double H, double Dy)> decor,
        int page, TreeNode n, double shiftAmount)
    {
        if (n.Id is null || !HasBbox(n) || !HasMcid(n)) return;
        if (decor.TryGetValue(n.Id, out var d))
        {
            decor[n.Id] = d with { Dy = d.Dy - shiftAmount };
        }
        else
        {
            // First shift of this node - n.Y is still its ORIGINAL (source-stream) position.
            decor[n.Id] = (page, n.X, n.Y, n.Width, n.Height, -shiftAmount);
        }
    }

    private static void EmitAnnotShiftIfAnnotBacked(EditPlan.Builder plan, int page, TreeNode n, double shiftAmount)
    {
        if (!HasBbox(n)) return;
        if (n.Text is not ("Form" or "Link" or "Annot" or "Widget")) return;
        plan.AnnotShift(new AnnotShiftOverlay(page, n.X, n.Y, n.Width, n.Height, -shiftAmount));
    }

    private static double NextSiblingTopBelow(Dictionary<int, List<TreeNode>> nodesByPage, TreeNode target)
    {
        if (!nodesByPage.TryGetValue(target.Page, out var onPage)) return double.NaN;
        double bestTop = double.NaN;
        foreach (var n in onPage)
        {
            if (ReferenceEquals(n, target) || string.Equals(n.Id, target.Id, StringComparison.Ordinal)) continue;
            double top = n.Y + n.Height;
            if (top > target.Y) continue;
            if (double.IsNaN(bestTop) || top > bestTop) bestTop = top;
        }
        return bestTop;
    }

    private static double GlyphBaselineY(GeometryJson? geom, int page, int mcid)
    {
        if (geom is null) return double.NaN;
        var glyphs = geom.GlyphsFor(page, mcid);
        if (glyphs.Count == 0) return double.NaN;
        return glyphs[0].Y;
    }

    private void ApplyAddParagraph(AddParagraphOp op, DocumentJson doc, GeometryJson? geom,
        Dictionary<string, TreeNode> byId,
        Dictionary<string, List<TreeNode>> childrenByParent,
        EditPlan.Builder plan,
        Dictionary<string, (int Page, double X, double Y, double W, double H, double Dy)> decor,
        List<(string Parent, int Page, double Y)> tombstones)
    {
        if (string.IsNullOrWhiteSpace(op.Parent))
        {
            throw new ArgumentException("addParagraph.parent is required");
        }
        if (op.Content is null)
        {
            throw new ArgumentException("addParagraph.content is required");
        }
        var tag = string.IsNullOrEmpty(op.Tag) ? "P" : op.Tag;
        if (!AddParagraphTagWhitelist.Contains(tag))
        {
            throw new ArgumentException(
                $"addParagraph.tag='{tag}' not in POC whitelist (P, H1-H6, Lbl, Span). Use the dedicated ops for table/list mutation.");
        }
        if (!byId.TryGetValue(op.Parent, out var parent))
        {
            throw new ArgumentException($"addParagraph.parent id={op.Parent} not found in document.json");
        }
        if (ListItemChildTags.Contains(tag))
        {
            if (!"LI".Equals(parent.Text, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"addParagraph.tag='{tag}' can only be added inside an LI, but parent id={op.Parent}" +
                    $" has tag '{parent.Text}'. Use addListItem to create a new LI + Lbl + LBody atomically, or pick an existing LI as parent.");
            }
            var listContainer = parent.Parent is null ? null
                : (byId.TryGetValue(parent.Parent, out var lc) ? lc : null);
            if (listContainer is null || !ListContainerTags.Contains(listContainer.Text ?? string.Empty))
            {
                throw new ArgumentException(
                    $"addParagraph.tag='{tag}' requires the parent LI (id={op.Parent}) to live inside an" +
                    $" L/List container, but its parent is " +
                    (listContainer is null ? "missing" : $"id={listContainer.Id} tag='{listContainer.Text}'") + ".");
            }
        }
        else if (!AddParagraphParentWhitelist.Contains(parent.Text ?? string.Empty))
        {
            throw new ArgumentException(
                $"addParagraph.parent id={op.Parent} has tag '{parent.Text}' which is not in the free-form-paragraph" +
                " parent whitelist. Table/list containers need their dedicated add ops.");
        }

        var siblings = ChildrenOf(childrenByParent, op.Parent);
        int index = op.Index;
        if (index == -1) index = siblings.Count;
        if (index < 0 || index > siblings.Count)
        {
            throw new ArgumentException(
                $"addParagraph.index={op.Index} out of range for parent id={op.Parent}" +
                $" (has {siblings.Count} children).");
        }

        var inheritFromId = op.Style?.InheritFrom;
        TreeNode? explicitDonor = inheritFromId is null ? null
            : (byId.TryGetValue(inheritFromId, out var ed) ? ed : null);
        if (inheritFromId is not null && explicitDonor is null)
        {
            throw new ArgumentException(
                $"addParagraph.style.inheritFrom id={inheritFromId} not found in document.json");
        }

        var positionDonor = NearestSibling(siblings, index, HasBbox);
        var tagTreeDonor = NearestSibling(siblings, index, HasMcid);
        var placementPrev = NearestSiblingBefore(siblings, index, HasBbox);
        var placementNext = NearestSiblingAfter(siblings, index, HasBbox);

        if (positionDonor is null)
        {
            throw new ArgumentException(
                $"addParagraph parent id={op.Parent} has no child with a resolvable bbox to inherit position from." +
                " Adding into an empty parent is out of POC scope.");
        }
        if (tagTreeDonor is null)
        {
            throw new ArgumentException(
                $"addParagraph parent id={op.Parent} has no descendant with a tagged MCID — the writer needs one to locate" +
                " the insertion point in the copied StructTreeRoot. Out of POC scope.");
        }

        var styleDonor = explicitDonor ?? tagTreeDonor ?? positionDonor;
        var style = ResolveStyle(styleDonor.Page, styleDonor.Mcid, op.Style?.Font);

        // Font-subset pre-flight against the donor that ResolveStyle actually used —
        // structural donors with mcid<0 are skipped (no font known), matching setText.
        CheckFontCoverage("addParagraph.content", $"style donor id={styleDonor.Id}",
            styleDonor.Page, styleDonor.Mcid, op.Content);

        int page = positionDonor.Page;
        CheckPage(op.Page, page, "addParagraph", $"position donor id={positionDonor.Id}");
        double x = positionDonor.X;
        double width = positionDonor.Width;
        int lineCount = EstimateWrappedLineCount(op.Content, width, style.Size);
        double naturalLineHeight = style.Size * EffectiveLeadingMultiplier(style);
        double lineHeight = naturalLineHeight * AdobeTlCompensation;
        double height = lineCount * lineHeight;
        // Inter-paragraph gap tuned to match source's Adobe "paragraph spacing after" readout.
        double paraGap = 0.58 * naturalLineHeight;
        double shiftAmount = height + paraGap;
        double y;
        bool applyPushDown;
        if (placementPrev is not null)
        {
            y = placementPrev.Y - paraGap - height;
            applyPushDown = true;
        }
        else if (placementNext is not null)
        {
            y = placementNext.Y + placementNext.Height + paraGap;
            applyPushDown = false;
        }
        else
        {
            y = positionDonor.Y - paraGap - height;
            applyPushDown = false;
        }
        // Vacated space from earlier deletes under the same parent stays reserved
        // ("no pull-up") - its border/rule artifacts are still drawn there.
        foreach (var t in tombstones)
        {
            if (string.Equals(t.Parent, op.Parent, StringComparison.Ordinal) && t.Page == page)
            {
                y = Math.Min(y, t.Y - paraGap - height);
            }
        }
        if (y < 0)
        {
            throw new ArgumentException(
                $"addParagraph would land at y={y}" +
                $" (off the bottom of page {page}). §5.5 option (ii) — pick an insertion point with room.");
        }

        var toShift = applyPushDown ? CollectShiftTargets(doc, childrenByParent, siblings, index, page) : new List<TreeNode>();
        toShift = ExcludeNodesAboveBand(toShift, y + height, geom, page);
        toShift = ExcludeNodesOutsideColumn(toShift, x, x + width, geom, page);
        toShift = ExcludeSplitLists(toShift, byId, childrenByParent, y + height, null);
        toShift = PruneShiftChain(toShift, y, shiftAmount);
        var keepOut = KeepOutRects(doc, page, toShift);
        foreach (var n in toShift)
        {
            if (HasBbox(n) && (n.Y - shiftAmount) < 0)
            {
                _log.LogWarning(
                    "addParagraph push-down places node id={Id} at y={Y} (below page {Page} edge); content still emits — Adobe editor handles the overflow.",
                    n.Id, n.Y - shiftAmount, page);
            }
        }

        CheckCollisions(doc, page, new List<Bbox> { new(x, y, width, height) }, toShift, shiftAmount);

        var newId = MintId(doc);
        var created = new TreeNode
        {
            Id = newId,
            Parent = op.Parent,
            Text = tag,
            Page = page,
            Mcid = -1,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Content = op.Content,
            Order = NextOrderForParent(siblings, index),
            IsArtifact = false,
            Status = "added"
        };

        InsertIntoTree(doc, created, siblings, index);
        byId[newId] = created;

        var alignment = ColumnAlignment(doc, positionDonor);

        plan.AddParagraph(new AddParagraphOverlay(
            page, x, y, width, height,
            tag,
            op.Content,
            style,
            alignment,
            newId,
            // tagTreeDonor: non-null by throw guard above; flow-state lost across the ??-chain assignment.
            tagTreeDonor!.Page,
            tagTreeDonor!.Mcid));

        foreach (var n in toShift)
        {
            EmitAnnotShiftIfAnnotBacked(plan, page, n, shiftAmount);
            AccumulateDecorShift(decor, page, n, shiftAmount);
            if (HasBbox(n)) n.Y -= shiftAmount;
            if (HasMcid(n))
            {
                plan.Move(new MoveOverlay(page, n.Mcid, 0.0, -shiftAmount));
            }
        }

        ShiftOrphanGeometryMcids(doc, geom, page, y + height, toShift, shiftAmount, plan);

        // No pushed-down content → no decoration shift. Emitting the band anyway drags
        // full-width artifact rects (section header fills) down while their white text
        // stays put (Bessemer p4). Diverges from Java like the exclusions above — §9.
        if (toShift.Count > 0)
        {
            var (bandLeft, bandRight) = ComputeBandSpan(toShift, geom, page, x, x + width);
            plan.PathBand(new PathBandOverlay(page, y + height, -shiftAmount, bandLeft, bandRight, keepOut));
        }
    }

    private void ApplyAddListItem(AddListItemOp op, DocumentJson doc, GeometryJson? geom,
        Dictionary<string, TreeNode> byId,
        Dictionary<string, List<TreeNode>> childrenByParent,
        EditPlan.Builder plan,
        Dictionary<string, (int Page, double X, double Y, double W, double H, double Dy)> decor,
        List<(string Parent, int Page, double Y)> tombstones)
    {
        if (string.IsNullOrWhiteSpace(op.Parent))
        {
            throw new ArgumentException("addListItem.parent is required");
        }
        if (op.LabelText is null)
        {
            throw new ArgumentException("addListItem.labelText is required");
        }
        if (op.BodyText is null)
        {
            throw new ArgumentException("addListItem.bodyText is required");
        }
        if (!byId.TryGetValue(op.Parent, out var listParent))
        {
            throw new ArgumentException($"addListItem.parent id={op.Parent} not found in document.json");
        }
        if (!AddListItemParentWhitelist.Contains(listParent.Text ?? string.Empty))
        {
            throw new ArgumentException(
                $"addListItem.parent id={op.Parent} has tag '{listParent.Text}'; only L/List containers accept a new LI.");
        }

        var allChildren = ChildrenOf(childrenByParent, op.Parent);
        var liSiblings = new List<TreeNode>();
        foreach (var c in allChildren)
        {
            if ("LI".Equals(c.Text, StringComparison.Ordinal)) liSiblings.Add(c);
        }
        if (liSiblings.Count == 0)
        {
            throw new ArgumentException(
                $"addListItem.parent id={op.Parent} has no existing LI children — the new LI's Lbl/LBody columns are inherited" +
                " from a donor. Empty-list insertion is out of POC scope.");
        }

        int index = op.Index;
        if (index == -1) index = liSiblings.Count;
        if (index < 0 || index > liSiblings.Count)
        {
            throw new ArgumentException(
                $"addListItem.index={op.Index} out of range for L id={op.Parent}" +
                $" (has {liSiblings.Count} LI children).");
        }

        var inheritFromId = op.Style?.InheritFrom;
        TreeNode? explicitDonor = inheritFromId is null ? null
            : (byId.TryGetValue(inheritFromId, out var ed) ? ed : null);
        if (inheritFromId is not null && explicitDonor is null)
        {
            throw new ArgumentException(
                $"addListItem.style.inheritFrom id={inheritFromId} not found in document.json");
        }
        var explicitDonorLi = LiOf(explicitDonor, byId);

        var donorLi = explicitDonorLi
            ?? NearestListItem(liSiblings, index, true)
            ?? NearestListItem(liSiblings, index, false);
        if (donorLi is null)
        {
            throw new InvalidOperationException("addListItem: no donor LI resolvable (this should not happen)");
        }

        // donorLi.Id: LI nodes originate from ChildrenOf which yields TreeNodes from doc.Tree;
        // real corpora always emit Id on structural LI entries — treated as invariant here.
        var donorKids = ChildrenOf(childrenByParent, donorLi.Id!);
        TreeNode? donorLbl = null, donorLBody = null;
        foreach (var k in donorKids)
        {
            if (donorLbl is null && "Lbl".Equals(k.Text, StringComparison.Ordinal)) donorLbl = k;
            else if (donorLBody is null && "LBody".Equals(k.Text, StringComparison.Ordinal)) donorLBody = k;
        }
        if (donorLbl is null || donorLBody is null)
        {
            throw new ArgumentException(
                $"addListItem donor LI id={donorLi.Id} must have both an Lbl and an LBody child to inherit column geometry from" +
                $" (found Lbl={donorLbl is not null}, LBody={donorLBody is not null}).");
        }

        var donorMcidLeaf = HasMcid(donorLBody) ? donorLBody
            : HasMcid(donorLbl) ? donorLbl : FirstMcidDescendant(childrenByParent, donorKids);
        if (donorMcidLeaf is null)
        {
            throw new ArgumentException(
                $"addListItem donor LI id={donorLi.Id} has no MCID-bearing descendant — writer can't locate the L container.");
        }

        // Fall back to donorMcidLeaf when the specific column donor lacks its own MCID
        // (e.g. structural Lbl with a leaf descendant carrying the mcid). Matches the
        // font the writer will pick in AddListItemStamper.ResolveFontFor.
        int lblFontMcid = HasMcid(donorLbl) ? donorLbl.Mcid : donorMcidLeaf.Mcid;
        int lblFontPage = HasMcid(donorLbl) ? donorLbl.Page : donorMcidLeaf.Page;
        int bodyFontMcid = HasMcid(donorLBody) ? donorLBody.Mcid : donorMcidLeaf.Mcid;
        int bodyFontPage = HasMcid(donorLBody) ? donorLBody.Page : donorMcidLeaf.Page;
        CheckFontCoverage("addListItem.labelText", $"donor Lbl id={donorLbl.Id}",
            lblFontPage, lblFontMcid, op.LabelText);
        CheckFontCoverage("addListItem.bodyText", $"donor LBody id={donorLBody.Id}",
            bodyFontPage, bodyFontMcid, op.BodyText);

        var fontOverride = op.Style?.Font;
        var lblStyle = ResolveStyle(donorLbl.Page, donorLbl.Mcid, fontOverride);
        var bodyStyle = ResolveStyle(donorLBody.Page, donorLBody.Mcid, fontOverride);
        int lblLines = EstimateWrappedLineCount(op.LabelText, donorLbl.Width, lblStyle.Size);
        int bodyLines = EstimateWrappedLineCount(op.BodyText, donorLBody.Width, bodyStyle.Size);
        double naturalBodyLineHeight = bodyStyle.Size * EffectiveLeadingMultiplier(bodyStyle);
        double bodyLineHeight = naturalBodyLineHeight * AdobeTlCompensation;
        double lblRowHeight = lblLines * lblStyle.Size * EffectiveLeadingMultiplier(lblStyle) * AdobeTlCompensation;
        double bodyRowHeight = bodyLines * bodyLineHeight;
        double height = Math.Max(lblRowHeight, bodyRowHeight);
        // Inter-LI gap mirrors addParagraph's 0.58 × lineHeight.
        double listItemGap = 0.58 * naturalBodyLineHeight;
        int page = donorLbl.Page;
        CheckPage(op.Page, page, "addListItem", $"donor LI id={donorLi.Id}");
        double donorRowBottom = Math.Min(donorLbl.Y, donorLBody.Y);
        double newY;
        bool applyPushDown;
        if (index == 0)
        {
            double topDonorTop = donorLbl.Y + Math.Max(donorLbl.Height, donorLBody.Height);
            newY = topDonorTop + listItemGap;
            applyPushDown = false;
        }
        else
        {
            var placementPrev = liSiblings[index - 1];
            // placementPrev.Id: same LI-has-Id invariant as donorLi above.
            var prevKids = ChildrenOf(childrenByParent, placementPrev.Id!);
            double prevBottom = donorRowBottom;
            foreach (var k in prevKids)
            {
                if (HasBbox(k)) prevBottom = Math.Min(prevBottom, k.Y);
            }
            newY = prevBottom - listItemGap - height;
            applyPushDown = index < liSiblings.Count;
        }
        foreach (var t in tombstones)
        {
            if (string.Equals(t.Parent, op.Parent, StringComparison.Ordinal) && t.Page == page)
            {
                newY = Math.Min(newY, t.Y - listItemGap - height);
            }
        }
        if (newY < 0)
        {
            throw new ArgumentException(
                $"addListItem would land at y={newY} (off the bottom of page {page}). §5.5 option (ii) — pick a different insertion point.");
        }

        double shiftAmount = height + listItemGap;

        var laterLiRoots = new List<TreeNode>();
        for (int i = index; i < liSiblings.Count; i++) laterLiRoots.Add(liSiblings[i]);
        var toShift = applyPushDown ? CollectShiftTargetsFromRoots(doc, childrenByParent, laterLiRoots, page) : new List<TreeNode>();
        // Append case: shift content OUTSIDE the L that follows in reading order so the source's original gap is preserved.
        if (index == liSiblings.Count && listParent.Parent is not null)
        {
            var lParentSiblings = ChildrenOf(childrenByParent, listParent.Parent);
            int lPos = -1;
            for (int i = 0; i < lParentSiblings.Count; i++)
            {
                if (string.Equals(lParentSiblings[i].Id, listParent.Id, StringComparison.Ordinal))
                {
                    lPos = i;
                    break;
                }
            }
            if (lPos >= 0 && lPos + 1 < lParentSiblings.Count)
            {
                var after = CollectShiftTargets(doc, childrenByParent, lParentSiblings, lPos + 1, page);
                var seen = new HashSet<string>();
                foreach (var n in toShift)
                {
                    if (n.Id is not null) seen.Add(n.Id);
                }
                foreach (var n in after)
                {
                    if (n.Id is not null && seen.Add(n.Id)) toShift.Add(n);
                }
            }
        }
        toShift = ExcludeNodesAboveBand(toShift, newY + height, geom, page);
        double columnLeft = Math.Min(donorLbl.X, donorLBody.X);
        double columnRight = Math.Max(donorLbl.X + donorLbl.Width, donorLBody.X + donorLBody.Width);
        toShift = ExcludeNodesOutsideColumn(toShift, columnLeft, columnRight, geom, page);
        toShift = ExcludeSplitLists(toShift, byId, childrenByParent, newY + height, listParent.Id);
        toShift = PruneShiftChain(toShift, newY, shiftAmount);
        var keepOut = KeepOutRects(doc, page, toShift);

        foreach (var n in toShift)
        {
            if (HasBbox(n) && (n.Y - shiftAmount) < 0)
            {
                _log.LogWarning(
                    "addListItem push-down places node id={Id} at y={Y} (below page {Page} edge); content still emits — Adobe editor handles the overflow.",
                    n.Id, n.Y - shiftAmount, page);
            }
        }

        CheckCollisions(doc, page, new List<Bbox>
        {
            new(donorLbl.X, newY, donorLbl.Width, height),
            new(donorLBody.X, newY, donorLBody.Width, height)
        }, toShift, shiftAmount);

        var newLiId = MintId(doc);
        var newLblId = MintIdAfter(doc, newLiId);
        var newLBodyId = MintIdAfter(doc, newLblId);

        var newLi = Structural(newLiId, op.Parent, "LI", page);
        var newLbl = Leaf(newLblId, newLiId, "Lbl", page, -1,
            donorLbl.X, newY, donorLbl.Width, height, op.LabelText);
        var newLBody = Leaf(newLBodyId, newLiId, "LBody", page, -1,
            donorLBody.X, newY, donorLBody.Width, height, op.BodyText);

        newLi.X = Math.Min(donorLbl.X, donorLBody.X);
        newLi.Y = newY;
        newLi.Width = Math.Max(donorLbl.X + donorLbl.Width, donorLBody.X + donorLBody.Width) - newLi.X;
        newLi.Height = height;

        InsertIntoTree(doc, newLi, liSiblings, index);
        int liPos = doc.Tree.IndexOf(newLi);
        doc.Tree.Insert(liPos + 1, newLbl);
        doc.Tree.Insert(liPos + 2, newLBody);

        byId[newLiId] = newLi;
        byId[newLblId] = newLbl;
        byId[newLBodyId] = newLBody;

        var alignment = ColumnAlignment(doc, donorLBody);

        plan.AddListItem(new AddListItemOverlay(
            page,
            donorLbl.X, newY, donorLbl.Width, height, op.LabelText,
            donorLBody.X, newY, donorLBody.Width, height, op.BodyText,
            lblStyle, bodyStyle, alignment,
            newLiId, newLblId, newLBodyId,
            donorMcidLeaf.Page, donorMcidLeaf.Mcid));

        foreach (var n in toShift)
        {
            EmitAnnotShiftIfAnnotBacked(plan, page, n, shiftAmount);
            AccumulateDecorShift(decor, page, n, shiftAmount);
            if (HasBbox(n)) n.Y -= shiftAmount;
            if (HasMcid(n))
            {
                plan.Move(new MoveOverlay(page, n.Mcid, 0.0, -shiftAmount));
            }
        }

        ShiftOrphanGeometryMcids(doc, geom, page, newY + height, toShift, shiftAmount, plan);

        // See ApplyAddParagraph: an empty shift chain must not emit a path band.
        if (toShift.Count > 0)
        {
            var (bandLeft, bandRight) = ComputeBandSpan(toShift, geom, page, columnLeft, columnRight);
            plan.PathBand(new PathBandOverlay(page, newY + height, -shiftAmount, bandLeft, bandRight, keepOut));
        }
    }

    /// <summary>
    /// Bboxes of every LEAF node on the page whose text is NOT moving (not in the final
    /// shift chain) — the path band must not sweep their decorations. Covers both nodes
    /// the exclusion passes dropped AND nodes that precede the insertion in reading order
    /// but sit below the band geometrically (UDO p2: left-column TOC link underlines).
    /// </summary>
    private static List<KeepOutRect> KeepOutRects(DocumentJson doc, int page, List<TreeNode> kept)
    {
        var keptIds = new HashSet<string>();
        foreach (var n in kept)
        {
            if (n.Id is not null) keptIds.Add(n.Id);
        }
        var shiftedBoxes = new List<KeepOutRect>();
        foreach (var n in kept)
        {
            if (HasBbox(n)) shiftedBoxes.Add(new KeepOutRect(n.X, n.Y, n.Width, n.Height));
        }

        var keepOut = new List<KeepOutRect>();
        foreach (var n in doc.Tree)
        {
            if (n.Page != page || !HasBbox(n) || !HasMcid(n)) continue;
            if (n.Id is not null && keptIds.Contains(n.Id)) continue;
            // A non-shifted node riding INSIDE a shifted one (a Link inside a pushed-down P)
            // must not veto the move — its decorations belong to the moving text
            // (form-40x p2: a link underline stranded mid-paragraph as a strikethrough).
            // FULL containment only: a mere centre overlap with a wide shifted paragraph
            // dropped an unshifted heading's keep-out and its pill background moved alone.
            const double slack = 2.0;
            bool insideShifted = false;
            foreach (var s in shiftedBoxes)
            {
                if (n.X >= s.X - slack && n.X + n.Width <= s.X + s.Width + slack
                    && n.Y >= s.Y - slack && n.Y + n.Height <= s.Y + s.Height + slack)
                {
                    insideShifted = true;
                    break;
                }
            }
            if (insideShifted) continue;
            keepOut.Add(new KeepOutRect(n.X, n.Y, n.Width, n.Height));
        }
        return keepOut;
    }

    private List<TreeNode> CollectShiftTargetsFromRoots(DocumentJson doc,
        Dictionary<string, List<TreeNode>> childrenByParent, List<TreeNode> roots, int page)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<TreeNode>();
        foreach (var r in roots)
        {
            if (r.Id is { } id && visited.Add(id)) queue.Enqueue(r);
        }
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Id is not { } nid) continue;
            if (!childrenByParent.TryGetValue(nid, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (kid.Id is { } kidId && visited.Add(kidId)) queue.Enqueue(kid);
            }
        }
        var output = new List<TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (n.Id is not null && visited.Contains(n.Id) && n.Page == page) output.Add(n);
        }
        return output;
    }

    private static TreeNode? NearestListItem(List<TreeNode> liSiblings, int index, bool before)
    {
        if (before)
        {
            return index > 0 ? liSiblings[index - 1] : null;
        }
        return index < liSiblings.Count ? liSiblings[index] : null;
    }

    private static TreeNode? LiOf(TreeNode? node, Dictionary<string, TreeNode> byId)
    {
        var cur = node;
        while (cur is not null)
        {
            if ("LI".Equals(cur.Text, StringComparison.Ordinal)) return cur;
            cur = cur.Parent is null ? null : (byId.TryGetValue(cur.Parent, out var p) ? p : null);
        }
        return null;
    }

    private static TreeNode? FirstMcidDescendant(
        Dictionary<string, List<TreeNode>> childrenByParent, List<TreeNode> roots)
    {
        var stack = new Stack<TreeNode>();
        foreach (var r in roots) stack.Push(r);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (HasMcid(n) && HasBbox(n)) return n;
            if (n.Id is not { } nid) continue;
            if (!childrenByParent.TryGetValue(nid, out var kids)) continue;
            foreach (var kid in kids) stack.Push(kid);
        }
        return null;
    }

    private static TreeNode Structural(string id, string parent, string tag, int page) => new()
    {
        Id = id, Parent = parent, Text = tag, Page = page, Mcid = -1, Status = "added"
    };

    private static TreeNode Leaf(string id, string parent, string tag, int page, int mcid,
        double x, double y, double w, double h, string content) => new()
    {
        Id = id, Parent = parent, Text = tag, Page = page, Mcid = mcid,
        X = x, Y = y, Width = w, Height = h, Content = content, Status = "added"
    };

    private static string MintIdAfter(DocumentJson doc, string previousId)
    {
        int start = int.TryParse(previousId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        int max = start;
        foreach (var n in doc.Tree)
        {
            if (int.TryParse(n.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
        }
        return (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyDeleteNode(DeleteNodeOp op, DocumentJson doc,
        Dictionary<string, TreeNode> byId,
        Dictionary<string, List<TreeNode>> childrenByParent,
        EditPlan.Builder plan,
        List<(string Parent, int Page, double Y)> tombstones)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            throw new ArgumentException("deleteNode.target is required");
        }
        if (!byId.TryGetValue(op.Target, out var target))
        {
            throw new ArgumentException($"deleteNode.target id={op.Target} not found in document.json");
        }
        if (target.Mcid < 0)
        {
            throw new ArgumentException(
                $"deleteNode.target id={op.Target} has no MCID — POC scope is leaf text nodes only (P, H1-H6, Lbl, Span). Deleting Sect/Document/table containers is out of scope.");
        }
        if (target.IsArtifact)
        {
            throw new ArgumentException($"deleteNode.target id={op.Target} is an artifact; refusing to delete");
        }
        if (!DeleteNodeTagWhitelist.Contains(target.Text ?? string.Empty))
        {
            throw new ArgumentException(
                $"deleteNode.target id={op.Target} has tag '{target.Text}' — POC whitelist is P, H1-H6, Lbl, Span.");
        }

        var problematicDescendants = CollectNonArtifactDescendants(doc, childrenByParent, target);
        if (problematicDescendants.Count > 0)
        {
            var summary = string.Join(", ", problematicDescendants
                .Take(3)
                .Select(n => $"id={n.Id} tag={n.Text}"));
            var more = problematicDescendants.Count > 3
                ? $" (+{problematicDescendants.Count - 3} more)" : string.Empty;
            throw new ArgumentException(
                $"deleteNode.target id={op.Target} has {problematicDescendants.Count} non-artifact descendant(s) — " +
                "pruning would orphan them in the tag tree, breaking PDF/UA. Descendants: " +
                summary + more + ". Delete the descendants first, or use setText to blank the target instead.");
        }

        CheckPage(op.Page, target.Page, "deleteNode", $"target id={target.Id}");

        plan.Delete(new DeleteOverlay(target.Page, target.Mcid, op.Target,
            target.X, target.Y, target.Width, target.Height));
        if (target.Parent is { } tp && HasBbox(target))
        {
            tombstones.Add((tp, target.Page, target.Y));
        }
        doc.Tree.Remove(target);
        byId.Remove(op.Target);
    }

    private static List<TreeNode> CollectNonArtifactDescendants(DocumentJson doc,
        Dictionary<string, List<TreeNode>> childrenByParent, TreeNode root)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<TreeNode>();
        if (root.Id is { } rootId)
        {
            visited.Add(rootId);
            queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Id is not { } nid) continue;
            if (!childrenByParent.TryGetValue(nid, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (kid.Id is { } kidId && visited.Add(kidId)) queue.Enqueue(kid);
            }
        }
        // Emit in doc.Tree order, excluding the root itself, matching the original walk.
        var output = new List<TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (ReferenceEquals(n, root)) continue;
            if (n.Id is not null && visited.Contains(n.Id) && !n.IsArtifact) output.Add(n);
        }
        return output;
    }

    private List<TreeNode> CollectShiftTargets(DocumentJson doc,
        Dictionary<string, List<TreeNode>> childrenByParent, List<TreeNode> siblings,
        int insertionIndex, int page)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<TreeNode>();
        for (int i = insertionIndex; i < siblings.Count; i++)
        {
            if (siblings[i].Id is { } id && visited.Add(id)) queue.Enqueue(siblings[i]);
        }
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Id is not { } nid) continue;
            if (!childrenByParent.TryGetValue(nid, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (kid.Id is { } kidId && visited.Add(kidId)) queue.Enqueue(kid);
            }
        }
        var output = new List<TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (n.Id is not null && visited.Contains(n.Id) && n.Page == page) output.Add(n);
        }
        return output;
    }

    private static void ShiftOrphanGeometryMcids(DocumentJson doc, GeometryJson? geom,
        int page, double newParaTopY,
        List<TreeNode> toShift, double shiftAmount,
        EditPlan.Builder plan)
    {
        if (geom?.PageMcidWords is null || toShift.Count == 0) return;
        if (!geom.PageMcidWords.TryGetValue(page.ToString(CultureInfo.InvariantCulture), out var byMcid)
            || byMcid is null || byMcid.Count == 0) return;

        var known = new HashSet<int>();
        foreach (var n in doc.Tree)
        {
            if (n.Page == page && n.Mcid >= 0) known.Add(n.Mcid);
        }

        foreach (var (key, glyphs) in byMcid)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mcid)) continue;
            if (known.Contains(mcid)) continue;
            if (glyphs is null || glyphs.Count == 0) continue;
            double gMinX = double.PositiveInfinity, gMaxX = double.NegativeInfinity;
            double gMaxY = double.NegativeInfinity;
            foreach (var g in glyphs)
            {
                if (g.X < gMinX) gMinX = g.X;
                if (g.X + g.Width > gMaxX) gMaxX = g.X + g.Width;
                if (g.Y + g.Height > gMaxY) gMaxY = g.Y + g.Height;
            }
            if (gMaxX <= gMinX || gMaxY < 0) continue;
            if (gMaxY > newParaTopY + 0.5) continue;
            double gWidth = gMaxX - gMinX;
            bool overlaps = false;
            foreach (var n in toShift)
            {
                if (!HasBbox(n) || !HasMcid(n)) continue;
                if (XOverlap(gMinX, gWidth, n.X, n.Width))
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps) continue;
            plan.Move(new MoveOverlay(page, mcid, 0.0, -shiftAmount));
        }
    }

    private static bool HasBbox(TreeNode? n) => n is not null && n.Width > 0 && n.Height > 0;

    private static bool HasMcid(TreeNode? n) => n is not null && n.Mcid >= 0;

    // Sub-point overlaps are extraction noise, not visual collisions - a donor Lbl/LBody
    // pair 0.25pt apart tripped the new-node check (ImplementationGuidelines p11).
    private const double OverlapTolerancePt = 1.0;

    private static bool XOverlap(double x1, double w1, double x2, double w2)
        => Math.Max(x1, x2) < Math.Min(x1 + w1, x2 + w2) - OverlapTolerancePt;

    private static bool YOverlap(double y1, double h1, double y2, double h2)
        => Math.Max(y1, y2) < Math.Min(y1 + h1, y2 + h2) - OverlapTolerancePt;

    private readonly record struct Bbox(double X, double Y, double W, double H);

    private static void CheckCollisions(DocumentJson doc, int page,
        List<Bbox> newBboxes,
        List<TreeNode> toShift, double shiftAmount)
    {
        var shiftedIds = new HashSet<string>();
        foreach (var n in toShift)
        {
            if (n.Id is not null) shiftedIds.Add(n.Id);
        }

        var otherLeaves = new List<TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (n.Page != page) continue;
            if (!HasBbox(n) || !HasMcid(n)) continue;
            if (n.Id is not null && shiftedIds.Contains(n.Id)) continue;
            otherLeaves.Add(n);
        }

        for (int i = 0; i < newBboxes.Count; i++)
        {
            var nb = newBboxes[i];
            foreach (var other in otherLeaves)
            {
                if (XOverlap(nb.X, nb.W, other.X, other.Width)
                    && YOverlap(nb.Y, nb.H, other.Y, other.Height))
                {
                    throw new ArgumentException(
                        $"new paragraph bbox {FmtBbox(nb.X, nb.Y, nb.W, nb.H)} on page {page}" +
                        $" collides with existing node id={other.Id} (tag={other.Text}," +
                        $" bbox={FmtBbox(other.X, other.Y, other.Width, other.Height)})." +
                        " §5.5 option (ii) — pick a different insertion point.");
                }
            }
            for (int j = i + 1; j < newBboxes.Count; j++)
            {
                var nb2 = newBboxes[j];
                if (XOverlap(nb.X, nb.W, nb2.X, nb2.W)
                    && YOverlap(nb.Y, nb.H, nb2.Y, nb2.H))
                {
                    throw new ArgumentException(
                        $"new-node bbox {FmtBbox(nb.X, nb.Y, nb.W, nb.H)} overlaps another new-node bbox" +
                        $" {FmtBbox(nb2.X, nb2.Y, nb2.W, nb2.H)} on page {page}" +
                        " — donor list-item likely has overlapping Lbl/LBody columns.");
                }
            }
        }

        foreach (var shifted in toShift)
        {
            if (!HasBbox(shifted) || !HasMcid(shifted)) continue;
            double newShiftedY = shifted.Y - shiftAmount;
            foreach (var other in otherLeaves)
            {
                if (XOverlap(shifted.X, shifted.Width, other.X, other.Width)
                    && YOverlap(newShiftedY, shifted.Height, other.Y, other.Height))
                {
                    throw new ArgumentException(
                        $"push-down of {shiftAmount.ToString("F1", CultureInfo.InvariantCulture)}pt" +
                        $" would move node id={shifted.Id} (tag={shifted.Text})" +
                        $" to y={newShiftedY.ToString("F2", CultureInfo.InvariantCulture)}" +
                        $" on page {page}, where it collides with node id=" +
                        $"{other.Id} (tag={other.Text}, y={other.Y.ToString("F2", CultureInfo.InvariantCulture)})." +
                        " §5.5 option (ii) — pick a different insertion point.");
                }
            }
        }
    }

    private static string FmtBbox(double x, double y, double w, double h)
        => string.Format(CultureInfo.InvariantCulture, "[x={0:F2}, y={1:F2}, w={2:F2}, h={3:F2}]", x, y, w, h);

    private static TreeNode? NearestSibling(List<TreeNode> siblings, int index, Func<TreeNode, bool> valid)
    {
        for (int offset = 0; offset < siblings.Count; offset++)
        {
            int prev = index - 1 - offset;
            if (prev >= 0 && prev < siblings.Count && valid(siblings[prev])) return siblings[prev];
            int next = index + offset;
            if (next >= 0 && next < siblings.Count && valid(siblings[next])) return siblings[next];
        }
        return null;
    }

    private static TreeNode? NearestSiblingBefore(List<TreeNode> siblings, int index, Func<TreeNode, bool> valid)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (valid(siblings[i])) return siblings[i];
        }
        return null;
    }

    private static TreeNode? NearestSiblingAfter(List<TreeNode> siblings, int index, Func<TreeNode, bool> valid)
    {
        for (int i = index; i < siblings.Count; i++)
        {
            if (valid(siblings[i])) return siblings[i];
        }
        return null;
    }

    private static List<TreeNode> ChildrenOf(
        Dictionary<string, List<TreeNode>> childrenByParent, string parentId)
    {
        return childrenByParent.TryGetValue(parentId, out var kids) ? kids : new List<TreeNode>();
    }

    /// <summary>
    /// One-pass build of a parent-id → children map from <see cref="DocumentJson.Tree"/>.
    /// Preserves doc-tree insertion order inside each list; applies the same Order-based
    /// sort as the old <c>ChildrenOf</c> when any sibling has a non-zero Order. Rebuild
    /// per op (ops mutate <c>doc.Tree</c> so the map goes stale after insert/delete).
    /// </summary>
    private static Dictionary<string, List<TreeNode>> BuildChildrenByParent(DocumentJson doc)
    {
        var result = new Dictionary<string, List<TreeNode>>(StringComparer.Ordinal);
        foreach (var n in doc.Tree)
        {
            if (n.Parent is not { } parent) continue;
            if (!result.TryGetValue(parent, out var list))
            {
                list = new List<TreeNode>();
                result[parent] = list;
            }
            list.Add(n);
        }
        foreach (var (_, list) in result)
        {
            bool anyOrder = false;
            foreach (var n in list)
            {
                if (n.Order > 0) { anyOrder = true; break; }
            }
            if (anyOrder) list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return result;
    }

    private static string MintId(DocumentJson doc)
    {
        int max = 0;
        foreach (var n in doc.Tree)
        {
            if (int.TryParse(n.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
        }
        return (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static int NextOrderForParent(List<TreeNode> siblings, int insertIndex)
    {
        var prev = insertIndex > 0 ? siblings[insertIndex - 1] : null;
        var next = insertIndex < siblings.Count ? siblings[insertIndex] : null;
        if (prev is not null && next is not null && next.Order - prev.Order >= 2)
        {
            return prev.Order + (next.Order - prev.Order) / 2;
        }
        int maxOrder = 0;
        foreach (var s in siblings)
        {
            if (s.Order > maxOrder) maxOrder = s.Order;
        }
        return maxOrder + 1;
    }

    private static void InsertIntoTree(DocumentJson doc, TreeNode created, List<TreeNode> siblings, int index)
    {
        int insertAt;
        if (siblings.Count == 0)
        {
            insertAt = doc.Tree.Count;
        }
        else if (index == 0)
        {
            insertAt = doc.Tree.IndexOf(siblings[0]);
        }
        else if (index >= siblings.Count)
        {
            insertAt = doc.Tree.IndexOf(siblings[^1]) + 1;
        }
        else
        {
            insertAt = doc.Tree.IndexOf(siblings[index]);
        }
        if (insertAt < 0) insertAt = doc.Tree.Count;
        doc.Tree.Insert(insertAt, created);
    }

    private static Alignment ColumnAlignment(DocumentJson doc, TreeNode? donor)
    {
        if (donor is null) return Alignment.Left;
        var pageNodes = new List<TreeNode>();
        foreach (var n in doc.Tree)
        {
            if (n.Page != donor.Page) continue;
            if (n.Mcid < 0) continue;
            if (n.IsArtifact) continue;
            if (string.IsNullOrEmpty(n.Content)) continue;
            if (n.Width <= 0) continue;
            pageNodes.Add(n);
        }
        var a = new AlignmentDetector().ClassifyInColumn(donor, pageNodes);
        return a == Alignment.Unknown ? Alignment.Left : a;
    }

    /// <summary>
    /// Drop shift candidates that sit ENTIRELY ABOVE the insertion band — the same
    /// "y ≥ bandTopY → unchanged" rule <see cref="Writer.ContentStreamPathBandShifter"/>
    /// applies to vector artwork. Reading-order collection sweeps in later-in-order
    /// content that is physically above the insert in multi-column layouts (Bessemer p2:
    /// appending a bullet in the left column collected the right column's table at the
    /// page top), shifting text whose untouched backgrounds stay put. Diverges from
    /// Java, which shifts every reading-order-later node — see PORTING_PLAN §9.
    /// </summary>
    private static List<TreeNode> ExcludeNodesAboveBand(List<TreeNode> toShift, double bandTopY,
        GeometryJson? geom, int page)
    {
        var kept = new List<TreeNode>();
        foreach (var n in toShift)
        {
            double? bottomY = null;
            if (HasBbox(n))
            {
                bottomY = n.Y;
            }
            else if (HasMcid(n) && geom is not null)
            {
                // Bbox-less tree nodes (Bessemer TD cells carry y=0/h=0) — recover the
                // bottom edge from geometry.json glyphs.
                double minY = double.PositiveInfinity;
                foreach (var g in geom.GlyphsFor(page, n.Mcid))
                {
                    if (g.Y < minY) minY = g.Y;
                }
                if (!double.IsPositiveInfinity(minY)) bottomY = minY;
            }
            if (bottomY is { } b && b >= bandTopY) continue;
            kept.Add(n);
        }
        return kept;
    }

    /// <summary>
    /// Drop shift candidates whose horizontal extent doesn't overlap the inserting
    /// column. Reading-order collection sweeps in other columns' content (Bessemer p2:
    /// a left-column bullet insert collected the right column's donut legend and
    /// distributions table), which must stay put — their untouched backgrounds don't
    /// move either. Bbox-less nodes fall back to geometry.json glyph extents; nodes
    /// with no geometry at all are kept (no evidence to exclude on).
    /// </summary>
    private static List<TreeNode> ExcludeNodesOutsideColumn(List<TreeNode> toShift,
        double columnLeft, double columnRight, GeometryJson? geom, int page)
    {
        return ExcludeNodesOutsideColumnCore(toShift, columnLeft, columnRight, geom, page);
    }

    /// <summary>
    /// Drop shift candidates whose nearest L/List ancestor also has visible items that are
    /// NOT shifting (typically excluded as above-band): shifting only the tail items tears
    /// the list apart (form-40x p2: a left-column append split the mid-column bullet list —
    /// "Give/Call" stayed while "Respond" moved down, leaving a gap and later collisions).
    /// The op's own target list is exempt — mid-list inserts legitimately move later items.
    /// </summary>
    private static List<TreeNode> ExcludeSplitLists(List<TreeNode> toShift,
        Dictionary<string, TreeNode> byId,
        Dictionary<string, List<TreeNode>> childrenByParent,
        double bandTopY, string? exemptListId)
    {
        if (toShift.Count == 0) return toShift;
        var shiftIds = new HashSet<string>();
        foreach (var n in toShift)
        {
            if (n.Id is not null) shiftIds.Add(n.Id);
        }

        string? NearestList(TreeNode n)
        {
            var cur = n;
            while (cur.Parent is { } pid && byId.TryGetValue(pid, out var p))
            {
                if (p.Text is "L" or "List") return p.Id;
                cur = p;
            }
            return null;
        }

        bool ListIsSplit(string listId)
        {
            var stack = new Stack<TreeNode>(ChildrenOf(childrenByParent, listId));
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                bool visible = HasBbox(c) && HasMcid(c);
                if (visible && (c.Id is null || !shiftIds.Contains(c.Id)))
                {
                    return true;
                }
                if (c.Id is not null)
                {
                    foreach (var g in ChildrenOf(childrenByParent, c.Id)) stack.Push(g);
                }
            }
            return false;
        }

        var splitCache = new Dictionary<string, bool>();
        var result = new List<TreeNode>();
        foreach (var n in toShift)
        {
            var listId = NearestList(n);
            if (listId is not null && !string.Equals(listId, exemptListId, StringComparison.Ordinal))
            {
                if (!splitCache.TryGetValue(listId, out bool split))
                {
                    split = ListIsSplit(listId);
                    splitCache[listId] = split;
                }
                if (split) continue;
            }
            result.Add(n);
        }
        return result;
    }

    private static List<TreeNode> ExcludeNodesOutsideColumnCore(List<TreeNode> toShift,
        double columnLeft, double columnRight, GeometryJson? geom, int page)
    {
        // Column membership is TRANSITIVE: a wide paragraph overlapping the donor column
        // (form-40x p2: a P spanning x 144-487 next to a 216-396 list) pulls its own
        // horizontal neighbours into the shift — excluding them leaves interlocked rows
        // half-shifted (collision). The span grows to a fixpoint; genuinely separate
        // columns (Bessemer p2's right-hand donut/table) never overlap and stay out.
        // Slack absorbs few-point overhangs past a column edge.
        const double slack = 15.0;
        var spans = new List<(TreeNode Node, double Left, double Right)?>();
        foreach (var n in toShift)
        {
            double? left = null, right = null;
            if (HasBbox(n))
            {
                left = n.X;
                right = n.X + n.Width;
            }
            else if (HasMcid(n) && geom is not null)
            {
                double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
                foreach (var g in geom.GlyphsFor(page, n.Mcid))
                {
                    if (g.X < minX) minX = g.X;
                    if (g.X + g.Width > maxX) maxX = g.X + g.Width;
                }
                if (!double.IsPositiveInfinity(minX)) { left = minX; right = maxX; }
            }
            spans.Add(left is { } l && right is { } r ? (n, l, r) : null);
        }

        double spanLeft = columnLeft, spanRight = columnRight;
        var inColumn = new bool[toShift.Count];
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < toShift.Count; i++)
            {
                if (inColumn[i] || spans[i] is not { } s) continue;
                if (s.Right <= spanLeft - slack || s.Left >= spanRight + slack) continue;
                inColumn[i] = true;
                changed = true;
                if (s.Left < spanLeft) spanLeft = s.Left;
                if (s.Right > spanRight) spanRight = s.Right;
            }
        }

        var kept = new List<TreeNode>();
        for (int i = 0; i < toShift.Count; i++)
        {
            // No measurable extent → keep (no evidence to exclude on).
            if (spans[i] is null || inColumn[i]) kept.Add(toShift[i]);
        }
        return kept;
    }

    /// <summary>
    /// Horizontal span the push-down band applies to: union of the inserted item's span
    /// and every surviving shift target's span, padded by a small slack so column-edge
    /// artwork (bar-chart labels drawn a few points left of the text margin) rides along.
    /// </summary>
    private static (double Left, double Right) ComputeBandSpan(List<TreeNode> toShift,
        GeometryJson? geom, int page, double itemLeft, double itemRight)
    {
        const double slack = 15.0;
        double left = itemLeft, right = itemRight;
        foreach (var n in toShift)
        {
            if (HasBbox(n))
            {
                left = Math.Min(left, n.X);
                right = Math.Max(right, n.X + n.Width);
            }
            else if (HasMcid(n) && geom is not null)
            {
                foreach (var g in geom.GlyphsFor(page, n.Mcid))
                {
                    left = Math.Min(left, g.X);
                    right = Math.Max(right, g.X + g.Width);
                }
            }
        }
        return (left - slack, right + slack);
    }

    internal static List<TreeNode> PruneShiftChain(List<TreeNode> toShift, double newParaBottomY, double shiftAmount)
    {
        if (toShift.Count == 0) return toShift;
        var visual = new List<TreeNode>();
        foreach (var n in toShift)
        {
            if (HasBbox(n) && HasMcid(n)) visual.Add(n);
        }
        if (visual.Count == 0) return toShift;
        // Walk in TOP-edge order, not bottom-edge: a tall node (Bessemer p2's 230pt
        // Figure, top 345 / bottom 114) sorted by bottom walks AFTER the small
        // references nested inside it, so the walk saw a fake 254pt gap to the
        // references' top and cut them out of the chain while keeping the Figure —
        // the Figure then shifted onto the suddenly-static references.
        visual.Sort((a, b) => (-(a.Y + a.Height)).CompareTo(-(b.Y + b.Height)));

        double prevBottom = newParaBottomY;
        double cutoffTopY = double.NegativeInfinity;
        foreach (var candidate in visual)
        {
            double candidateTop = candidate.Y + candidate.Height;
            double gap = prevBottom - candidateTop;
            if (gap >= shiftAmount)
            {
                cutoffTopY = candidateTop;
                break;
            }
            // Track the MINIMUM shifted bottom, not the last candidate's: a short Lbl
            // walked after its tall LBody sibling (949163 p1 nested list rows) would
            // otherwise RAISE prevBottom and fake a gap to the next row, cutting it
            // from the chain while the rows above still shift onto it.
            prevBottom = Math.Min(prevBottom, candidate.Y - shiftAmount);
        }
        if (double.IsNegativeInfinity(cutoffTopY)) return toShift;

        double cutoff = cutoffTopY;
        // Build byId once for the descendant-walk universe; IsDescendantOf reuses it
        // instead of rebuilding on every call (was O(len^3) → now O(len^2)).
        var byId = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (var n in toShift)
        {
            if (n.Id is { } id) byId[id] = n;
        }
        var pruned = new List<TreeNode>();
        foreach (var n in toShift)
        {
            if (HasBbox(n))
            {
                if ((n.Y + n.Height) > cutoff) pruned.Add(n);
            }
            else
            {
                bool anyAboveCutoff = false;
                foreach (var m in toShift)
                {
                    if (ReferenceEquals(m, n)) continue;
                    if (!HasBbox(m)) continue;
                    if (!IsDescendantOf(m, n, byId)) continue;
                    if ((m.Y + m.Height) > cutoff)
                    {
                        anyAboveCutoff = true;
                        break;
                    }
                }
                if (anyAboveCutoff) pruned.Add(n);
            }
        }
        return pruned;
    }

    private static bool IsDescendantOf(TreeNode candidate, TreeNode ancestor, Dictionary<string, TreeNode> byId)
    {
        var cur = candidate;
        while (cur is not null && cur.Parent is not null)
        {
            if (string.Equals(ancestor.Id, cur.Parent, StringComparison.Ordinal)) return true;
            cur = byId.TryGetValue(cur.Parent, out var p) ? p : null;
        }
        return false;
    }

    internal static int EstimateWrappedLineCount(string? content, double width, float fontSize)
    {
        if (string.IsNullOrEmpty(content)) return 1;
        double avgAdvance = Math.Max(1e-3, fontSize * AvgCharAdvanceRatio);
        int perLine = Math.Max(1, (int)Math.Floor(width / avgAdvance));
        int total = 0;
        foreach (var segment in content.Split('\n'))
        {
            int len = segment.Length;
            total += Math.Max(1, (int)Math.Ceiling((double)len / perLine));
        }
        return Math.Max(1, total);
    }

    private static void CheckPage(int? opPage, int resolvedPage, string opName, string source)
    {
        if (opPage is { } p && p != resolvedPage)
        {
            throw new ArgumentException(
                $"{opName}.page={p} does not match the resolved page {resolvedPage} (from {source})." +
                " Remove the page field or correct it to match.");
        }
    }

    /// <summary>
    /// Pre-flight guard shared by setText, addParagraph, and addListItem. Rejects
    /// text ONLY when neither the source PDF's embedded font subset at (page, mcid)
    /// NOR the writer's guaranteed-embedded system fallback (Arial-family via
    /// <see cref="SystemFontLocator.LoadUniversalFallback"/>) can render some
    /// character in <paramref name="text"/>.
    ///
    /// When the source subset misses a char but the fallback covers it, the writer's
    /// per-char <see cref="MultiFontLineEmitter"/> picks the fallback for that char
    /// — the op proceeds. A structured log line records the mixed-face rendering
    /// so the operator can trace visual font swaps.
    ///
    /// History: P1 (2026-09-04) rejected any source-subset miss to stop the silent
    /// drop the customer reported. P2 relaxes the reject once we know the writer
    /// has a real fallback — accepting the customer's addListItem now works end
    /// to end even for chars like 'X'/'Y'/brackets/braces that aren't in the source
    /// subset for a typical prose LBody.
    ///
    /// No-op when the resolver is unset, the mcid is negative (no known font), or
    /// the text is empty.
    /// </summary>
    private void CheckFontCoverage(string opField, string subjectLabel, int page, int mcid, string? text)
    {
        if (_fontResolver is null) return;
        if (string.IsNullOrEmpty(text)) return;
        if (mcid < 0) return;
        var missing = _allowExtractedGlyphs
            ? _fontResolver.FirstUnrenderableCodePointForOcrRaster(page, mcid, text)
            : _fontResolver.FirstUnrenderableCodePoint(page, mcid, text);
        if (missing is not { } cp) return;

        var style = _fontResolver.Resolve(page, mcid);
        var fallbackMiss = FirstFallbackMiss(style, text);
        if (fallbackMiss is null)
        {
            _log.LogWarning(
                "{OpField} for {Subject}: char U+{Cp:X4} ('{Char}') not in source subset " +
                "(page={Page}, mcid={Mcid}); writer will emit via embedded system fallback (mixed-face rendering).",
                opField, subjectLabel, cp, char.ConvertFromUtf32(cp), page, mcid);
            return;
        }
        int hardMissCp = fallbackMiss.Value;
        throw new ArgumentException(
            $"{opField} for {subjectLabel}" +
            $" needs character U+{hardMissCp.ToString("X4", CultureInfo.InvariantCulture)}" +
            $" ('{char.ConvertFromUtf32(hardMissCp)}'), which neither the source PDF's embedded font subset for " +
            $"(page={page}, mcid={mcid}) nor the writer's embedded system fallback can render. " +
            "Restrict the edit to characters covered by an installed system font family, or extend " +
            "the source PDF's embedded font before re-running.");
    }

    /// <summary>
    /// First code point in <paramref name="text"/> that the writer's system fallback
    /// (same face family the AddListItemStamper / AddParagraphStamper / ContentStreamMcidReplacer
    /// pick when the source subset misses a char) can't render — or null when the
    /// fallback covers everything. Loading the fallback is cheap (metadata-only read
    /// of the on-disk TTF).
    /// </summary>
    private static int? FirstFallbackMiss(FontStyle? style, string text)
    {
        var fallback = SystemFontLocator.LoadUniversalFallback(style);
        if (fallback is null)
        {
            // No system font present — treat every char as a hard miss so the pre-flight
            // still fails loud rather than letting the writer land on non-embedded stdlib.
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                if (cp != '\n' && cp != '\r' && cp != '\t') return cp;
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }
            return null;
        }
        int miss = PageFontInventory.FirstMissingCodePoint(fallback, text);
        return miss >= 0 ? miss : null;
    }

    private FontStyle ResolveStyle(int page, int mcid)
    {
        if (_fontResolver is null) return FallbackStyle;
        return _fontResolver.Resolve(page, mcid) ?? FallbackStyle;
    }

    private FontStyle ResolveStyle(int page, int mcid, FontOverride? overrideFont)
    {
        var baseStyle = ResolveStyle(page, mcid);
        if (overrideFont is null) return baseStyle;
        var family = !string.IsNullOrWhiteSpace(overrideFont.Family) ? overrideFont.Family : baseStyle.Family;
        float size = overrideFont.Size is { } sz && sz >= 4.0f ? sz : baseStyle.Size;
        var weight = !string.IsNullOrWhiteSpace(overrideFont.Weight) ? overrideFont.Weight : baseStyle.Weight;
        var colorHex = !string.IsNullOrWhiteSpace(overrideFont.ColorHex) ? overrideFont.ColorHex : baseStyle.ColorHex;
        return new FontStyle(family, size, weight, colorHex,
            baseStyle.SourceFontObjNumber, baseStyle.LeadingRatio);
    }
}
