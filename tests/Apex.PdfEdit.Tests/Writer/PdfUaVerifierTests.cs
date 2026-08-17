using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Smoke tests for <see cref="PdfUaVerifier"/> — asserts the verifier plumbs
/// catalog + structure-tree checks through and reports pass/fail sanely. Deep
/// PDF/UA coverage is PAC 2024's job; this suite guards against regressions in
/// the verifier wiring itself.
/// </summary>
public sealed class PdfUaVerifierTests
{
    private const string Form40xPdfPath = "form-40x-2016-Remediated/form-40x-2016-Remediated.pdf";

    [FactIfSample(Form40xPdfPath)]
    public void IdentityWriteFixesSourceLinkContentsGap()
    {
        // form-40x's source has Link annotations without /Contents. The identity
        // SourceBasedWriter pass runs LinkContentsFiller which auto-populates
        // /Contents from the /A /S /URI string, so the output PASSES the checker.
        var srcPath = TestSamples.Resolve(Form40xPdfPath);
        var tmp = Path.GetTempFileName();
        try
        {
            using (var os = File.OpenWrite(tmp))
            {
                new SourceBasedWriter(srcPath).Write(os);
            }
            var sourceR = PdfUaVerifier.Verify(srcPath);
            var outR = PdfUaVerifier.Verify(tmp);
            sourceR.Ok.Should().BeFalse($"form-40x source unexpectedly clean; issues=[{string.Join(", ", sourceR.Issues)}]");
            outR.Ok.Should().BeTrue($"output failed PDF/UA-1 check; issues=[{string.Join(", ", outR.Issues)}]");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void VerifyingASourcePdfThatLacksXmpFailsWithReportedIssue()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using (var os = File.OpenWrite(tmp))
            using (var writer = new PdfWriter(os))
            using (var doc = new PdfDocument(writer))
            {
                doc.AddNewPage();
            }
            var r = PdfUaVerifier.Verify(tmp);
            r.Ok.Should().BeFalse();
            r.Issues.Should().HaveCount(1);
            r.Issues[0].Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
