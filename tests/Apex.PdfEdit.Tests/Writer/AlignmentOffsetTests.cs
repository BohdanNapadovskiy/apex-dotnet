using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Unit tests for the per-line X offset math shared between
/// <see cref="OcrRevectorizeWriter"/> (PDF user-space) and
/// <see cref="RasterizedTextStamper"/> (image pixel space).
/// </summary>
public sealed class AlignmentOffsetTests
{
    [Fact]
    public void WriterAlignedStartX_LeftAndUnknownAndJustifiedAnchorToLeftEdge()
    {
        OcrRevectorizeWriter.AlignedStartX(50, 200, Alignment.Left, 40).Should().Be(50f);
        OcrRevectorizeWriter.AlignedStartX(50, 200, Alignment.Unknown, 40).Should().Be(50f);
        OcrRevectorizeWriter.AlignedStartX(50, 200, Alignment.Justified, 40).Should().Be(50f);
    }

    [Fact]
    public void WriterAlignedStartX_RightAnchorsToRightEdge()
    {
        // Right edge sits at 250; a 40pt line ends at 250, so it starts at 210.
        OcrRevectorizeWriter.AlignedStartX(50, 200, Alignment.Right, 40)
            .Should().BeApproximately(210f, 0.001f);
    }

    [Fact]
    public void WriterAlignedStartX_CenterSplitsLeftover()
    {
        // 200pt bbox with 40pt line → 80pt slack, halved → start at 50+80 = 130.
        OcrRevectorizeWriter.AlignedStartX(50, 200, Alignment.Center, 40)
            .Should().BeApproximately(130f, 0.001f);
    }

    [Fact]
    public void WriterAlignedStartX_OverWideLineClampedToLeft()
    {
        OcrRevectorizeWriter.AlignedStartX(50, 100, Alignment.Center, 200).Should().Be(50f);
        OcrRevectorizeWriter.AlignedStartX(50, 100, Alignment.Right, 200).Should().Be(50f);
    }

    [Fact]
    public void StamperAlignedStartXPx_MatchesWriterSemantics()
    {
        // 800px bbox, 200px line → CENTER starts at 300.
        RasterizedTextStamper.AlignedStartXPx(800, 200f, Alignment.Center)
            .Should().BeApproximately(300f, 0.001f);
        // RIGHT ends at right edge (800), so starts at 600.
        RasterizedTextStamper.AlignedStartXPx(800, 200f, Alignment.Right)
            .Should().BeApproximately(600f, 0.001f);
        RasterizedTextStamper.AlignedStartXPx(800, 200f, Alignment.Left).Should().Be(0f);
        RasterizedTextStamper.AlignedStartXPx(800, 200f, Alignment.Justified).Should().Be(0f);
        RasterizedTextStamper.AlignedStartXPx(800, 200f, Alignment.Unknown).Should().Be(0f);
    }

    [Fact]
    public void StamperAlignedStartXPx_OverWideLineClampedToZero()
    {
        RasterizedTextStamper.AlignedStartXPx(200, 400f, Alignment.Center).Should().Be(0f);
        RasterizedTextStamper.AlignedStartXPx(200, 400f, Alignment.Right).Should().Be(0f);
    }
}
