using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Tagutils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Ensures every Widget annotation is reachable via a <c>Form</c> or <c>Artifact</c>
/// StructElem, per PDF/UA-1 §7.18.4. Two failure modes are fixed:
/// <list type="bullet">
///   <item><b>Widget with OBJR under a non-Form parent</b> — the source's StructElem
///       covering the OBJR has role like <c>Div</c> or <c>P</c>. We <b>change that
///       parent's role to <c>Form</c></b> and attach a <c>/O /PrintField /Role /tv</c>
///       attribute (needed because Form must have exactly one OBJR child unless
///       PrintField is set).</item>
///   <item><b>Widget with no covering OBJR at all</b> — no <c>/StructParent</c> or
///       its ParentTree entry doesn't resolve. We add a fresh <c>Form</c> at the
///       document root with an OBJR pointing to the widget.</item>
/// </list>
///
/// The pass is idempotent. Non-Widget annotations are ignored.
/// This is a .NET-only enhancement — no Java equivalent exists.
/// </summary>
public static class OrphanWidgetTagger
{
    private const string FormRole = "Form";
    private const string ArtifactRole = "Artifact";

    /// <summary>
    /// Fix every non-compliant Widget annotation reachable from <paramref name="doc"/>.
    /// Returns the count of widgets patched.
    /// </summary>
    public static int ApplyAll(PdfDocument doc, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var log = logger ?? NullLogger.Instance;

        // Walk the source StructElem tree once, building a map from widget-annot-ref
        // to the parent StructElem of the OBJR referencing it. Also collect the set
        // of already-covered widget refs so we can skip freshly-added coverage.
        var parentByWidget = new Dictionary<PdfIndirectReference, PdfStructElem>();
        var root = doc.GetStructTreeRoot();
        if (root is not null) Walk(root, parentByWidget);

        var tag = new TagTreePointer(doc);
        int patched = 0;

        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var page = doc.GetPage(p);
            var annots = page.GetAnnotations();
            if (annots is null) continue;

            foreach (var annot in annots)
            {
                if (annot is not PdfWidgetAnnotation widget) continue;
                var refr = widget.GetPdfObject().GetIndirectReference();

                if (refr is not null && parentByWidget.TryGetValue(refr, out var parent))
                {
                    // Widget IS covered by an OBJR — check the parent's role.
                    var role = parent.GetRole()?.GetValue();
                    if (FormRole.Equals(role, StringComparison.Ordinal)
                        || ArtifactRole.Equals(role, StringComparison.Ordinal))
                    {
                        continue; // Already compliant.
                    }
                    // Change parent's role to Form + attach PrintField (which allows
                    // multiple children — the parent likely has non-OBJR content
                    // beyond the widget's OBJR).
                    parent.GetPdfObject().Put(PdfName.S, new PdfName(FormRole));
                    parent.SetModified();
                    AttachPrintFieldAttribute(parent);
                    patched++;
                    continue;
                }

                // Widget has no covering OBJR — add a fresh Form + OBJR under the tree root.
                widget.GetPdfObject().Remove(PdfName.StructParent);
                widget.SetModified();
                tag.SetPageForTagging(page);
                tag.AddTag(FormRole);
                tag.GetProperties().AddAttributes(
                    new PdfStructureAttributes("PrintField").AddEnumAttribute("Role", "tv"));
                tag.AddAnnotationTag(widget);
                tag.MoveToParent();
                patched++;
            }
        }

        if (patched > 0)
        {
            log.LogInformation("[widget-tag] fixed {N} Widget annotation(s) for PDF/UA-1 compliance", patched);
        }
        return patched;
    }

    private static void AttachPrintFieldAttribute(PdfStructElem parent)
    {
        // Append /O /PrintField /Role /tv to the existing /A array, or create one.
        var attrsDict = new PdfDictionary();
        attrsDict.Put(PdfName.O, new PdfName("PrintField"));
        attrsDict.Put(new PdfName("Role"), new PdfName("tv"));

        var existing = parent.GetPdfObject().Get(PdfName.A);
        if (existing is null)
        {
            parent.GetPdfObject().Put(PdfName.A, attrsDict);
        }
        else if (existing is PdfArray arr)
        {
            arr.Add(attrsDict);
        }
        else
        {
            var merged = new PdfArray();
            merged.Add(existing);
            merged.Add(attrsDict);
            parent.GetPdfObject().Put(PdfName.A, merged);
        }
    }

    /// <summary>
    /// DFS the structure tree. For every <see cref="PdfObjRef"/> that references a
    /// widget annotation, record the widget-ref → containing-StructElem mapping.
    /// </summary>
    private static void Walk(IStructureNode node,
        Dictionary<PdfIndirectReference, PdfStructElem> parentByWidget)
    {
        var kids = node.GetKids();
        if (kids is null) return;
        foreach (var kid in kids)
        {
            // OBJR checked BEFORE PdfMcr because PdfObjRef extends PdfMcr.
            if (kid is PdfObjRef objRef)
            {
                var annotDict = objRef.GetReferencedObject();
                if (annotDict is null) continue;
                if (!PdfName.Widget.Equals(annotDict.GetAsName(PdfName.Subtype))) continue;
                var refr = annotDict.GetIndirectReference();
                if (refr is null) continue;
                if (node is PdfStructElem parent)
                {
                    parentByWidget.TryAdd(refr, parent);
                }
            }
            else if (kid is PdfStructElem child)
            {
                Walk(child, parentByWidget);
            }
        }
    }
}
