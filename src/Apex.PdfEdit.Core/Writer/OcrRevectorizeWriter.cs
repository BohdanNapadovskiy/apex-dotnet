using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Model;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Tagutils;
using iText.Kernel.Pdf.Xobject;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Re-vectorises a scanned + OCR-overlaid PDF into a fresh tagged PDF.
///
/// Architecture mirrors the original OCR PDF layout (raster + invisible text) but rebuilds
/// both halves from clean sources:
/// <list type="bullet">
///   <item><b>Raster background</b> — extract the source page's largest image XObject and
///       paint it at page size so every pixel is preserved verbatim.</item>
///   <item><b>Vector text overlay</b> — walk <see cref="DocumentJson"/>'s tag tree, place
///       each MCID's glyphs from <see cref="GeometryJson"/> in the correct source-embedded
///       font, rendered invisibly (rendering mode 3) so the raster shows through unchanged.</item>
/// </list>
///
/// <b>Port note</b> — Java uses <c>java.awt.Font</c> for the source-font glyph-outline check
/// and <c>reflection</c> on <c>TagTreePointer.getCurrentStructElem</c>. This .NET port uses
/// SkiaSharp (via <see cref="SourcePdfFontResolver.SkiaTypefaceFor"/>) and .NET reflection
/// through the equivalent iText-for-.NET internal method (also PascalCase-named).
/// </summary>
public sealed class OcrRevectorizeWriter
{
    private static readonly FontStyle FallbackStyle = new(null, 10.0f, "regular", "#000000");

    private readonly ILogger _log;
    private readonly SourcePdfFontResolver _fontResolver;
    private readonly Dictionary<string, PdfFont> _fontCache = new();

    /// <summary>
    /// Source-font → destination-adopted-font map for one write pass. Source PdfFont
    /// instances are bound to the resolver's source PdfDocument; drawing them on the
    /// destination canvas requires copying the font dictionary via
    /// <see cref="PdfObject.CopyTo(PdfDocument)"/> and re-wrapping. Uses
    /// <see cref="ReferenceEqualityComparer.Instance"/> to match Java's IdentityHashMap.
    /// </summary>
    private readonly Dictionary<PdfFont, PdfFont> _adoptedSourceFonts =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Destination doc for the current write call.</summary>
    private PdfDocument? _currentDestPdf;

    /// <summary>When true, paint the vector text layer visibly instead of invisibly.</summary>
    private readonly bool _debugVisibleText;

    /// <summary>When true (default), edited/added regions are painted as rasterised images.</summary>
    private readonly bool _rasterizeEdits;

    /// <summary>Shared stamper for the current write pass.</summary>
    private readonly RasterizedTextStamper _stamper = new();

    /// <summary>Detected scan DPI per page.</summary>
    private readonly Dictionary<int, float> _scanDpiByPage = new();

    /// <summary>Per-(page, mcid) SKTypeface extracted from source's embedded FontFile.</summary>
    private readonly Dictionary<long, SKTypeface?> _sourceTypefaceByPageMcid = new();

    /// <summary>Per-setText-node word-level diff result when the diff is usable.</summary>
    private readonly Dictionary<string, WordDiffResult> _wordDiffByNodeId = new();

    /// <summary>
    /// Threshold above which a setText edit is treated as a wholesale rewrite and falls
    /// back to whole-MCID redact-and-repaint.
    /// </summary>
    private const float WordDiffChangeRatioThreshold = 0.5f;

    /// <summary>Y tolerance (points) for grouping a Replace's source words as "same line".</summary>
    private const float SameLineYTolerancePt = 6.0f;

    /// <summary>Outline stroke width (points, pre-DPI-scale) applied to per-word stamps.</summary>
    private const float WordStampStrokePt = 0.1f;

    /// <summary>Per-word stamps always load the bold face regardless of source weight.</summary>
    private const bool WordStampForceBold = true;

    /// <summary>
    /// Bleed baked into whole-MCID redaction rectangles to absorb the source's per-glyph
    /// antialiasing halo.
    /// </summary>
    private const float LegacyRedactBleedPt = 1.0f;

    /// <summary>
    /// Ratio bboxHeight/lineHeight at or below which we treat the source as single-line.
    /// </summary>
    private const float SingleLineBboxRatio = 1.5f;

    private static readonly Regex LabelPrefix = new(@"^(?:[A-Za-z]|[0-9]{1,4}|[IVXLC]{1,6})[.)]\s+",
        RegexOptions.Compiled);

    public OcrRevectorizeWriter(SourcePdfFontResolver fontResolver)
        : this(fontResolver, false, true, null) { }

    public OcrRevectorizeWriter(SourcePdfFontResolver fontResolver, bool debugVisibleText)
        : this(fontResolver, debugVisibleText, true, null) { }

    public OcrRevectorizeWriter(SourcePdfFontResolver fontResolver,
        bool debugVisibleText, bool rasterizeEdits, ILogger<OcrRevectorizeWriter>? logger = null)
    {
        _fontResolver = fontResolver;
        _debugVisibleText = debugVisibleText;
        _rasterizeEdits = rasterizeEdits;
        _log = logger ?? NullLogger<OcrRevectorizeWriter>.Instance;
    }

    public void Write(DocumentJson doc, GeometryJson geometry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(output);
        Write(doc, geometry, EditPlan.Empty(), output);
    }

    /// <summary>
    /// Edit-aware render. Applies raster passthrough + invisible vector overlay for
    /// unchanged MCIDs, but for regions covered by <paramref name="plan"/>'s overlays:
    /// <list type="bullet">
    ///   <item><b>setText</b> — white-rectangle over the source's pre-edit bbox, then
    ///       paint the mutated node's new content visibly.</item>
    ///   <item><b>deleteNode</b> — white-rectangle only; no text painted.</item>
    ///   <item><b>addParagraph / addListItem</b> — white-rectangle over the added node's
    ///       bbox, then paint the node's content visibly.</item>
    /// </list>
    /// </summary>
    public void Write(DocumentJson doc, GeometryJson geometry, EditPlan plan, Stream output)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        var source = _fontResolver.SourceDocument;
        int pageCount = Math.Max(PageCountFromDoc(doc), source.GetNumberOfPages());

        var editedMcids = EditedMcidKeys(plan);

        using var writer = new PdfWriter(output, WriterDeterminism.PropsForSource(source));
        using var pdf = new PdfDocument(writer);
        _currentDestPdf = pdf;
        _adoptedSourceFonts.Clear();
        _fontCache.Clear();
        _scanDpiByPage.Clear();
        _wordDiffByNodeId.Clear();
        _sourceTypefaceByPageMcid.Clear();
        pdf.SetTagged();

        // PDF/UA-critical catalog metadata not inherited by fresh PdfDocument.
        CopyCatalogMetadata(source, pdf);
        var sourceAttrs = SourceStructAttrIndex.Build(source);

        // Wraps + redactions must be computed AFTER _currentDestPdf is set so wrapForFit's
        // fontFor call can adopt source fonts into the dest doc.
        var wrapByNodeId = PrecomputeWraps(plan);
        PrecomputeWordDiffs(plan, geometry, wrapByNodeId);
        var redactByPage = RedactRectanglesByPage(plan, wrapByNodeId, geometry);

        for (int p = 1; p <= pageCount; p++)
        {
            var size = p <= source.GetNumberOfPages()
                ? new PageSize(source.GetPage(p).GetPageSize())
                : PageSize.LETTER;
            pdf.AddNewPage(size);
        }

        // Preamble pass: raster background + redactions per page.
        var canvasByPage = new Dictionary<int, PdfCanvas>();
        for (int p = 1; p <= pageCount; p++)
        {
            var destPage = pdf.GetPage(p);
            var canvas = new PdfCanvas(destPage);
            canvasByPage[p] = canvas;
            if (p <= source.GetNumberOfPages())
            {
                PaintRasterBackground(source.GetPage(p), destPage, canvas, pdf);
            }
            if (redactByPage.TryGetValue(p, out var rects))
            {
                PaintRedactions(canvas, rects);
            }
        }

        // Build parent → ordered-children index once.
        var childrenByParent = new Dictionary<string, IList<TreeNode>>();
        foreach (var n in doc.Tree)
        {
            var parent = string.IsNullOrEmpty(n.Parent) ? "#" : n.Parent!;
            if (!childrenByParent.TryGetValue(parent, out var list))
            {
                list = new List<TreeNode>();
                childrenByParent[parent] = list;
            }
            list.Add(n);
        }
        foreach (var list in childrenByParent.Values)
        {
            ((List<TreeNode>)list).Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        var maxSourceMcidByPage = new Dictionary<int, int>();
        foreach (var n in doc.Tree)
        {
            if (n.Mcid < 0 || n.Page < 1) continue;
            maxSourceMcidByPage.TryGetValue(n.Page, out var cur);
            maxSourceMcidByPage[n.Page] = Math.Max(cur, n.Mcid);
        }
        var nextAddedMcidByPage = new Dictionary<int, int>();

        var ctx = new WalkContext(pdf, canvasByPage, geometry, editedMcids,
            wrapByNodeId, childrenByParent, sourceAttrs,
            maxSourceMcidByPage, nextAddedMcidByPage);
        var tag = new TagTreePointer(pdf);
        if (childrenByParent.TryGetValue("#", out var roots))
        {
            foreach (var root in roots)
            {
                WalkTree(root, tag, ctx);
            }
        }

        // Link /Contents backfill.
        LinkContentsFiller.ApplyAll(pdf);
    }

    /// <summary>Copy PDF/UA-required catalog-level metadata from source to dest.</summary>
    private void CopyCatalogMetadata(PdfDocument source, PdfDocument dest)
    {
        CopyDocumentInfo(source, dest);
        CopyXmpMetadata(source, dest);
        CopyLang(source, dest);
        CopyViewerPreferences(source, dest);
    }

    private static void CopyDocumentInfo(PdfDocument source, PdfDocument dest)
    {
        var srcInfo = source.GetDocumentInfo();
        var destInfo = dest.GetDocumentInfo();
        if (srcInfo is null || destInfo is null) return;
        SetIfPresent(srcInfo.GetTitle(), destInfo.SetTitle);
        SetIfPresent(srcInfo.GetAuthor(), destInfo.SetAuthor);
        SetIfPresent(srcInfo.GetSubject(), destInfo.SetSubject);
        SetIfPresent(srcInfo.GetKeywords(), destInfo.SetKeywords);
        SetIfPresent(srcInfo.GetCreator(), destInfo.SetCreator);
    }

    private static void SetIfPresent(string? value, Func<string, PdfDocumentInfo> setter)
    {
        if (!string.IsNullOrEmpty(value)) setter(value);
    }

    private void CopyXmpMetadata(PdfDocument source, PdfDocument dest)
    {
        try
        {
            var meta = source.GetXmpMetadata();
            if (meta is not null)
            {
                dest.SetXmpMetadata(meta);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("Failed to copy XMP metadata from source: {Msg}", e.Message);
        }
    }

    private static void CopyLang(PdfDocument source, PdfDocument dest)
    {
        var srcLang = source.GetCatalog().GetPdfObject().GetAsString(PdfName.Lang);
        if (srcLang is not null)
        {
            dest.GetCatalog().SetLang(new PdfString(srcLang.ToUnicodeString()));
        }
    }

    private static void CopyViewerPreferences(PdfDocument source, PdfDocument dest)
    {
        var srcVp = source.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.ViewerPreferences);
        var destVp = new PdfViewerPreferences();
        if (srcVp is not null)
        {
            foreach (var key in srcVp.KeySet())
            {
                var value = srcVp.Get(key);
                if (value is null) continue;
                try
                {
                    destVp.Put(key, value.CopyTo(dest));
                }
                catch
                {
                    // Skip individual pref on copy failure.
                }
            }
        }
        // PDF/UA §7.1: DisplayDocTitle must be true.
        destVp.SetDisplayDocTitle(true);
        dest.GetCatalog().SetViewerPreferences(destVp);
    }

    /// <summary>DFS the source tag tree, emitting tags and drawing content per node.</summary>
    private void WalkTree(TreeNode node, TagTreePointer tag, WalkContext ctx)
    {
        if (node.IsArtifact) return;
        bool isDocumentRoot = (string.Equals(node.Parent, "#", StringComparison.Ordinal)
                               || string.IsNullOrEmpty(node.Parent))
                              && "Document".Equals(node.Text, StringComparison.Ordinal);
        bool isSyntheticDiv = "Div".Equals(node.Text, StringComparison.Ordinal);
        bool skipAddTag = isDocumentRoot || isSyntheticDiv;
        if (!skipAddTag)
        {
            var role = MapRole(node.Text);
            tag.AddTag(role);
            ApplyStructAttrs(tag, node, role, ctx);
            EmitSourceAnnotationTag(tag, node, role, ctx);
        }
        try
        {
            bool isLeaf = node.Mcid >= 0 || IsAdded(node);
            bool hasContent = !string.IsNullOrEmpty(node.Content);
            bool pageValid = node.Page >= 1 && ctx.CanvasByPage.ContainsKey(node.Page);
            bool canEmit = isLeaf && pageValid && (hasContent || node.Mcid >= 0);
            if (canEmit)
            {
                var canvas = ctx.CanvasByPage[node.Page];
                var destPage = ctx.Pdf.GetPage(node.Page);
                tag.SetPageForTagging(destPage);
                float? pageDpi = _scanDpiByPage.TryGetValue(node.Page, out var dpi) ? dpi : null;
                bool edited = _debugVisibleText || IsEdited(node, ctx.EditedMcids);
                WrapResult? wrap = edited && node.Id is not null && ctx.WrapByNodeId.TryGetValue(node.Id, out var w) ? w : null;
                bool canRasterHere = _rasterizeEdits && pageDpi is not null && wrap is not null;
                bool visible = edited && !canRasterHere;
                // Pin source's MCID; for added leaves, allocate above source's max on this page.
                int pinnedMcid;
                if (node.Mcid >= 0)
                {
                    pinnedMcid = node.Mcid;
                }
                else
                {
                    int floor = (ctx.MaxSourceMcidByPage.TryGetValue(node.Page, out var m) ? m : -1) + 1;
                    if (ctx.NextAddedMcidByPage.TryGetValue(node.Page, out var existing))
                    {
                        pinnedMcid = Math.Max(existing + 1, floor);
                    }
                    else
                    {
                        pinnedMcid = floor;
                    }
                    ctx.NextAddedMcidByPage[node.Page] = pinnedMcid;
                }
                DrawNodeContent(node, canvas, tag, ctx.Geometry, visible, wrap, pinnedMcid, destPage);
                if (canRasterHere)
                {
                    StampEditedRegion(canvas, ctx.Pdf, node, wrap!, pageDpi!.Value);
                }
            }
            if (node.Id is not null && ctx.ChildrenByParent.TryGetValue(node.Id, out var children))
            {
                foreach (var child in children)
                {
                    WalkTree(child, tag, ctx);
                }
            }
        }
        finally
        {
            if (!skipAddTag) tag.MoveToParent();
        }
    }

    /// <summary>Copy source's StructElem attrs onto the tag the pointer currently points at.</summary>
    private static void ApplyStructAttrs(TagTreePointer tag, TreeNode node, string role, WalkContext ctx)
    {
        var props = tag.GetProperties();
        if (props is null) return;
        var jsonAlt = TrimToNull(node.AltText);
        var jsonActual = TrimToNull(node.ActualText);
        var jsonLang = TrimToNull(node.Lang);
        if (jsonAlt is not null) props.SetAlternateDescription(jsonAlt);
        if (jsonActual is not null) props.SetActualText(jsonActual);
        if (jsonLang is not null) props.SetLanguage(jsonLang);

        ApplyTableAttrs(props, node, role);
        ApplyFormAttrs(props, role);

        int canonicalMcid = CanonicalMcidOf(node, ctx.ChildrenByParent);
        int page = PageOf(node, ctx.ChildrenByParent);
        if (page >= 1 && canonicalMcid >= 0)
        {
            var attrs = ctx.SourceAttrs.Lookup(page, canonicalMcid, role);
            if (attrs is not null)
            {
                if (jsonAlt is null && !string.IsNullOrEmpty(attrs.Alt))
                {
                    props.SetAlternateDescription(attrs.Alt);
                }
                if (jsonActual is null && !string.IsNullOrEmpty(attrs.ActualText))
                {
                    props.SetActualText(attrs.ActualText);
                }
                if (jsonLang is null && !string.IsNullOrEmpty(attrs.Lang))
                {
                    props.SetLanguage(attrs.Lang);
                }
            }
        }
    }

    private static string? TrimToNull(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// Emit /O /PrintField /Role /tv on Form StructElems. PDF/UA-1 §7.18.4 forbids a
    /// Form element from carrying multiple children unless it also declares a
    /// PrintField role attribute. Our revectorizer emits the OBJR plus any JSON-tree
    /// descendants under Form, so we always attach the attribute defensively.
    /// The /tv (text/value) default is a safe fallback when the widget subtype
    /// isn't known — the checker only requires that some Role be set.
    /// </summary>
    private static void ApplyFormAttrs(AccessibilityProperties props, string role)
    {
        if (!"Form".Equals(role, StringComparison.Ordinal)) return;
        var attrs = new PdfStructureAttributes("PrintField");
        attrs.AddEnumAttribute("Role", "tv");
        props.AddAttributes(attrs);
    }

    /// <summary>Emit /Scope on TH, /RowSpan / /ColSpan on cells, /Summary on Table.</summary>
    private static void ApplyTableAttrs(AccessibilityProperties props, TreeNode node, string role)
    {
        bool isTh = "TH".Equals(role, StringComparison.Ordinal);
        bool isTd = "TD".Equals(role, StringComparison.Ordinal);
        bool isTable = "Table".Equals(role, StringComparison.Ordinal);
        if (!(isTh || isTd || isTable)) return;

        var attrs = new PdfStructureAttributes("Table");
        bool any = false;

        if (isTh)
        {
            var scope = CapitaliseScope(node.Scope);
            if (scope is not null)
            {
                attrs.AddEnumAttribute("Scope", scope);
                any = true;
            }
        }
        if (isTh || isTd)
        {
            if (node.RowSpan >= 2) { attrs.AddIntAttribute("RowSpan", node.RowSpan); any = true; }
            if (node.ColSpan >= 2) { attrs.AddIntAttribute("ColSpan", node.ColSpan); any = true; }
        }
        if (isTable)
        {
            var summary = TrimToNull(node.TableSummary);
            if (summary is not null)
            {
                attrs.AddTextAttribute("Summary", summary);
                any = true;
            }
        }
        if (any) props.AddAttributes(attrs);
    }

    /// <summary>
    /// Copy the source annotation for this node onto the dest page + emit OBJR under the
    /// currently-added dest tag.
    /// </summary>
    private void EmitSourceAnnotationTag(TagTreePointer tag, TreeNode node, string role, WalkContext ctx)
    {
        if (!"Link".Equals(role, StringComparison.Ordinal) && !"Form".Equals(role, StringComparison.Ordinal)) return;

        int canonicalMcid = CanonicalMcidOf(node, ctx.ChildrenByParent);
        int page = PageOf(node, ctx.ChildrenByParent);
        if (page < 1 || canonicalMcid < 0) return;
        var srcDict = ctx.SourceAttrs.LookupAnnotation(page, canonicalMcid, role);
        if (srcDict is null) return;
        if (page > ctx.Pdf.GetNumberOfPages()) return;

        try
        {
            var copied = srcDict.CopyTo(ctx.Pdf);
            if (copied is not PdfDictionary destDict) return;
            var destPage = ctx.Pdf.GetPage(page);
            destDict.Put(PdfName.P, destPage.GetPdfObject());
            destDict.Remove(PdfName.StructParent);
            var annot = PdfAnnotation.MakeAnnotation(destDict);
            if (annot is null) return;
            destPage.AddAnnotation(-1, annot, false);
            tag.SetPageForTagging(destPage);
            tag.AddAnnotationTag(annot);
        }
        catch (Exception e)
        {
            _log.LogWarning("Failed to emit source annotation for {Role} node id={Id} on page {Page}: {Msg}",
                role, node.Id, page, e.Message);
        }
    }

    /// <summary>JSON row/column/both → PDF spec initial-cap Row/Column/Both.</summary>
    private static string? CapitaliseScope(string? raw)
    {
        if (raw is null) return null;
        var lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "row" => "Row",
            "column" => "Column",
            "both" => "Both",
            _ => null
        };
    }

    private static int CanonicalMcidOf(TreeNode node, IDictionary<string, IList<TreeNode>> childrenByParent)
    {
        if (node.Mcid >= 0) return node.Mcid;
        if (node.Id is null) return -1;
        if (!childrenByParent.TryGetValue(node.Id, out var children)) return -1;
        foreach (var child in children)
        {
            if (child.IsArtifact) continue;
            int c = CanonicalMcidOf(child, childrenByParent);
            if (c >= 0) return c;
        }
        return -1;
    }

    private static int PageOf(TreeNode node, IDictionary<string, IList<TreeNode>> childrenByParent)
    {
        if (node.Mcid >= 0) return node.Page;
        if (node.Id is null) return 0;
        if (!childrenByParent.TryGetValue(node.Id, out var children)) return 0;
        foreach (var child in children)
        {
            if (child.IsArtifact) continue;
            int p = PageOf(child, childrenByParent);
            if (p > 0) return p;
        }
        return 0;
    }

    private static bool IsAdded(TreeNode n) => "added".Equals(n.Status, StringComparison.Ordinal);

    private static bool IsEdited(TreeNode n, ISet<long> editedMcids)
    {
        if (IsAdded(n)) return true;
        if (n.Mcid < 0) return false;
        return editedMcids.Contains(McidKey(n.Page, n.Mcid));
    }

    private static long McidKey(int page, int mcid) => ((long)page << 32) | (uint)mcid;

    private static HashSet<long> EditedMcidKeys(EditPlan plan)
    {
        var set = new HashSet<long>();
        foreach (var o in plan.SetTextOverlays)
        {
            set.Add(McidKey(o.Page, o.Mcid));
        }
        return set;
    }

    /// <summary>Per-page white-rectangle regions to paint over the raster before the vector overlay.</summary>
    private Dictionary<int, List<Rectangle>> RedactRectanglesByPage(EditPlan plan,
        Dictionary<string, WrapResult> wrapByNodeId, GeometryJson? geometry)
    {
        var output = new Dictionary<int, List<Rectangle>>();
        foreach (var o in plan.SetTextOverlays)
        {
            if (_wordDiffByNodeId.TryGetValue(o.NodeId, out var diff))
            {
                // Word-diff path: emit one small redaction per changed source range.
                foreach (var seg in diff.Segments)
                {
                    if (seg is WordDiffSegment.Replace r && r.SourceRange.Count > 0)
                    {
                        var full = UnionBbox(r.SourceRange);
                        float fontSize = full.GetHeight() * 0.75f;
                        var trimmed = TrimBboxBottom(full, fontSize);
                        AddRect(output, o.Page, trimmed.GetX(), trimmed.GetY(),
                            trimmed.GetWidth(), trimmed.GetHeight());
                    }
                }
                continue;
            }
            WrapResult? wrap = wrapByNodeId.TryGetValue(o.NodeId, out var w) ? w : null;
            AddRectGrown(output, o.Page, o.X, o.Y, o.Width, o.Height, wrap);
        }
        foreach (var o in plan.DeleteOverlays)
        {
            if (o.Width <= 0 || o.Height <= 0) continue;
            var glyphs = geometry is not null ? geometry.GlyphsFor(o.Page, o.Mcid) : Array.Empty<Glyph>();
            if (glyphs.Count == 0)
            {
                AddRectGrown(output, o.Page, o.X, o.Y, o.Width, o.Height, null);
                continue;
            }
            var boxes = new List<WordBox>(glyphs.Count);
            foreach (var g in glyphs)
            {
                if (g is not null && g.Width > 0 && g.Height > 0) boxes.Add(WordBox.From(g));
            }
            if (boxes.Count == 0)
            {
                AddRectGrown(output, o.Page, o.X, o.Y, o.Width, o.Height, null);
                continue;
            }
            var tight = UnionBbox(boxes);
            var trimmed = TrimBboxBottom(tight, tight.GetHeight() * 0.75f);
            AddRect(output, o.Page, trimmed.GetX(), trimmed.GetY(),
                trimmed.GetWidth(), trimmed.GetHeight());
        }
        foreach (var o in plan.AddParagraphOverlays)
        {
            WrapResult? wrap = wrapByNodeId.TryGetValue(o.NewNodeId, out var w) ? w : null;
            AddRectGrown(output, o.Page, o.X, o.Y, o.Width, o.Height, wrap);
        }
        foreach (var o in plan.AddListItemOverlays)
        {
            WrapResult? lblWrap = wrapByNodeId.TryGetValue(o.NewLblId, out var lw) ? lw : null;
            WrapResult? bodyWrap = wrapByNodeId.TryGetValue(o.NewLBodyId, out var bw) ? bw : null;
            AddRectGrown(output, o.Page, o.LblX, o.LblY, o.LblWidth, o.LblHeight, lblWrap);
            AddRectGrown(output, o.Page, o.BodyX, o.BodyY, o.BodyWidth, o.BodyHeight, bodyWrap);
        }
        return output;
    }

    private static bool IsHeadingTag(string? tag)
    {
        if (tag is null || tag.Length != 2) return false;
        char c0 = tag[0];
        char c1 = tag[1];
        return (c0 == 'H' || c0 == 'h') && c1 >= '1' && c1 <= '6';
    }

    private static void AddRectGrown(Dictionary<int, List<Rectangle>> map, int page,
        double x, double y, double w, double h, WrapResult? wrap)
    {
        if (w <= 0 || h <= 0) return;
        float extraBelow = wrap is null ? 0f : ExtraBelowFor(y, h, wrap);
        float bleed = LegacyRedactBleedPt;
        GetOrAdd(map, page).Add(new Rectangle(
            (float)x - bleed, (float)(y - extraBelow) - bleed,
            (float)w + 2 * bleed, (float)(h + extraBelow) + 2 * bleed));
    }

    private static void AddRect(Dictionary<int, List<Rectangle>> map, int page,
        double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        GetOrAdd(map, page).Add(new Rectangle((float)x, (float)y, (float)w, (float)h));
    }

    private static List<T> GetOrAdd<T>(Dictionary<int, List<T>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }
        return list;
    }

    /// <summary>Union bbox of a run of adjacent source words on the same line.</summary>
    private static Rectangle UnionBbox(IReadOnlyList<WordBox> words)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var w in words)
        {
            if (w.X < minX) minX = w.X;
            if (w.Y < minY) minY = w.Y;
            if (w.X + w.Width > maxX) maxX = w.X + w.Width;
            if (w.Y + w.Height > maxY) maxY = w.Y + w.Height;
        }
        return new Rectangle((float)minX, (float)minY,
            (float)(maxX - minX), (float)(maxY - minY));
    }

    /// <summary>Return bbox shortened from the bottom so its height doesn't exceed fontSize × 0.9.</summary>
    private static Rectangle TrimBboxBottom(Rectangle bbox, float fontSize)
    {
        float maxHeight = fontSize * 0.9f;
        if (bbox.GetHeight() <= maxHeight) return bbox;
        float trim = bbox.GetHeight() - maxHeight;
        return new Rectangle(bbox.GetX(), bbox.GetY() + trim, bbox.GetWidth(), maxHeight);
    }

    private static float ExtraBelowFor(double y, double h, WrapResult wrap)
    {
        float firstBaseline = FirstBaselineFor(y, h, wrap);
        float lastLineBottom = firstBaseline - (wrap.Lines.Count - 1) * wrap.LineHeight
                               - wrap.FontSize * 0.2f;
        return Math.Max(0f, (float)(y - lastLineBottom));
    }

    private static float FirstBaselineFor(double y, double h, WrapResult wrap)
    {
        float ascent = wrap.FontSize * 0.8f;
        return (float)(y + h - ascent);
    }

    /// <summary>Greedy word wrap of content to bboxWidth using the resolved font/size.</summary>
    private WrapResult WrapForFit(string? content, FontStyle? style, double bboxWidth,
        int sourcePage, int sourceMcid, Alignment alignment)
    {
        var effective = style ?? FallbackStyle;
        var text = content ?? string.Empty;
        var font = FontFor(effective, sourcePage, sourceMcid, text);
        float fontSize = effective.Size >= 4.0f ? effective.Size : 10.0f;
        float leadingRatio = effective.LeadingRatio > 0f ? effective.LeadingRatio : 1.2f;
        float lineHeight = fontSize * leadingRatio;
        float maxWidth = (float)bboxWidth;

        if (maxWidth <= 0 || text.Length == 0)
        {
            return new WrapResult(new[] { text }, fontSize, lineHeight, font, alignment);
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.GetWidth(candidate, fontSize) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            var remaining = word;
            while (font.GetWidth(remaining, fontSize) > maxWidth && remaining.Length > 1)
            {
                int fit = 1;
                while (fit < remaining.Length
                       && font.GetWidth(remaining[..(fit + 1)], fontSize) <= maxWidth)
                {
                    fit++;
                }
                lines.Add(remaining[..fit]);
                remaining = remaining[fit..];
            }
            current.Append(remaining);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add(string.Empty);
        return new WrapResult(lines, fontSize, lineHeight, font, alignment);
    }

    private WrapResult CollapseSingleLineOverflow(WrapResult wrap, string? content,
        double bboxHeight, string nodeId)
    {
        if (string.IsNullOrEmpty(content)) return wrap;
        if (wrap.Lines.Count <= 1) return wrap;
        if (wrap.LineHeight <= 0) return wrap;
        bool sourceSingleLine = bboxHeight < wrap.LineHeight * SingleLineBboxRatio;
        if (!sourceSingleLine) return wrap;
        _log.LogInformation(
            "setText node {NodeId}: source is single-line (bbox {H} < {R} × lineHeight {LH}) — collapsing {Have} wrapped lines to one at {Size}pt",
            nodeId,
            bboxHeight.ToString("F2", CultureInfo.InvariantCulture),
            SingleLineBboxRatio,
            wrap.LineHeight.ToString("F2", CultureInfo.InvariantCulture),
            wrap.Lines.Count,
            wrap.FontSize.ToString("F2", CultureInfo.InvariantCulture));
        return new WrapResult(new[] { content }, wrap.FontSize, wrap.LineHeight,
            wrap.Font, wrap.Alignment);
    }

    /// <summary>For each setText overlay, decide whether a word-level diff is usable.</summary>
    private void PrecomputeWordDiffs(EditPlan plan, GeometryJson? geometry,
        Dictionary<string, WrapResult> wrapByNodeId)
    {
        if (geometry is null) return;
        foreach (var o in plan.SetTextOverlays)
        {
            if (o.Mcid < 0) continue;
            var src = geometry.GlyphsFor(o.Page, o.Mcid);
            if (src.Count == 0) continue;
            var diff = WordLevelDiff.Diff(src, o.NewContent);
            if (diff.ChangeRatio > WordDiffChangeRatioThreshold) continue;
            if (!wrapByNodeId.TryGetValue(o.NodeId, out var wrap)) continue;
            if (!AllReplacesFit(diff, wrap)) continue;
            _wordDiffByNodeId[o.NodeId] = diff;
        }
    }

    private static bool AllReplacesFit(WordDiffResult diff, WrapResult wrap)
    {
        var font = wrap.Font;
        foreach (var seg in diff.Segments)
        {
            if (seg is not WordDiffSegment.Replace r) continue;
            if (r.SourceRange.Count == 0) return false;
            double firstY = r.SourceRange[0].Y;
            foreach (var w in r.SourceRange)
            {
                if (Math.Abs(w.Y - firstY) > SameLineYTolerancePt) return false;
            }
            if (r.NewRange.Count == 0) continue;
            var joined = string.Join(" ", r.NewRange);
            var bbox = UnionBbox(r.SourceRange);
            float stampFontSize = bbox.GetHeight() * 0.75f;
            float measured = font.GetWidth(joined, stampFontSize);
            if (measured > bbox.GetWidth() * 1.05f) return false;
        }
        return true;
    }

    /// <summary>Precompute per-edited-node wrap layouts up front.</summary>
    private Dictionary<string, WrapResult> PrecomputeWraps(EditPlan plan)
    {
        var output = new Dictionary<string, WrapResult>();
        foreach (var o in plan.SetTextOverlays)
        {
            var wrap = WrapForFit(o.NewContent, o.Style, o.Width,
                o.Page, o.Mcid, o.Alignment);
            wrap = CollapseSingleLineOverflow(wrap, o.NewContent, o.Height, o.NodeId);
            output[o.NodeId] = wrap;
        }
        foreach (var o in plan.AddParagraphOverlays)
        {
            output[o.NewNodeId] = WrapForFit(o.NewContent, o.Style, o.Width,
                o.DonorPage, o.DonorMcid, o.Alignment);
        }
        foreach (var o in plan.AddListItemOverlays)
        {
            output[o.NewLblId] = WrapForFit(o.LabelText, o.LblStyle, o.LblWidth,
                o.DonorPage, o.DonorLiMcid, o.Alignment);
            output[o.NewLBodyId] = WrapForFit(o.BodyText, o.BodyStyle, o.BodyWidth,
                o.DonorPage, o.DonorLiMcid, o.Alignment);
        }
        return output;
    }

    /// <summary>Paint solid-white rectangles over the raster to hide source pixels.</summary>
    private static void PaintRedactions(PdfCanvas canvas, IReadOnlyList<Rectangle> rects)
    {
        if (rects.Count == 0) return;
        // Redaction rectangles are pure visual — screen readers must skip them.
        canvas.OpenTag(new CanvasArtifact());
        canvas.SaveState();
        canvas.SetFillColor(DeviceGray.WHITE);
        foreach (var r in rects)
        {
            canvas.Rectangle(r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight());
            canvas.Fill();
        }
        canvas.RestoreState();
        canvas.CloseTag();
    }

    /// <summary>Copy the source page's largest image XObject and paint it at dest page size.</summary>
    private void PaintRasterBackground(PdfPage srcPage, PdfPage destPage,
        PdfCanvas canvas, PdfDocument destPdf)
    {
        var res = srcPage.GetResources();
        var xobjectNames = res.GetResourceNames(PdfName.XObject);
        if (xobjectNames is null || xobjectNames.Count == 0) return;

        PdfImageXObject? best = null;
        long bestArea = -1;
        foreach (var name in xobjectNames)
        {
            var img = res.GetImage(name);
            if (img is null) continue;
            long area = (long)img.GetWidth() * (long)img.GetHeight();
            if (area > bestArea)
            {
                bestArea = area;
                best = img;
            }
        }
        if (best is null) return;

        try
        {
            var copy = best.CopyTo(destPdf);
            var bbox = destPage.GetPageSize();
            // Wrap in /Artifact BMC/EMC — image is decorative per PDF/UA.
            canvas.OpenTag(new CanvasArtifact());
            canvas.AddXObjectFittedIntoRectangle(copy, bbox);
            canvas.CloseTag();
            float dpiX = best.GetWidth() / (bbox.GetWidth() / 72f);
            float dpiY = best.GetHeight() / (bbox.GetHeight() / 72f);
            float dpi = Math.Min(dpiX, dpiY);
            if (dpi >= 72f && dpi <= 1200f)
            {
                _scanDpiByPage[destPdf.GetPageNumber(destPage)] = dpi;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("Failed to passthrough raster on page {Page}: {Msg}",
                srcPage.GetDocument().GetPageNumber(srcPage), e.Message);
        }
    }

    /// <summary>Rasterise the wrapped edit content at pageDpi and paint over the node's bbox.</summary>
    private void StampEditedRegion(PdfCanvas canvas, PdfDocument destPdf, TreeNode node,
        WrapResult wrap, float pageDpi)
    {
        var style = ResolveStampStyle(node, wrap);
        if (node.Id is not null && _wordDiffByNodeId.TryGetValue(node.Id, out var diff))
        {
            try
            {
                canvas.OpenTag(new CanvasArtifact());
                StampChangedSpans(canvas, destPdf, node, diff, style, pageDpi);
                canvas.CloseTag();
            }
            catch (Exception e)
            {
                _log.LogWarning("Per-word stamp on page {Page} for node {Id} failed: {Msg}",
                    node.Page, node.Id, e.Message);
            }
            return;
        }
        float extraBelow = ExtraBelowFor(node.Y, node.Height, wrap);
        float rasterWidth = (float)node.Width;
        if (wrap.Lines.Count == 1)
        {
            float measured = wrap.Font.GetWidth(wrap.Lines[0], wrap.FontSize);
            if (measured > rasterWidth) rasterWidth = measured;
        }
        var bbox = new Rectangle(
            (float)node.X,
            (float)(node.Y - extraBelow),
            rasterWidth,
            (float)(node.Height + extraBelow));
        bool isHeading = IsHeadingTag(node.Text);
        try
        {
            canvas.OpenTag(new CanvasArtifact());
            if (isHeading)
            {
                _stamper.StampOnto(canvas, bbox, wrap.Lines, wrap.FontSize, wrap.LineHeight,
                    style, pageDpi, wrap.Alignment, true, 0.3f);
                DrawHeadingUnderline(canvas, bbox, wrap, node.Text);
            }
            else
            {
                _stamper.StampOnto(canvas, bbox, wrap.Lines, wrap.FontSize, wrap.LineHeight,
                    style, pageDpi, wrap.Alignment);
            }
            canvas.CloseTag();
        }
        catch (Exception e)
        {
            _log.LogWarning("Rasterised stamp on page {Page} for node {Id} failed: {Msg}",
                node.Page, node.Id, e.Message);
        }
    }

    /// <summary>Draw a thin black rectangle just below the stamped text's baseline.</summary>
    private static void DrawHeadingUnderline(PdfCanvas canvas, Rectangle bbox, WrapResult wrap, string? tag)
    {
        float fontSize = wrap.FontSize;
        float baselineY = bbox.GetY() + bbox.GetHeight() - fontSize * 0.8f;
        float thickness = Math.Max(0.5f, fontSize / 14f);

        float lineWidth = bbox.GetWidth();
        string? firstLine = wrap.Lines.Count > 0 ? wrap.Lines[0] : null;
        if (firstLine is not null)
        {
            float measured = wrap.Font.GetWidth(firstLine, fontSize);
            if (measured > 0f) lineWidth = measured;
        }

        float leftOffset = AlignmentLeftOffset(bbox.GetWidth(), lineWidth, wrap.Alignment);
        float labelWidth = firstLine is not null
            ? LeadingLabelWidth(firstLine, wrap.Font, fontSize) : 0f;
        if (labelWidth >= lineWidth) labelWidth = 0f;

        float xStart = bbox.GetX() + leftOffset + labelWidth;
        float ruleWidth = lineWidth - labelWidth;

        canvas.SaveState();
        canvas.SetFillColor(DeviceGray.BLACK);
        float rule1Y = baselineY - fontSize * 0.20f;
        canvas.Rectangle(xStart, rule1Y - thickness, ruleWidth, thickness);
        canvas.Fill();
        if (IsTopLevelHeading(tag))
        {
            float rule2Y = rule1Y - Math.Max(1.2f, fontSize * 0.12f);
            canvas.Rectangle(xStart, rule2Y - thickness, ruleWidth, thickness);
            canvas.Fill();
        }
        canvas.RestoreState();
    }

    private static bool IsTopLevelHeading(string? tag)
        => "H1".Equals(tag, StringComparison.OrdinalIgnoreCase)
           || "H2".Equals(tag, StringComparison.OrdinalIgnoreCase);

    private static float AlignmentLeftOffset(float bboxWidth, float lineWidth, Alignment alignment)
    {
        if (lineWidth >= bboxWidth) return 0f;
        return alignment switch
        {
            Alignment.Right => Math.Max(0f, bboxWidth - lineWidth),
            Alignment.Center => Math.Max(0f, (bboxWidth - lineWidth) / 2f),
            _ => 0f
        };
    }

    private static float LeadingLabelWidth(string line, PdfFont font, float fontSize)
    {
        if (string.IsNullOrEmpty(line)) return 0f;
        var m = LabelPrefix.Match(line);
        if (!m.Success) return 0f;
        return font.GetWidth(m.Value, fontSize);
    }

    /// <summary>Paint changed-word spans using source's own extracted glyph outlines.</summary>
    private void StampChangedSpans(PdfCanvas canvas, PdfDocument destPdf, TreeNode node,
        WordDiffResult diff, FontStyle style, float pageDpi)
    {
        var sourceTypeface = ExtractSourceTypeface(node.Page, node.Mcid);
        foreach (var seg in diff.Segments)
        {
            if (seg is not WordDiffSegment.Replace r) continue;
            if (r.SourceRange.Count == 0 || r.NewRange.Count == 0) continue;
            var fullBbox = UnionBbox(r.SourceRange);
            var joined = string.Join(" ", r.NewRange);
            float sourceFontSize = fullBbox.GetHeight() * 0.75f;
            float sourceLineHeight = sourceFontSize * 1.2f;
            var bbox = TrimBboxBottom(fullBbox, sourceFontSize);
            // Preferred path: use the source PDF's own embedded font outlines.
            if (sourceTypeface is not null && CanRenderWithTypeface(sourceTypeface, joined))
            {
                var skColor = ParseHexToSkia(style.ColorHex);
                _stamper.StampOntoWithFont(canvas, bbox, new[] { joined },
                    sourceFontSize, sourceLineHeight,
                    sourceTypeface, skColor, pageDpi, Alignment.Left,
                    WordStampStrokePt);
            }
            else
            {
                _stamper.StampOnto(canvas, bbox, new[] { joined },
                    sourceFontSize, sourceLineHeight,
                    style, pageDpi, Alignment.Left,
                    WordStampForceBold, WordStampStrokePt);
            }
        }
    }

    /// <summary>Parse #rrggbb hex string into an SKColor.</summary>
    private static SKColor ParseHexToSkia(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#') return SKColors.Black;
        try
        {
            byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new SKColor(r, g, b);
        }
        catch (FormatException)
        {
            return SKColors.Black;
        }
    }

    /// <summary>Compute the leading-edge X for a wrapped line inside a bbox.</summary>
    internal static float AlignedStartX(double nodeX, double nodeWidth, Alignment alignment, float lineWidth)
    {
        float left = (float)nodeX;
        float width = (float)nodeWidth;
        return alignment switch
        {
            Alignment.Right => Math.Max(left, left + width - lineWidth),
            Alignment.Center => Math.Max(left, left + (width - lineWidth) / 2f),
            _ => left
        };
    }

    /// <summary>Pick a FontStyle for the raster stamper.</summary>
    private FontStyle ResolveStampStyle(TreeNode node, WrapResult wrap)
    {
        var style = _fontResolver.Resolve(node.Page, node.Mcid) ?? FallbackStyle;
        var detected = DetectSourceFamily(node.Page, node.Mcid);
        if (detected is null)
        {
            detected = DetectFamilyFromPdfFont(wrap.Font);
        }
        if (detected is null) return style;
        return new FontStyle(detected, style.Size, style.Weight,
            style.ColorHex, style.SourceFontObjNumber, style.LeadingRatio);
    }

    /// <summary>Pull source's embedded font-program bytes via <see cref="SourcePdfFontResolver.SkiaTypefaceFor"/>.</summary>
    private SKTypeface? ExtractSourceTypeface(int page, int mcid)
    {
        long key = McidKey(page, mcid);
        if (_sourceTypefaceByPageMcid.TryGetValue(key, out var cached)) return cached;
        var srcFont = _fontResolver.SourceFontFor(page, mcid);
        var result = srcFont is null ? null : _fontResolver.SkiaTypefaceFor(srcFont);
        _sourceTypefaceByPageMcid[key] = result;
        return result;
    }

    /// <summary>
    /// SkiaSharp equivalent of Java's AWT-outline canRender. Every non-whitespace char
    /// must have a non-zero glyph ID whose path is non-empty.
    /// </summary>
    private static bool CanRenderWithTypeface(SKTypeface typeface, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        using var font = new SKFont(typeface);
        Span<int> cpBuf = stackalloc int[1];
        Span<ushort> glyphOut = stackalloc ushort[1];
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(rune)) continue;
            cpBuf[0] = rune.Value;
            font.GetGlyphs(cpBuf, glyphOut);
            if (glyphOut[0] == 0) return false;
            using var path = font.GetGlyphPath(glyphOut[0]);
            if (path is null || path.IsEmpty) return false;
        }
        return true;
    }

    private string? DetectSourceFamily(int page, int mcid)
    {
        var src = _fontResolver.SourceFontFor(page, mcid);
        return src is null ? null : DetectFamilyFromPdfFont(src);
    }

    private static string? DetectFamilyFromPdfFont(PdfFont? font)
    {
        if (font is null) return null;
        var program = font.GetFontProgram();
        if (program is null) return null;
        var names = program.GetFontNames();
        if (names is null) return null;
        var candidate = names.GetFontName();
        if (string.IsNullOrEmpty(candidate))
        {
            var full = names.GetFullName();
            if (full is not null && full.Length > 0 && full[0].Length > 3)
            {
                candidate = full[0][3];
            }
        }
        return FamilyFromFontName(candidate);
    }

    private static string? FamilyFromFontName(string? candidate)
    {
        if (candidate is null) return null;
        var lower = candidate.ToLowerInvariant();
        if (lower.Contains("courier", StringComparison.Ordinal)
            || lower.Contains("cour", StringComparison.Ordinal)
            || lower.Contains("mono", StringComparison.Ordinal)
            || lower.Contains("consolas", StringComparison.Ordinal)
            || lower.Contains("typewriter", StringComparison.Ordinal))
        {
            return "Courier";
        }
        if (lower.Contains("times", StringComparison.Ordinal)
            || lower.Contains("roman", StringComparison.Ordinal)
            || lower.Contains("serif", StringComparison.Ordinal)
            || lower.Contains("cambria", StringComparison.Ordinal))
        {
            return "Times";
        }
        if (lower.Contains("arial", StringComparison.Ordinal)
            || lower.Contains("helvetica", StringComparison.Ordinal)
            || lower.Contains("sans", StringComparison.Ordinal))
        {
            return "Arial";
        }
        return null;
    }

    /// <summary>Paint an MCID leaf's content onto canvas under the currently-pointed tag.</summary>
    private void DrawNodeContent(TreeNode node, PdfCanvas canvas, TagTreePointer tag,
        GeometryJson? geometry, bool visible, WrapResult? wrap,
        int pinnedMcid, PdfPage destPage)
    {
        var style = _fontResolver.Resolve(node.Page, node.Mcid) ?? FallbackStyle;
        // Prefer live per-glyph geometry for unedited nodes.
        IReadOnlyList<Glyph> glyphs = (wrap is not null || geometry is null)
            ? Array.Empty<Glyph>()
            : geometry.GlyphsFor(node.Page, node.Mcid);

        var font = wrap?.Font ?? FontFor(style, node.Page, node.Mcid, node.Content);
        var color = ParseHex(style.ColorHex);
        float fontSize = wrap?.FontSize ?? EffectiveSize(style, node);

        // Pin MCID via reflection into TagTreePointer.GetCurrentStructElem.
        OpenPinnedMarkedContent(canvas, tag, pinnedMcid, destPage);
        canvas.SaveState();
        canvas.SetFillColor(color);
        canvas.BeginText();
        canvas.SetFontAndSize(font, fontSize);
        if (!visible)
        {
            canvas.SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
        }

        if (glyphs.Count > 0)
        {
            foreach (var g in glyphs)
            {
                if (g.Text is null) continue;
                float baselineY = (float)(g.Y + g.Height * 0.4);
                canvas.SetTextMatrix((float)g.X, baselineY);
                canvas.ShowText(g.Text);
            }
        }
        else if (wrap is not null)
        {
            float baseline = FirstBaselineFor(node.Y, node.Height, wrap);
            foreach (var line in wrap.Lines)
            {
                float lineWidth = font.GetWidth(line, fontSize);
                float startX = AlignedStartX(node.X, node.Width, wrap.Alignment, lineWidth);
                canvas.SetTextMatrix(startX, baseline);
                canvas.ShowText(line);
                baseline -= wrap.LineHeight;
            }
        }
        else if (!string.IsNullOrEmpty(node.Content))
        {
            float baseline = (float)(node.Y + node.Height * 0.15);
            canvas.SetTextMatrix((float)node.X, baseline);
            canvas.ShowText(node.Content);
        }

        canvas.EndText();
        canvas.RestoreState();
        canvas.EndMarkedContent();
    }

    /// <summary>
    /// Attach a <see cref="PdfMcrDictionary"/> pinned to pinnedMcid to the current
    /// StructElem, then open a matching BDC on the canvas. Uses reflection because
    /// <c>TagTreePointer.GetCurrentStructElem</c> is internal.
    /// </summary>
    private static void OpenPinnedMarkedContent(PdfCanvas canvas, TagTreePointer tag,
        int pinnedMcid, PdfPage destPage)
    {
        var elem = CurrentStructElem(tag);
        var mcrDict = new PdfDictionary();
        mcrDict.Put(PdfName.Type, PdfName.MCR);
        mcrDict.Put(PdfName.Pg, destPage.GetPdfObject().GetIndirectReference());
        mcrDict.Put(PdfName.MCID, new PdfNumber(pinnedMcid));
        var mcr = new PdfMcrDictionary(mcrDict, elem);
        elem.AddKid(mcr);
        var props = new PdfDictionary();
        props.Put(PdfName.MCID, new PdfNumber(pinnedMcid));
        canvas.BeginMarkedContent(elem.GetRole(), props);
    }

    private static PdfStructElem CurrentStructElem(TagTreePointer tag)
    {
        var m = typeof(TagTreePointer).GetMethod("GetCurrentStructElem",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (m is null)
        {
            throw new InvalidOperationException(
                "iText's TagTreePointer.GetCurrentStructElem() is no longer accessible via reflection — " +
                "the MCID pinning path in OcrRevectorizeWriter needs updating.");
        }
        try
        {
            var result = m.Invoke(tag, null);
            return (PdfStructElem)result!;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "iText's TagTreePointer.GetCurrentStructElem() invocation failed.", e);
        }
    }

    private static float EffectiveSize(FontStyle style, TreeNode node)
    {
        if (style.Size >= 4.0f) return style.Size;
        double approx = node.Height > 0 ? node.Height * 0.85 : 10.0;
        if (approx < 5.0) approx = 5.0;
        if (approx > 72.0) approx = 72.0;
        return (float)approx;
    }

    /// <summary>Pick the PdfFont for a given style, preferring adopted source font → system → standard-14.</summary>
    private PdfFont FontFor(FontStyle style, int sourcePage, int sourceMcid, string? content)
    {
        if (_currentDestPdf is not null && sourceMcid >= 0)
        {
            var src = _fontResolver.SourceFontFor(sourcePage, sourceMcid);
            if (src is not null)
            {
                bool canRender = content is null
                    || _fontResolver.FirstUnrenderableCodePoint(sourcePage, sourceMcid, content) is null;
                if (canRender)
                {
                    var adopted = AdoptSourceFont(src);
                    if (adopted is not null) return adopted;
                }
            }
        }
        var effective = style;
        if (sourceMcid >= 0)
        {
            var detected = DetectSourceFamily(sourcePage, sourceMcid);
            if (detected is not null)
            {
                effective = new FontStyle(detected, style.Size, style.Weight,
                    style.ColorHex, style.SourceFontObjNumber, style.LeadingRatio);
            }
        }
        var cacheKey = (effective.Family ?? "-") + "/" + (effective.Weight ?? "regular");
        if (_fontCache.TryGetValue(cacheKey, out var cached)) return cached;
        var systemFont = SystemFontLocator.LoadUniversalFallback(effective);
        if (systemFont is not null)
        {
            _fontCache[cacheKey] = systemFont;
            return systemFont;
        }
        var std14 = PdfFontFactory.CreateFont(StandardFontMapper.MapToStandardFont(effective));
        _fontCache[cacheKey] = std14;
        return std14;
    }

    /// <summary>Adopt a source-doc-bound PdfFont into <see cref="_currentDestPdf"/> via copyTo.</summary>
    private PdfFont? AdoptSourceFont(PdfFont src)
    {
        if (_currentDestPdf is null) return null;
        if (_adoptedSourceFonts.TryGetValue(src, out var cached)) return cached;
        try
        {
            var obj = src.GetPdfObject();
            if (obj is null) return null;
            var copy = obj.CopyTo(_currentDestPdf);
            if (copy is not PdfDictionary dict) return null;
            var adopted = PdfFontFactory.CreateFont(dict);
            _adoptedSourceFonts[src] = adopted;
            return adopted;
        }
        catch (Exception e)
        {
            var name = src.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "?";
            _log.LogWarning("Failed to adopt source font {Name} into destination doc: {Msg}",
                name, e.Message);
            return null;
        }
    }

    private static Color ParseHex(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return new DeviceRgb(0, 0, 0);
        }
        try
        {
            int r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new DeviceRgb(r, g, b);
        }
        catch (FormatException)
        {
            return new DeviceRgb(0, 0, 0);
        }
    }

    private static string MapRole(string? jsonRole) => jsonRole switch
    {
        null => StandardRoles.P,
        "Document" => StandardRoles.DOCUMENT,
        "Part" => StandardRoles.PART,
        "Sect" => StandardRoles.SECT,
        "Div" => StandardRoles.DIV,
        "P" => StandardRoles.P,
        "H1" => StandardRoles.H1,
        "H2" => StandardRoles.H2,
        "H3" => StandardRoles.H3,
        "H4" => StandardRoles.H4,
        "H5" => StandardRoles.H5,
        "H6" => StandardRoles.H6,
        "L" => StandardRoles.L,
        "LI" => StandardRoles.LI,
        "Lbl" => StandardRoles.LBL,
        "LBody" => StandardRoles.LBODY,
        "Table" => StandardRoles.TABLE,
        "TR" => StandardRoles.TR,
        "TH" => StandardRoles.TH,
        "TD" => StandardRoles.TD,
        "Figure" => StandardRoles.FIGURE,
        "Link" => StandardRoles.LINK,
        "Span" => StandardRoles.SPAN,
        "Reference" => StandardRoles.REFERENCE,
        "Note" => StandardRoles.NOTE,
        "Form" => StandardRoles.FORM,
        "TOC" => StandardRoles.TOC,
        "TOCI" => StandardRoles.TOCI,
        _ => StandardRoles.P
    };

    private static int PageCountFromDoc(DocumentJson doc)
    {
        if (doc.DocumentProperties is JsonElement props
            && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("numberOfPages", out var np)
            && np.ValueKind == JsonValueKind.Number
            && np.TryGetInt32(out var pages))
        {
            return Math.Max(1, pages);
        }
        int max = 1;
        foreach (var n in doc.Tree)
        {
            if (n.Page > max) max = n.Page;
        }
        return max;
    }
}
