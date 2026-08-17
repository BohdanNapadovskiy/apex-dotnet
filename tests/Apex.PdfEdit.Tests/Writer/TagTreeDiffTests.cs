using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class TagTreeDiffTests
{
    private const string Form40xPdf = "form-40x-2016-Remediated/form-40x-2016-Remediated.pdf";

    [FactIfSample(Form40xPdf)]
    public void IdentityWriteProducesCleanTagDiff()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var tmp = Path.GetTempFileName();
        try
        {
            using (var os = File.OpenWrite(tmp))
            {
                new SourceBasedWriter(srcPath).Write(os);
            }
            var r = TagTreeDiff.Diff(srcPath, tmp);
            r.SourceMcidCount.Should().BeGreaterThan(0);
            r.EditedMcidCount.Should().Be(r.SourceMcidCount);
            r.Mismatches.Should().BeEmpty("identity pass produced tag tree diffs — investigate before shipping");
            r.Clean.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [FactIfSample(Form40xPdf)]
    public void DiffAgainstItselfIsClean()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var r = TagTreeDiff.Diff(srcPath, srcPath);
        r.Clean.Should().BeTrue();
        r.SourceMcidCount.Should().Be(r.EditedMcidCount);
    }
}
