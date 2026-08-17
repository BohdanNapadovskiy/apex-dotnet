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
                        ApplyAddParagraph(a, doc, geom, byId, BuildChildrenByParent(doc), plan);
                        break;
                    case AddListItemOp a:
                        ApplyAddListItem(a, doc, geom, byId, BuildChildrenByParent(doc), plan);
                        break;
                    case DeleteNodeOp d:
                        ApplyDeleteNode(d, doc, byId, BuildChildrenByParent(doc), plan);
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

        if (_fontResolver is not null)
        {
            var missing = _allowExtractedGlyphs
                ? _fontResolver.FirstUnrenderableCodePointForOcrRaster(target.Page, target.Mcid, op.NewContent)
                : _fontResolver.FirstUnrenderableCodePoint(target.Page, target.Mcid, op.NewContent);
            if (missing is { } cp)
            {
                throw new ArgumentException(
                    $"setText.newContent for target id={op.Target}" +
                    $" needs character U+{cp.ToString("X4", CultureInfo.InvariantCulture)}" +
                    $" ('{char.ConvertFromUtf32(cp)}'), which is not in the source PDF's embedded font subset for " +
                    $"(page={target.Page}, mcid={target.Mcid}). " +
                    "Restrict the edit to characters already used in the source, or extend " +
                    "the source PDF's embedded font before re-running.");
            }
        }

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
            sourceRuns));
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
        EditPlan.Builder plan)
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
        if (y < 0)
        {
            throw new ArgumentException(
                $"addParagraph would land at y={y}" +
                $" (off the bottom of page {page}). §5.5 option (ii) — pick an insertion point with room.");
        }

        var toShift = applyPushDown ? CollectShiftTargets(doc, childrenByParent, siblings, index, page) : new List<TreeNode>();
        toShift = PruneShiftChain(toShift, y, shiftAmount);
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
            if (HasBbox(n)) n.Y -= shiftAmount;
            if (HasMcid(n))
            {
                plan.Move(new MoveOverlay(page, n.Mcid, 0.0, -shiftAmount));
            }
        }

        ShiftOrphanGeometryMcids(doc, geom, page, y + height, toShift, shiftAmount, plan);

        plan.PathBand(new PathBandOverlay(page, y + height, -shiftAmount));
    }

    private void ApplyAddListItem(AddListItemOp op, DocumentJson doc, GeometryJson? geom,
        Dictionary<string, TreeNode> byId,
        Dictionary<string, List<TreeNode>> childrenByParent,
        EditPlan.Builder plan)
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
        toShift = PruneShiftChain(toShift, newY, shiftAmount);

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
            if (HasBbox(n)) n.Y -= shiftAmount;
            if (HasMcid(n))
            {
                plan.Move(new MoveOverlay(page, n.Mcid, 0.0, -shiftAmount));
            }
        }

        ShiftOrphanGeometryMcids(doc, geom, page, newY + height, toShift, shiftAmount, plan);

        plan.PathBand(new PathBandOverlay(page, newY + height, -shiftAmount));
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
        EditPlan.Builder plan)
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

    private static bool XOverlap(double x1, double w1, double x2, double w2)
        => Math.Max(x1, x2) < Math.Min(x1 + w1, x2 + w2);

    private static bool YOverlap(double y1, double h1, double y2, double h2)
        => Math.Max(y1, y2) < Math.Min(y1 + h1, y2 + h2);

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

    internal static List<TreeNode> PruneShiftChain(List<TreeNode> toShift, double newParaBottomY, double shiftAmount)
    {
        if (toShift.Count == 0) return toShift;
        var visual = new List<TreeNode>();
        foreach (var n in toShift)
        {
            if (HasBbox(n) && HasMcid(n)) visual.Add(n);
        }
        if (visual.Count == 0) return toShift;
        visual.Sort((a, b) => (-a.Y).CompareTo(-b.Y));

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
            prevBottom = candidate.Y - shiftAmount;
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
