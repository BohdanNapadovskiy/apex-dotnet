using System.Text.RegularExpressions;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Covers the OCR re-vectorise flow on the ImplementationGuidelines UMass Boston
/// sample — an ABBYY-produced scan where the raster carries the visual and the
/// invisible OCR overlay carries the tag tree.
/// </summary>
public sealed class OcrRevectorizeWriterTests
{
    private const string Sample = "ImplementationGuidelines-l241_Accessible";
    private const string DocPath = Sample + "/" + Sample + "-document.json";
    private const string GeomPath = Sample + "/" + Sample + "-geometry.json";
    private const string PdfPath = Sample + "/" + Sample + ".pdf";
    private const string EditsPath = Sample + "/" + Sample + "-edits.json";

    [FactIfSample(PdfPath)]
    public void RenderOcrPassesRasterThroughAndKeepsVectorTextExtractable()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            new OcrRevectorizeWriter(resolver).Write(doc, geom, outBuf);
        }

        var bytes = outBuf.ToArray();
        var debug = Path.Combine(TestOutputs.ForSample(Sample), Sample + "_ocr-revectorize.pdf");
        File.WriteAllBytes(debug, bytes);

        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().Be(30);
        pdf.IsTagged().Should().BeTrue();

        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        var normalised = Regex.Replace(page1, @"\s+", "");
        normalised.Should().Contain("UNIVERSITYOFMASSACHUSETTS");
        normalised.Should().Contain("BOSTONCAMPUS");
        normalised.Should().Contain("TABLEOFCONTENTS");
    }

    [FactIfSample(EditsPath)]
    public void EditOcrAppliesSetTextAndDeleteWithoutIssues()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));
        var edits = EditsJsonLoader.Load(TestSamples.Resolve(EditsPath));

        var outBuf = new MemoryStream();
        EditResult result;
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            // Widened OCR-raster glyph check.
            result = new EditEngine(resolver, allowExtractedGlyphs: true).Apply(doc, geom, edits);
            new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, outBuf);
        }

        var bytes = outBuf.ToArray();
        var debug = Path.Combine(TestOutputs.ForSample(Sample), Sample + "_ocr-edited.pdf");
        File.WriteAllBytes(debug, bytes);

        result.Issues.Should().BeEmpty("edit issues");
        result.AppliedOpIds.Should().HaveCount(edits.Operations.Count);

        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().Be(30);
        pdf.IsTagged().Should().BeTrue();

        var page3norm = Regex.Replace(PdfTextExtractor.GetTextFromPage(pdf.GetPage(3)), @"\s+", " ");
        page3norm.Should().Contain("APEX testing results");
        var page6norm = Regex.Replace(PdfTextExtractor.GetTextFromPage(pdf.GetPage(6)), @"\s+", " ");
        page6norm.Should().Contain("apex award");

        page3norm.Should().NotContain("Ad: Hoc Department Personnel Committee");
    }

    /// <summary>
    /// Phase 1 of OCR_Visual_Fidelity_Plan — the writer adopts source's own embedded font
    /// subset for edited nodes instead of falling through to a Windows / standard-14 fallback.
    /// </summary>
    [FactIfSample(EditsPath)]
    public void EditedNodeUsesSourceEmbeddedFontSubset()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));
        var edits = EditsJsonLoader.Load(TestSamples.Resolve(EditsPath));

        var sourcePage3Fonts = ReadBaseFontNames(TestSamples.Resolve(PdfPath), 3);
        sourcePage3Fonts.Should().NotBeEmpty("source page 3 must have at least one embedded font to reuse");

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            var result = new EditEngine(resolver, allowExtractedGlyphs: true).Apply(doc, geom, edits);
            new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, outBuf);
        }

        var bytes = outBuf.ToArray();
        HashSet<string> outputPage3Fonts;
        using (var reader = new PdfReader(new MemoryStream(bytes)))
        using (var pdf = new PdfDocument(reader))
        {
            outputPage3Fonts = CollectBaseFontNames(pdf.GetPage(3));
        }

        // Intersection: at least one source BaseFont carried over to the output verbatim.
        var shared = new HashSet<string>(sourcePage3Fonts);
        shared.IntersectWith(outputPage3Fonts);
        shared.Should().NotBeEmpty(
            $"output page 3 must reuse at least one source-embedded font; source=[{string.Join(", ", sourcePage3Fonts)}], output=[{string.Join(", ", outputPage3Fonts)}]");
    }

    /// <summary>
    /// Phase 1g — the rasterisation path adds a PdfImageXObject per edited region on top
    /// of the source scan passthrough. Page 3 must have ≥ 2 image XObjects (source scan +
    /// rasterised edit stamp).
    /// </summary>
    [FactIfSample(EditsPath)]
    public void EditedRegionAddsRasterImageXObject()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));
        var edits = EditsJsonLoader.Load(TestSamples.Resolve(EditsPath));

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            var result = new EditEngine(resolver).Apply(doc, geom, edits);
            new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, outBuf);
        }

        var bytes = outBuf.ToArray();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        int imagesOnPage3 = CountImageXObjects(pdf.GetPage(3));
        imagesOnPage3.Should().BeGreaterThanOrEqualTo(2,
            "page 3 must have source scan + at least one rasterised edit stamp");
    }

    private static HashSet<string> ReadBaseFontNames(string pdfPath, int pageNumber)
    {
        using var reader = new PdfReader(pdfPath);
        using var pdf = new PdfDocument(reader);
        return CollectBaseFontNames(pdf.GetPage(pageNumber));
    }

    private static HashSet<string> CollectBaseFontNames(PdfPage page)
    {
        var names = new HashSet<string>();
        var inv = PageFontInventory.Of(page);
        foreach (var f in inv.Fonts.Values)
        {
            if (f.GetFontProgram() is null) continue;
            var n = f.GetFontProgram().GetFontNames()?.GetFontName();
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
        }
        return names;
    }

    private static int CountImageXObjects(PdfPage page)
    {
        int count = 0;
        var xobj = page.GetResources().GetResource(PdfName.XObject);
        if (xobj is null) return 0;
        foreach (var name in xobj.KeySet())
        {
            try
            {
                var img = page.GetResources().GetImage(name);
                if (img is not null) count++;
            }
            catch
            {
                // Skip non-image XObjects (form XObjects, etc.)
            }
        }
        return count;
    }
}
