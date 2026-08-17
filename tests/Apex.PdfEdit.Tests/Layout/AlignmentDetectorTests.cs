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
