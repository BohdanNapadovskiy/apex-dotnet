using Apex.PdfEdit.Core.Model;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Unit tests for <see cref="WordLevelDiff"/>. The writer relies on this producing
/// exactly one <see cref="WordDiffSegment.Replace"/> per changed-word span so
/// the redact-and-stamp pass only touches the diff — a bug that produces a
/// whole-content Replace would silently regress "preserve the raster".
/// </summary>
public sealed class WordLevelDiffTests
{
    private static Glyph G(string text, double x, double y, double w, double h) => new()
    {
        Text = text,
        X = x,
        Y = y,
        Width = w,
        Height = h
    };

    [Fact]
    public void SingleWordSubstitution_ProducesOneReplaceAndKeepsEverythingElse()
    {
        var source = new List<Glyph>
        {
            G("Each", 100, 600, 30, 15),
            G("college", 135, 600, 50, 15),
            G("shall", 190, 600, 35, 15),
            G("develop", 230, 600, 45, 15),
            G("procedures", 280, 600, 60, 15)
        };
        var r = WordLevelDiff.Diff(source, "Each college shall adopt procedures");

        r.Segments.Should().HaveCount(5);
        r.Segments[0].Should().BeOfType<WordDiffSegment.Keep>();
        r.Segments[1].Should().BeOfType<WordDiffSegment.Keep>();
        r.Segments[2].Should().BeOfType<WordDiffSegment.Keep>();
        r.Segments[3].Should().BeOfType<WordDiffSegment.Replace>();
        r.Segments[4].Should().BeOfType<WordDiffSegment.Keep>();

        var repl = (WordDiffSegment.Replace)r.Segments[3];
        repl.SourceRange.Select(b => b.Text).Should().ContainSingle().Which.Should().Be("develop");
        repl.NewRange.Should().ContainSingle().Which.Should().Be("adopt");
        r.ChangeRatio.Should().BeApproximately(0.2f, 0.001f);
    }

    [Fact]
    public void OcrFragmentedSourceWord_ReunitedIntoOneKeep()
    {
        var source = new List<Glyph>
        {
            G("the", 100, 600, 24, 15),
            G("appointm", 125, 600, 48, 15),
            G("ent", 173, 600, 24, 15),
            G("of", 200, 600, 18, 15)
        };
        var r = WordLevelDiff.Diff(source, "the appointment of");

        r.Segments.Should().HaveCount(3);
        r.Segments.Should().AllBeOfType<WordDiffSegment.Keep>();

        var mid = (WordDiffSegment.Keep)r.Segments[1];
        mid.Source.Text.Should().Be("appointment");
        mid.Source.X.Should().BeApproximately(125.0, 0.001);
        (mid.Source.X + mid.Source.Width).Should().BeApproximately(197.0, 0.001);
        r.ChangeRatio.Should().Be(0f);
    }

    [Fact]
    public void OcrFragmentedSourceWord_ReunitedCaseInsensitively()
    {
        var source = new List<Glyph>
        {
            G("C", 100, 600, 8, 15),
            G("hair", 108, 600, 24, 15)
        };
        var r = WordLevelDiff.Diff(source, "Chair");

        r.Segments.Should().ContainSingle().Which.Should().BeOfType<WordDiffSegment.Keep>();
    }

    [Fact]
    public void PureDelete_TailProducesReplaceWithEmptyNewRange()
    {
        var source = new List<Glyph>
        {
            G("keep", 100, 600, 30, 15),
            G("this", 135, 600, 30, 15),
            G("drop", 170, 600, 30, 15),
            G("that", 205, 600, 30, 15)
        };
        var r = WordLevelDiff.Diff(source, "keep this");

        r.Segments.Should().HaveCount(3);
        var tail = (WordDiffSegment.Replace)r.Segments[2];
        tail.SourceRange.Select(b => b.Text).Should().Equal("drop", "that");
        tail.NewRange.Should().BeEmpty();
        r.ChangeRatio.Should().Be(0f);
    }

    [Fact]
    public void PureInsert_TailProducesReplaceWithEmptySourceRange()
    {
        var source = new List<Glyph>
        {
            G("hello", 100, 600, 40, 15),
            G("world", 145, 600, 40, 15)
        };
        var r = WordLevelDiff.Diff(source, "hello world extra tokens");

        r.Segments.Should().HaveCount(3);
        var tail = (WordDiffSegment.Replace)r.Segments[2];
        tail.SourceRange.Should().BeEmpty();
        tail.NewRange.Should().Equal("extra", "tokens");
        r.ChangeRatio.Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void WholesaleRewrite_ProducesSingleReplaceAndFullChangeRatio()
    {
        var source = new List<Glyph>
        {
            G("original", 100, 600, 50, 15),
            G("paragraph", 155, 600, 60, 15),
            G("text", 220, 600, 30, 15)
        };
        var r = WordLevelDiff.Diff(source, "completely different content here");

        r.Segments.Should().ContainSingle();
        var only = (WordDiffSegment.Replace)r.Segments[0];
        only.SourceRange.Should().HaveCount(3);
        only.NewRange.Should().HaveCount(4);
        r.ChangeRatio.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void EmptyNewContent_ProducesOnePureDeleteReplace()
    {
        var source = new List<Glyph>
        {
            G("gone", 100, 600, 30, 15),
            G("also-gone", 135, 600, 50, 15)
        };
        var r = WordLevelDiff.Diff(source, string.Empty);

        r.Segments.Should().ContainSingle();
        var only = (WordDiffSegment.Replace)r.Segments[0];
        only.SourceRange.Should().HaveCount(2);
        only.NewRange.Should().BeEmpty();
        r.ChangeRatio.Should().Be(0f);
    }

    [Fact]
    public void EmptySource_ProducesOnePureInsert()
    {
        var r = WordLevelDiff.Diff(new List<Glyph>(), "brand new content");

        r.Segments.Should().ContainSingle();
        var only = (WordDiffSegment.Replace)r.Segments[0];
        only.SourceRange.Should().BeEmpty();
        only.NewRange.Should().Equal("brand", "new", "content");
        r.ChangeRatio.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void UmassParagraph_DevelopToAdopt_IsolatesOneReplace()
    {
        var source = ParagraphSource();
        var newContent = "Each college shall adopt procedures concerning the "
                         + "appointment of department chairpersons or acting department "
                         + "chairpersons. Such procedures shall cover vacancy of the chair "
                         + "for any reason. All such procedures shall require:";
        var r = WordLevelDiff.Diff(source, newContent);

        var replaces = r.Segments.Count(s => s is WordDiffSegment.Replace);
        replaces.Should().Be(1);
        r.ChangeRatio.Should().BeLessThan(0.1f);
    }

    private static List<Glyph> ParagraphSource()
    {
        var src = new List<Glyph>();
        string[] words =
        {
            "Each", "college", "shall", "develop", "procedures",
            "concerning", "the", "appointm", "ent", "of", "department",
            "chairpersons", "or", "acting", "department", "chairpersons.",
            "Such", "procedures", "shall", "cover", "vacancy", "of", "the",
            "chair", "for", "any", "reason.", "All", "such", "procedures",
            "shall", "requir", "e:"
        };
        double x = 100;
        foreach (var w in words)
        {
            src.Add(G(w, x, 600, w.Length * 6, 15));
            x += w.Length * 6 + 1;
        }
        return src;
    }
}
