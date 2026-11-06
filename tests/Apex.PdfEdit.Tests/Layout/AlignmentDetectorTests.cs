using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Model;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Layout;

public sealed class AlignmentDetectorTests
{
    private const string Form40x = "form-40x-2016-Remediated/form-40x-2016-Remediated-document.json";

    [Fact]
    public void ClassifiesSyntheticLeftCenterRightCorrectly()
    {
        // Median left = 100, median right = 500. Center = 300.
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("left1", 100, 200, 50),
                Node("left2", 100, 100, 50),
                Node("right1", 400, 100, 50),
                Node("right2", 400, 100, 50),
                Node("centered", 275, 50, 50),        // center = 300
                Node("justified", 100, 400, 50)       // spans 100..500, full width
            }
        };

        var a = new AlignmentDetector().Detect(doc);

        a["left1"].Should().Be(Alignment.Left);
        a["left2"].Should().Be(Alignment.Left);
        a["right1"].Should().Be(Alignment.Right);
        a["right2"].Should().Be(Alignment.Right);
        a["centered"].Should().Be(Alignment.Center);
        a["justified"].Should().Be(Alignment.Justified);
    }

    [Fact]
    public void UnknownWhenNodeMatchesNoEdge()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("a", 100, 300, 50),
                Node("b", 100, 300, 50),
                Node("outlier", 250, 60, 50)       // starts at 250, ends 310
            }
        };
        var a = new AlignmentDetector(2.0).Detect(doc);
        a["outlier"].Should().Be(Alignment.Unknown);
    }

    [FactIfSample(Form40x)]
    public void Form40xProducesSensibleAlignmentDistribution()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(Form40x));
        var a = new AlignmentDetector().Detect(doc);

        long expectedCount = doc.Tree.Count(n => n.Mcid >= 0 && !n.IsArtifact
            && !string.IsNullOrEmpty(n.Content) && n.Width > 0);
        a.Should().HaveCount((int)expectedCount);

        var hist = new Dictionary<Alignment, long>();
        foreach (var v in a.Values)
        {
            hist.TryGetValue(v, out var count);
            hist[v] = count + 1;
        }

        hist.GetValueOrDefault(Alignment.Left, 0L).Should().BePositive();
        long total = a.Count;
        long max = hist.Values.DefaultIfEmpty(0L).Max();
        ((double)max / total).Should().BeLessThan(0.98);
    }

    [Fact]
    public void LinesSharingAMidpointClassifyAsCenterOffThePageAxis()
    {
        // Board Packet p3 H2: every line centered at x≈306 while the page's body margins put
        // the page center at 301.5 — 4.3pt off, outside the 3pt tolerance, so the union-bbox
        // test alone said UNKNOWN and the edit re-flowed left.
        var title = Node("title", 124.8, 362.1, 72);
        title.Boxes = new List<TreeBox>
        {
            Box(254.0, 104.0), Box(225.4, 161.0), Box(196.6, 218.8), Box(124.8, 362.1), Box(269.8, 75.4)
        };
        var doc = new DocumentJson
        {
            Tree = { Node("body1", 72, 459, 50), Node("body2", 72, 459, 50), Node("body3", 72, 400, 50), title }
        };

        new AlignmentDetector().Detect(doc)["title"].Should().Be(Alignment.Center);
    }

    [Fact]
    public void SmallFirstLineIndentDoesNotReadAsCenter()
    {
        // Sample2 Note shape, two lines: 3.5pt indent on line 0, both lines reach the right
        // edge → midpoints within 1.75pt, but a 3.5pt left spread is under the 2×tolerance floor.
        var note = Node("note", 72, 459, 24);
        note.Boxes = new List<TreeBox> { Box(75.5, 455.5), Box(72, 459) };
        var doc = new DocumentJson { Tree = { Node("body1", 72, 459, 50), Node("body2", 72, 459, 50), note } };

        new AlignmentDetector().Detect(doc)["note"].Should().Be(Alignment.Justified);
    }

    private static TreeBox Box(double x, double width) => new() { X = x, Width = width, Height = 12, Page = 1 };

    private static TreeNode Node(string id, double x, double width, double height)
    {
        int digits = int.TryParse(new string(id.Where(char.IsDigit).ToArray()), out var m) ? m : 0;
        return new TreeNode
        {
            Id = id,
            Parent = "#",
            Text = "P",
            Page = 1,
            Mcid = Math.Max(0, digits),
            X = x,
            Width = width,
            Height = height,
            Content = "sample"
        };
    }
}
