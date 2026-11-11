using Apex.PdfEdit.Core.Edit;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Edit;

/// <summary>
/// Loader-level coverage for the two on-wire shapes <see cref="EditsJsonLoader"/> now
/// accepts: the engine-native <c>operations[]</c> format and the customer's
/// <c>edits[{targetNodeId, updatedText}]</c> format. Pre-fix the loader silently
/// deserialized the customer shape as <c>operations=[]</c> and the CLI/API reported
/// <c>ops=0</c> with no error — the Ram UAT reproduction hit this on the first run.
/// </summary>
public sealed class EditsJsonLoaderTests
{
    [Fact]
    public void EngineShapeRoundTripsIntoOperations()
    {
        string json = """
        {
          "schemaVersion": "1",
          "baseDocument": "doc.json",
          "operations": [
            { "id": "op-0", "type": "setText", "target": "12", "newContent": "hello" },
            { "id": "op-1", "type": "setText", "target": "34", "newContent": "world" }
          ]
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var edits = EditsJsonLoader.Load(path);
            edits.SchemaVersion.Should().Be("1");
            edits.BaseDocument.Should().Be("doc.json");
            edits.Operations.Should().HaveCount(2);
            edits.Operations.Select(o => o.Id).Should().Equal("op-0", "op-1");
            edits.Operations.OfType<SetTextOp>().Select(o => o.Target).Should().Equal("12", "34");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CustomerShapeIsTranslatedIntoSetTextOps()
    {
        // The exact byte pattern Ram's edits.json ships (2026-09-07): {targetNodeId,
        // updatedText, updatedAt}. Pre-P3 this produced ops=0.
        string json = """
        {
          "schemaVersion": "1.0",
          "edits": [
            { "targetNodeId": "5", "updatedText": "hello", "updatedAt": "2026-08-31T17:11:26Z" },
            { "targetNodeId": "39", "updatedText": "7.0 Keys and locks 10", "updatedAt": "2026-08-31T17:15:17Z" }
          ]
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var edits = EditsJsonLoader.Load(path);
            edits.SchemaVersion.Should().Be("1.0");
            edits.Operations.Should().HaveCount(2);
            edits.Operations.Select(o => o.Id).Should().Equal("edit-0", "edit-1");
            var setTexts = edits.Operations.OfType<SetTextOp>().ToList();
            setTexts.Should().HaveCount(2);
            setTexts[0].Target.Should().Be("5");
            setTexts[0].NewContent.Should().Be("hello");
            setTexts[1].Target.Should().Be("39");
            setTexts[1].NewContent.Should().Be("7.0 Keys and locks 10");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CustomerShapeMissingTargetNodeIdThrowsClearError()
    {
        string json = """{ "edits": [ { "updatedText": "hi" } ] }""";
        var path = WriteTempJson(json);
        try
        {
            Action act = () => EditsJsonLoader.Load(path);
            act.Should().Throw<InvalidDataException>()
                .WithMessage("*edits[0].targetNodeId missing or empty*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CustomerShapeMissingUpdatedTextThrowsClearError()
    {
        string json = """{ "edits": [ { "targetNodeId": "5" } ] }""";
        var path = WriteTempJson(json);
        try
        {
            Action act = () => EditsJsonLoader.Load(path);
            act.Should().Throw<InvalidDataException>()
                .WithMessage("*edits[0].updatedText missing or non-string*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownShapeThrowsClearError()
    {
        string json = """{ "schemaVersion": "?", "somethingElse": [] }""";
        var path = WriteTempJson(json);
        try
        {
            Action act = () => EditsJsonLoader.Load(path);
            act.Should().Throw<InvalidDataException>()
                .WithMessage("*neither 'operations' * nor 'edits'*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EngineShapeWinsWhenBothPresent()
    {
        // Customers don't ship the hybrid, but silent precedence beats throwing.
        string json = """
        {
          "operations": [{ "id": "op-x", "type": "setText", "target": "1", "newContent": "engine" }],
          "edits":      [{ "targetNodeId": "1", "updatedText": "customer" }]
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var edits = EditsJsonLoader.Load(path);
            edits.Operations.Should().HaveCount(1);
            edits.Operations[0].Id.Should().Be("op-x");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyCustomerEditsArrayLoadsWithZeroOps()
    {
        // Distinguish "empty on purpose" from "unrecognized shape" — an empty edits
        // array is still a valid customer payload.
        string json = """{ "schemaVersion": "1.0", "edits": [] }""";
        var path = WriteTempJson(json);
        try
        {
            var edits = EditsJsonLoader.Load(path);
            edits.Operations.Should().BeEmpty();
            edits.SchemaVersion.Should().Be("1.0");
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, content);
        return path;
    }
}
