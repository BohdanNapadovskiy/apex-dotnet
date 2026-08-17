using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Tagutils;
using iText.Kernel.Geom;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class OrphanWidgetTaggerTests
{
    [Fact]
    public void UntaggedWidgetGetsFormStructElem()
    {
        // Build a fresh tagged PDF with an untagged widget, flush to bytes, then reopen
        // in stamp mode — matches the real usage where OrphanWidgetTagger runs against
        // a source-derived PdfDocument whose annots array is already indirect-referenced.
        var src = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(src)))
        {
            doc.SetTagged();
            doc.AddNewPage();
            var widget = new PdfWidgetAnnotation(new Rectangle(50, 50, 100, 20));
            widget.SetPage(doc.GetPage(1));
            doc.GetPage(1).AddAnnotation(widget);
        }
        var dst = new MemoryStream();
        using (var doc = new PdfDocument(
            new PdfReader(new MemoryStream(src.ToArray())),
            new PdfWriter(dst)))
        {
            var patched = OrphanWidgetTagger.ApplyAll(doc);
            patched.Should().Be(1);

            var formsWithWidgetKid = CountFormsCoveringAnyWidget(doc);
            formsWithWidgetKid.Should().Be(1);
        }
    }

    [Fact]
    public void SecondPassIsNoOp()
    {
        // Build → 1st pass patches the widget → 2nd pass on the same doc should be
        // a no-op because the widget is now under a Form. Exercises idempotency.
        var src = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(src)))
        {
            doc.SetTagged();
            doc.AddNewPage();
            var widget = new PdfWidgetAnnotation(new Rectangle(50, 50, 100, 20));
            widget.SetPage(doc.GetPage(1));
            doc.GetPage(1).AddAnnotation(widget);
        }
        var dst = new MemoryStream();
        using (var doc = new PdfDocument(
            new PdfReader(new MemoryStream(src.ToArray())),
            new PdfWriter(dst)))
        {
            OrphanWidgetTagger.ApplyAll(doc).Should().Be(1);
            OrphanWidgetTagger.ApplyAll(doc).Should().Be(0);
        }
    }

    [Fact]
    public void NonWidgetAnnotationsIgnored()
    {
        using var buf = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(buf)))
        {
            doc.SetTagged();
            doc.AddNewPage();
            var link = new PdfLinkAnnotation(new Rectangle(10, 10, 20, 20));
            doc.GetPage(1).AddAnnotation(link);

            var patched = OrphanWidgetTagger.ApplyAll(doc);
            patched.Should().Be(0);
        }
    }

    private static int CountFormsCoveringAnyWidget(PdfDocument doc)
    {
        int count = 0;
        var root = doc.GetStructTreeRoot();
        if (root is null) return 0;
        Walk(root, ref count);
        return count;
    }

    private static void Walk(IStructureNode node, ref int count)
    {
        var kids = node.GetKids();
        if (kids is null) return;
        foreach (var kid in kids)
        {
            if (kid is PdfStructElem se)
            {
                if ("Form".Equals(se.GetRole()?.GetValue(), StringComparison.Ordinal))
                {
                    foreach (var childKid in se.GetKids())
                    {
                        if (childKid is PdfObjRef) { count++; break; }
                    }
                }
                Walk(se, ref count);
            }
        }
    }
}
