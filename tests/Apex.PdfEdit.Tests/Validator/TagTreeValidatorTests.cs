using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Model;
using Apex.PdfEdit.Core.Validator;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Validator;

public sealed class TagTreeValidatorTests
{
    private const string Form40x = "form-40x-2016-Remediated/form-40x-2016-Remediated-document.json";
    private const string BoardPacket = "05-15-2025 Board Packet-Remediated/05-15-2025 Board Packet-Remediated-document.json";

    [Fact]
    public void DefaultSchemaLoadsAndContainsRev12Fixes()
    {
        var schema = SchemaLoader.LoadDefault();
        schema.DefinedTags().Should().Contain(new[] { "Artifact", "TH", "P", "Div" });

        // The confirmed TH.requiredChildren bug fix.
        schema.Rule("TH")!.RequiredChildren.Should().BeEmpty();

        // Rev 1.2 loosenings.
        schema.Rule("Div")!.AllowedChildren.Should().Contain("Artifact");
        schema.Rule("P")!.AllowedChildren.Should().Contain("Form");
        schema.Rule("Reference")!.AllowedChildren.Should().Contain("Link");
    }

    [FactIfSample(Form40x)]
    public void Form40xIsCleanUnderRev12()
    {
        var schema = SchemaLoader.LoadDefault();
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(Form40x));
        var report = new TagTreeValidator(schema).Validate(doc);

        report.ErrorCount().Should().Be(0);
        report.WarningCount().Should().Be(0);
        report.IsClean().Should().BeTrue();
    }

    [FactIfSample(BoardPacket)]
    public void BoardPacketHasExpectedViolationCountUnderRev12()
    {
        var schema = SchemaLoader.LoadDefault();
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(BoardPacket));
        var report = new TagTreeValidator(schema).Validate(doc);

        // _validator_baseline.py against Rev 1.2 reports 56 violating nodes on this sample.
        report.DistinctErrorNodes().Should().Be(56L);
        report.WarningCount().Should().Be(0);
    }

    [Fact]
    public void DanglingParentReportedAsError()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("1", "#", "Document"),
                Node("2", "does-not-exist", "P")
            }
        };

        var report = new TagTreeValidator(DefaultSchema()).Validate(doc);

        var errors = report.Errors();
        errors.Should().HaveCount(1);
        errors[0].Code.Should().Be("DANGLING_PARENT");
        errors[0].NodeId.Should().Be("2");
    }

    [Fact]
    public void UnknownTagReportedAsWarning()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("1", "#", "Document"),
                Node("2", "1", "SomeCustomTag")
            }
        };

        var report = new TagTreeValidator(DefaultSchema()).Validate(doc);

        // Unknown tag -> WARNING for id=2. Parent rule (Document.allowedChildren) rejects the
        // unknown tag -> also emits a PARENT_REJECTS_CHILD error on the same node.
        report.WarningCount().Should().Be(1L);
        report.Issues.Should().Contain(i => i.Code == "UNKNOWN_TAG" && i.NodeId == "2");
    }

    [Fact]
    public void ParentChildPairMismatchProducesBothDirectionErrors()
    {
        // Document -> Span is rejected in both directions: Document.allowedChildren has no Span,
        // Span.allowedParents has no Document.
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("1", "#", "Document"),
                Node("2", "1", "Span")
            }
        };

        var report = new TagTreeValidator(DefaultSchema()).Validate(doc);

        report.Errors().Select(e => e.NodeId).Should().OnlyContain(id => id == "2");
        report.Errors().Select(e => e.Code).Should().BeEquivalentTo(new[]
        {
            "PARENT_REJECTS_CHILD", "CHILD_REJECTS_PARENT"
        });
        // Two error issues on one node -> distinctErrorNodes = 1.
        report.DistinctErrorNodes().Should().Be(1L);
    }

    private static TreeNode Node(string id, string parent, string tag) => new()
    {
        Id = id,
        Parent = parent,
        Text = tag
    };

    private static SchemaRules DefaultSchema() => SchemaLoader.LoadDefault();
}
