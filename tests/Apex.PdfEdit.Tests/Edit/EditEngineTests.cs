using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Model;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Tagging;
using Xunit;

namespace Apex.PdfEdit.Tests.Edit;

/// <summary>
/// Comprehensive coverage of the four EditEngine ops (setText / addParagraph /
/// addListItem / deleteNode) plus push-down reflow, collision detection, font
/// resolution, and end-to-end round-trip tests via SourceBasedWriter.
/// </summary>
public sealed class EditEngineTests
{
    private const string SampleDir = "form-40x-2016-Remediated";
    private const string DocPath = SampleDir + "/" + SampleDir + "-document.json";
    private const string GeomPath = SampleDir + "/" + SampleDir + "-geometry.json";
    private const string PdfPath = SampleDir + "/" + SampleDir + ".pdf";

    // ============================================================================
    //  setText engine tests
    // ============================================================================

    [FactIfSample(PdfPath)]
    public void SetTextMutatesTreeAndProducesOverlay()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var newText = "notes";
        var edits = new EditsJson
        {
            SchemaVersion = "1",
            BaseDocument = Path.GetFileName(DocPath),
            Operations = { SetTextOp.Of("e1", "3", newText) }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath));
        var result = new EditEngine(resolver).Apply(doc, edits);

        result.AppliedOpIds.Should().Equal("e1");
        result.Issues.Should().BeEmpty();

        var target = doc.Tree.FirstOrDefault(n => n.Id == "3");
        target.Should().NotBeNull();
        target!.Content.Should().Be(newText);

        result.Plan.SetTextOverlays.Should().HaveCount(1);
        var o = result.Plan.SetTextOverlays[0];
        o.NodeId.Should().Be("3");
        o.Page.Should().Be(1);
        o.NewContent.Should().Be(newText);
        o.Style.Family.Should().NotBeNullOrWhiteSpace();
    }

    [FactIfSample(PdfPath)]
    public void SetTextRefusesGlyphNotInSourceFontSubset()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var edits = new EditsJson
        {
            Operations = { SetTextOp.Of("e1", "3", "Kansas") }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath));
        var result = new EditEngine(resolver).Apply(doc, edits);

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("U+004B").And.Contain("'K'").And.Contain("embedded font subset");
    }

    [Fact]
    public void OverlayCarriesRightAlignmentFromDetector()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Node("peer1", 100, 400),
                Node("peer2", 100, 400),
                Node("target", 380, 120)   // ends at 500 = modeRight; left far from 100
            }
        };
        var edits = new EditsJson { Operations = { SetTextOp.Of("e1", "target", "new") } };

        var result = new EditEngine(null).Apply(doc, edits);

        result.AppliedOpIds.Should().Equal("e1");
        result.Plan.SetTextOverlays.Should().HaveCount(1);
        result.Plan.SetTextOverlays[0].Alignment.Should().Be(Alignment.Right);
    }

    [FactIfSample(PdfPath)]
    public void SetTextFailsGracefullyOnMissingTarget()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var edits = new EditsJson
        {
            Operations =
            {
                SetTextOp.Of("e1", "3", "notes"),
                SetTextOp.Of("e2", "no-such-id", "bogus"),
                SetTextOp.Of("e3", "4", "note")
            }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath));
        var result = new EditEngine(resolver).Apply(doc, edits);

        result.AppliedOpIds.Should().Equal("e1", "e3");
        result.Issues.Should().HaveCount(1);
        result.Issues[0].OpId.Should().Be("e2");
        result.Issues[0].Message.Should().Contain("no-such-id");
    }

    // ============================================================================
    //  deleteNode engine tests (synthetic docs)
    // ============================================================================

    [Fact]
    public void DeleteNodeRemovesNodeAndEmitsOverlay()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "p2") }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("d1");
        doc.Tree.Any(n => n.Id == "p2").Should().BeFalse();
        doc.Tree.First(n => n.Id == "p3").Y.Should().Be(660.0);

        result.Plan.DeleteOverlays.Should().HaveCount(1);
        var o = result.Plan.DeleteOverlays[0];
        o.NodeId.Should().Be("p2");
        o.Page.Should().Be(1);
        o.Mcid.Should().Be(6);
    }

    [Fact]
    public void DeleteNodeRejectsStructuralNode()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "sect") }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("no MCID");
        doc.Tree.Any(n => n.Id == "sect").Should().BeTrue();
    }

    [Fact]
    public void DeleteNodeRejectsUnknownTarget()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "nope") }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("nope");
    }

    [Fact]
    public void DeleteNodeRejectsWhenTargetHasNonArtifactDescendants()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("sect", "#", "Sect"),
                Para("parent", "sect", 5, 100, 700, 300, 12, "Name:")
            }
        };
        var formChild = Para("widget", "parent", -1, 100, 700, 300, 22, "");
        formChild.Text = "Form";
        doc.Tree.Add(formChild);

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "parent") }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should()
            .Contain("non-artifact descendant")
            .And.Contain("id=widget tag=Form")
            .And.Contain("PDF/UA");
        doc.Tree.Any(n => n.Id == "parent").Should().BeTrue();
        doc.Tree.Any(n => n.Id == "widget").Should().BeTrue();
    }

    [Fact]
    public void DeleteNodeAllowedWhenDescendantsAreArtifact()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("sect", "#", "Sect"),
                Para("parent", "sect", 5, 100, 700, 300, 12, "Name:")
            }
        };
        var artifactChild = Para("art", "parent", -1, 100, 700, 300, 22, "");
        artifactChild.Text = "Form";
        artifactChild.IsArtifact = true;
        doc.Tree.Add(artifactChild);

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "parent") }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("d1");
        doc.Tree.Any(n => n.Id == "parent").Should().BeFalse();
    }

    [Fact]
    public void DeleteNodeRejectsNonWhitelistedTag()
    {
        var doc = new DocumentJson { Tree = { Sect("sect", "#", "Sect") } };
        var figure = Para("fig", "sect", 5, 100, 700, 100, 100, "");
        figure.Text = "Figure";
        doc.Tree.Add(figure);

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", "fig") }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("Figure");
        doc.Tree.Any(n => n.Id == "fig").Should().BeTrue();
    }

    // ============================================================================
    //  addParagraph engine tests (synthetic docs)
    // ============================================================================

    [Fact]
    public void AddParagraphInsertsBetweenSiblingsAndProducesOverlay()
    {
        var doc = ThreeParagraphSect();
        var edits = new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted paragraph", null) }
        };

        var result = new EditEngine(null).Apply(doc, edits);

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");

        var sectChildren = doc.Tree.Where(n => n.Parent == "sect").ToList();
        sectChildren.Select(n => n.Id).Should().Equal("p1", "1", "p2", "p3");
        var created = sectChildren[1];
        created.Text.Should().Be("P");
        created.Content.Should().Be("inserted paragraph");
        created.Page.Should().Be(1);
        created.X.Should().Be(100.0);
        created.Width.Should().Be(300.0);
        // paraGap = 0.58 × 12 = 6.96, height = 12 × 1.02 = 12.24
        created.Y.Should().Be(700.0 - 6.96 - 12.24);
        created.Status.Should().Be("added");
        created.Mcid.Should().Be(-1);

        result.Plan.AddParagraphOverlays.Should().HaveCount(1);
        var o = result.Plan.AddParagraphOverlays[0];
        o.NewNodeId.Should().Be("1");
        o.Tag.Should().Be("P");
        o.NewContent.Should().Be("inserted paragraph");
        o.Page.Should().Be(1);
        o.DonorMcid.Should().Be(5); // p1 is prev sibling
    }

    [Fact]
    public void AddParagraphIndexMinusOneAppends()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "P", "tail", null) }
        });

        result.Issues.Should().BeEmpty();
        var sectChildren = doc.Tree.Where(n => n.Parent == "sect").ToList();
        sectChildren.Select(n => n.Id).Should().Equal("p1", "p2", "p3", "1");
        result.Plan.AddParagraphOverlays[0].DonorMcid.Should().Be(7);
    }

    [Fact]
    public void AddParagraphIndexZeroInsertsBeforeFirstSibling()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 0, "P", "head", null) }
        });

        result.Issues.Should().BeEmpty();
        var sectChildren = doc.Tree.Where(n => n.Parent == "sect").ToList();
        sectChildren.Select(n => n.Id).Should().Equal("1", "p1", "p2", "p3");
        var o = result.Plan.AddParagraphOverlays[0];
        o.DonorMcid.Should().Be(5);
        o.Y.Should().Be(700.0 + 12.0 + 6.96);
    }

    [Fact]
    public void AddParagraphRejectsNonWhitelistedParent()
    {
        var doc = ThreeParagraphSect();
        doc.Tree.First(n => n.Id == "sect").Text = "Table";

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 0, "P", "x", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("parent whitelist");
    }

    [Fact]
    public void AddParagraphRejectsNonWhitelistedTag()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 0, "TR", "x", null) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("not in POC whitelist");
    }

    [Fact]
    public void AddParagraphRejectsMissingParent()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "does-not-exist", 0, "P", "x", null) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("does-not-exist");
    }

    [Fact]
    public void AddParagraphRejectsIndexOutOfRange()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 99, "P", "x", null) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("out of range");
    }

    [Fact]
    public void AddParagraphExplicitInheritFromWins()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "x", new StyleSpec("p3")) }
        });

        result.Issues.Should().BeEmpty();
        result.Plan.AddParagraphOverlays.Should().HaveCount(1);
    }

    [Fact]
    public void AddParagraphPushesDownLaterSiblings()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted", null) }
        });

        result.Issues.Should().BeEmpty();
        doc.Tree.First(n => n.Id == "p1").Y.Should().Be(700.0);
        // shiftAmount = 12.24 + 6.96 = 19.2
        doc.Tree.First(n => n.Id == "p2").Y.Should().Be(680.0 - 19.2);
        doc.Tree.First(n => n.Id == "p3").Y.Should().Be(660.0 - 19.2);

        result.Plan.MoveOverlays.Should().HaveCount(2);
        result.Plan.MoveOverlays.Select(m => m.Mcid).Should().BeEquivalentTo(new[] { 6, 7 });
        result.Plan.MoveOverlays.Should().OnlyContain(m => Math.Abs(m.Dy - -19.2) < 1e-6 && m.Dx == 0.0);
    }

    [Fact]
    public void AddParagraphPushDownShiftsOrphanGeometryMcids()
    {
        var doc = ThreeParagraphSect();

        var geom = new GeometryJson
        {
            PageMcidWords = new Dictionary<string, Dictionary<string, List<Glyph>>>
            {
                ["1"] = new()
                {
                    ["85"] = new() { GlyphAt(100, 665, 300, 10) },
                    ["999"] = new() { GlyphAt(500, 665, 100, 10) },
                    ["888"] = new() { GlyphAt(100, 800, 300, 10) }
                }
            }
        };

        var result = new EditEngine(null).Apply(doc, geom, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted", null) }
        });

        result.Issues.Should().BeEmpty();
        result.Plan.MoveOverlays.Select(m => m.Mcid).Should().BeEquivalentTo(new[] { 6, 7, 85 });
        result.Plan.MoveOverlays.Should().OnlyContain(m => Math.Abs(m.Dy - -19.2) < 1e-6 && m.Dx == 0.0);
    }

    [Fact]
    public void AddParagraphAtEndSkipsPushDown()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "P", "tail", null) }
        });

        result.Issues.Should().BeEmpty();
        result.Plan.MoveOverlays.Should().BeEmpty();
        doc.Tree.First(n => n.Id == "p3").Y.Should().Be(660.0);
    }

    [Fact]
    public void AddParagraphAllowsPushDownBelowPageBottom()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("sect", "#", "Sect"),
                Para("p_top", "sect", 10, 100, 32, 300, 12, "first"),
                Para("p_middle", "sect", 11, 100, 18, 300, 12, "second"),
                Para("p_bottom", "sect", 12, 100, 4, 300, 12, "third")
            }
        };

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted", null) }
        });

        result.AppliedOpIds.Should().Equal("a1");
        result.Issues.Should().BeEmpty();
        doc.Tree.First(n => n.Id == "p_bottom").Y.Should().BeLessThan(0.0);
    }

    [Fact]
    public void AddParagraphSkipsPushDownWhenSufficientWhitespaceExists()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("sect", "#", "Sect"),
                Para("p_top", "sect", 10, 100, 700, 300, 12, "first"),
                Para("p_bottom", "sect", 11, 100, 5, 300, 12, "last")
            }
        };

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "middle", null) }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");
        doc.Tree.First(n => n.Id == "p_bottom").Y.Should().Be(5.0);
        result.Plan.MoveOverlays.Select(m => m.Mcid).Should().NotContain(11);
    }

    [Fact]
    public void AddParagraphIgnoresNonOverlappingColumnLeaf()
    {
        var doc = ThreeParagraphSect();
        doc.Tree.Add(Para("side", "#", 99, 500, 655, 100, 12, "sidebar"));

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted", null) }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");
        doc.Tree.First(n => n.Id == "side").Y.Should().Be(655.0);
    }

    [Fact]
    public void AddParagraphRejectsWhenPushDownCollidesWithNonSiblingLeaf()
    {
        var doc = ThreeParagraphSect();
        doc.Tree.Add(Para("blocker", "#", 99, 100, 655, 300, 12, "unrelated leaf"));

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "inserted", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("push-down").And.Contain("id=blocker");
        doc.Tree.First(n => n.Id == "p2").Y.Should().Be(680.0);
        doc.Tree.First(n => n.Id == "p3").Y.Should().Be(660.0);
        doc.Tree.Any(n => n.Id == "blocker").Should().BeTrue();
    }

    [Fact]
    public void AddParagraphRejectsWhenNewParagraphCollidesWithNonSiblingLeaf()
    {
        var doc = ThreeParagraphSect();
        doc.Tree.Add(Para("blocker", "#", 99, 100, 650, 300, 4, "in-the-way"));

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "P", "tail", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("new paragraph").And.Contain("id=blocker");
    }

    [Fact]
    public void AddParagraphUsesColumnScopedAlignmentInMultiColumnPage()
    {
        var doc = new DocumentJson { Tree = { Sect("sect", "#", "Sect") } };
        // Left column: 9 short entries at x=45 (dominates page modeLeft).
        for (int i = 1; i <= 9; i++)
        {
            doc.Tree.Add(Para($"t{i}", "sect", 100 + i, 45, 700 - i * 20, 150, 12, $"toc {i}"));
        }
        // Body column: full-width paragraphs at x=258.
        doc.Tree.Add(Para("b1", "sect", 200, 258, 500, 318, 12, "body first line"));
        doc.Tree.Add(Para("b2", "sect", 201, 258, 480, 318, 12, "body second line"));
        doc.Tree.Add(Para("b3", "sect", 202, 258, 460, 318, 12, "body third line"));

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 11, "P", "new body para", null) }
        });

        result.Issues.Should().BeEmpty();
        var o = result.Plan.AddParagraphOverlays[0];
        o.Alignment.Should().BeOneOf(Alignment.Justified, Alignment.Left);
    }

    [Fact]
    public void AddParagraphAppliesExplicitFontOverride()
    {
        var doc = ThreeParagraphSect();
        var spec = new StyleSpec(null, new FontOverride("Times New Roman", 18.0f, "bold", "#334455"));
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "H2", "hdr", spec) }
        });

        result.Issues.Should().BeEmpty();
        var o = result.Plan.AddParagraphOverlays[0];
        o.Tag.Should().Be("H2");
        o.Style.Family.Should().Be("Times New Roman");
        o.Style.Size.Should().Be(18.0f);
        o.Style.Weight.Should().Be("bold");
        o.Style.ColorHex.Should().Be("#334455");
    }

    [Fact]
    public void AddParagraphPartialFontOverrideKeepsUnsetFieldsFromDonor()
    {
        var doc = ThreeParagraphSect();
        var spec = new StyleSpec(null, new FontOverride("Courier New", 8.0f, null, ""));
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "P", "body", spec) }
        });

        result.Issues.Should().BeEmpty();
        var o = result.Plan.AddParagraphOverlays[0];
        o.Style.Family.Should().Be("Courier New");
        o.Style.Size.Should().Be(8.0f);
        o.Style.Weight.Should().Be("regular");
        o.Style.ColorHex.Should().Be("#000000");
    }

    [Fact]
    public void AddParagraphIgnoresFontSizeBelowFloor()
    {
        var doc = ThreeParagraphSect();
        var spec = new StyleSpec(null, new FontOverride("Arial", 2.0f, null, null));
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "P", "body", spec) }
        });

        result.Issues.Should().BeEmpty();
        result.Plan.AddParagraphOverlays[0].Style.Family.Should().Be("Arial");
        result.Plan.AddParagraphOverlays[0].Style.Size.Should().Be(10.0f);
    }

    [Fact]
    public void AddParagraphAcceptsLblIntoLiInsideL()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("L1", "#", "L"),
                Sect("li1", "L1", "LI"),
                Para("li1-lbl", "li1", 1, 60, 700, 25, 12, "1.")
            }
        };
        doc.Tree.First(n => n.Id == "li1-lbl").Text = "Lbl";

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "li1", -1, "LBody", "body", null) }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");
    }

    [Fact]
    public void AddParagraphRejectsLblOutsideLi()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", -1, "Lbl", "x", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should()
            .Contain("can only be added inside an LI")
            .And.Contain("addListItem");
    }

    [Fact]
    public void AddParagraphRejectsLBodyWhenLiParentIsNotList()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("sect", "#", "Sect"),
                Sect("orphanLi", "sect", "LI"),
                Para("orphanLi-lbl", "orphanLi", 1, 60, 700, 25, 12, "1.")
            }
        };
        doc.Tree.First(n => n.Id == "orphanLi-lbl").Text = "Lbl";

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "orphanLi", -1, "LBody", "body", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should()
            .Contain("requires the parent LI")
            .And.Contain("L/List container")
            .And.Contain("tag='Sect'");
    }

    // ============================================================================
    //  explicit-page validation
    // ============================================================================

    [Fact]
    public void SetTextValidatesExplicitPageMatch()
    {
        var doc = ThreeParagraphSect();

        var ok = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { SetTextOp.Of("e1", 1, "p1", "updated") }
        });
        ok.Issues.Should().BeEmpty();

        var bad = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { SetTextOp.Of("e1", 99, "p1", "updated") }
        });
        bad.AppliedOpIds.Should().BeEmpty();
        bad.Issues.Should().HaveCount(1);
        bad.Issues[0].Message.Should()
            .Contain("setText.page=99")
            .And.Contain("resolved page 1")
            .And.Contain("target id=p1");
    }

    [Fact]
    public void AddParagraphValidatesExplicitPageMatch()
    {
        var doc = ThreeParagraphSect();
        var bad = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", 7, "sect", -1, "P", "tail", null) }
        });

        bad.AppliedOpIds.Should().BeEmpty();
        bad.Issues.Should().HaveCount(1);
        bad.Issues[0].Message.Should()
            .Contain("addParagraph.page=7")
            .And.Contain("resolved page 1");
    }

    [Fact]
    public void DeleteNodeValidatesExplicitPageMatch()
    {
        var doc = ThreeParagraphSect();
        var bad = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { DeleteNodeOp.Of("d1", 42, "p1") }
        });

        bad.AppliedOpIds.Should().BeEmpty();
        bad.Issues.Should().HaveCount(1);
        bad.Issues[0].Message.Should().Contain("deleteNode.page=42");
    }

    [Fact]
    public void AddListItemValidatesExplicitPageMatch()
    {
        var doc = TwoListItemL();
        var bad = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", 5, "L1", -1, "3.", "body", null) }
        });

        bad.AppliedOpIds.Should().BeEmpty();
        bad.Issues.Should().HaveCount(1);
        bad.Issues[0].Message.Should().Contain("addListItem.page=5");
    }

    [Fact]
    public void AddParagraphRejectsInheritFromUnknown()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "sect", 1, "P", "x", new StyleSpec("nope")) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("nope");
    }

    // ============================================================================
    //  addListItem engine tests (synthetic docs)
    // ============================================================================

    [Fact]
    public void AddListItemAppendsWithLblAndLBodyInheritingDonorColumns()
    {
        var doc = TwoListItemL();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "L1", -1, "3.", "Third item body text", null) }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");

        var liChildren = doc.Tree.Where(n => n.Parent == "L1").ToList();
        liChildren.Select(n => n.Text).Should().Equal("LI", "LI", "LI");
        var newLi = doc.Tree.Last(n => n.Text == "LI" && n.Parent == "L1");
        var newKids = doc.Tree.Where(n => n.Parent == newLi.Id).ToList();
        newKids.Select(n => n.Text).Should().Equal("Lbl", "LBody");
        newKids[0].Content.Should().Be("3.");
        newKids[1].Content.Should().Be("Third item body text");
        newKids[0].X.Should().Be(60.0);
        newKids[1].X.Should().Be(90.0);
        newKids[0].Y.Should().BeLessThan(680.0);
        newKids[1].Y.Should().Be(newKids[0].Y);

        result.Plan.AddListItemOverlays.Should().HaveCount(1);
        var o = result.Plan.AddListItemOverlays[0];
        o.LabelText.Should().Be("3.");
        o.BodyText.Should().Be("Third item body text");
        o.DonorLiMcid.Should().Be(4);
        o.LblStyle.Should().NotBeNull();
        o.BodyStyle.Should().NotBeNull();
        result.Plan.MoveOverlays.Should().BeEmpty();
    }

    [Fact]
    public void AddListItemOverlayCarriesIndependentLblAndBodyStyles()
    {
        var lblStyle = new FontStyle("Symbol", 8f, "regular", "#333333");
        var bodyStyle = new FontStyle("Arial", 12f, "regular", "#000000");
        var o = new AddListItemOverlay(
            1,
            60.0, 700.0, 25.0, 14.4, "•",
            90.0, 700.0, 300.0, 14.4, "New list body text",
            lblStyle, bodyStyle, Alignment.Left,
            "new-li", "new-lbl", "new-body",
            1, 4);
        o.LblStyle.Should().BeSameAs(lblStyle);
        o.BodyStyle.Should().BeSameAs(bodyStyle);
        o.LblStyle.Size.Should().Be(8f);
        o.BodyStyle.Size.Should().Be(12f);
        o.LblStyle.Family.Should().Be("Symbol");
        o.BodyStyle.Family.Should().Be("Arial");
    }

    [Fact]
    public void AddListItemOverlayBackCompatSingleStyleAppliesToBoth()
    {
        var single = new FontStyle("Arial", 10f, "regular", "#000000");
        var o = new AddListItemOverlay(
            1,
            60.0, 700.0, 25.0, 12.0, "1.",
            90.0, 700.0, 300.0, 12.0, "Body",
            single, Alignment.Left,
            "new-li", "new-lbl", "new-body",
            1, 4);
        o.LblStyle.Should().BeSameAs(single);
        o.BodyStyle.Should().BeSameAs(single);
    }

    [Fact]
    public void AddListItemInsertBetweenPushesLaterLIs()
    {
        var doc = TwoListItemL();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "L1", 1, "1.5", "Inserted between", null) }
        });

        result.Issues.Should().BeEmpty();
        // shiftAmount = 12.24 + 6.96 = 19.2
        doc.Tree.First(n => n.Id == "li2-lbl").Y.Should().Be(680.0 - 19.2);
        doc.Tree.First(n => n.Id == "li2-body").Y.Should().Be(680.0 - 19.2);

        result.Plan.MoveOverlays.Should().HaveCount(2);
        result.Plan.MoveOverlays.Select(m => m.Mcid).Should().BeEquivalentTo(new[] { 3, 4 });
    }

    [Fact]
    public void AddListItemRejectsNonListParent()
    {
        var doc = ThreeParagraphSect();
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "sect", -1, "•", "x", null) }
        });

        result.AppliedOpIds.Should().BeEmpty();
        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should().Contain("only L/List containers accept a new LI");
    }

    [Fact]
    public void AddListItemRejectsEmptyL()
    {
        var doc = new DocumentJson { Tree = { Sect("L1", "#", "L") } };
        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "L1", -1, "•", "x", null) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should()
            .Contain("no existing LI children")
            .And.Contain("Empty-list insertion is out of POC scope");
    }

    [Fact]
    public void AddListItemRejectsDonorMissingLbl()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("L1", "#", "L"),
                Sect("li1", "L1", "LI"),
            }
        };
        var li1Body = Para("li1-body", "li1", 2, 90, 700, 300, 12, "body");
        li1Body.Text = "LBody";
        doc.Tree.Add(li1Body);

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "L1", -1, "•", "x", null) }
        });

        result.Issues.Should().HaveCount(1);
        result.Issues[0].Message.Should()
            .Contain("both an Lbl and an LBody")
            .And.Contain("Lbl=False, LBody=True");
    }

    [Fact]
    public void AddListItemAppendShiftsContentAfterListContainer()
    {
        var doc = TwoListItemL();
        doc.Tree.Add(Para("blocker", "#", 99, 100, 670, 300, 12, "in the way"));

        var result = new EditEngine(null).Apply(doc, new EditsJson
        {
            Operations = { AddListItemOp.Of("a1", "L1", -1, "•", "x", null) }
        });

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("a1");
        doc.Tree.First(n => n.Id == "blocker").Y.Should().Be(670.0 - 19.2);
        result.Plan.MoveOverlays.Select(m => m.Mcid).Should().Contain(99);
    }

    // ============================================================================
    //  Helpers
    // ============================================================================

    private static TreeNode Node(string id, double x, double width) => new()
    {
        Id = id,
        Parent = "#",
        Text = "P",
        Page = 1,
        Mcid = Math.Abs(id.GetHashCode()) % 1000,
        X = x,
        Y = 700,
        Width = width,
        Height = 12,
        Content = "sample"
    };

    private static Glyph GlyphAt(double x, double y, double w, double h) => new()
    {
        X = x, Y = y, Width = w, Height = h
    };

    /// <summary>Synthetic list container: L { LI1 { Lbl, LBody }, LI2 { Lbl, LBody } }.</summary>
    private static DocumentJson TwoListItemL()
    {
        var doc = new DocumentJson
        {
            Tree =
            {
                Sect("L1", "#", "L"),
                Sect("li1", "L1", "LI"),
                Para("li1-lbl", "li1", 1, 60, 700, 25, 12, "1.")
            }
        };
        var li1Body = Para("li1-body", "li1", 2, 90, 700, 300, 12, "First item");
        li1Body.Text = "LBody";
        doc.Tree.Add(li1Body);
        doc.Tree.First(n => n.Id == "li1-lbl").Text = "Lbl";

        doc.Tree.Add(Sect("li2", "L1", "LI"));
        doc.Tree.Add(Para("li2-lbl", "li2", 3, 60, 680, 25, 12, "2."));
        var li2Body = Para("li2-body", "li2", 4, 90, 680, 300, 12, "Second item");
        li2Body.Text = "LBody";
        doc.Tree.Add(li2Body);
        doc.Tree.First(n => n.Id == "li2-lbl").Text = "Lbl";
        return doc;
    }

    /// <summary>Synthetic doc: sect { p1, p2, p3 } all on page 1, x=100 width=300, 20pt apart.</summary>
    private static DocumentJson ThreeParagraphSect() => new()
    {
        Tree =
        {
            Sect("sect", "#", "Sect"),
            Para("p1", "sect", 5, 100, 700, 300, 12, "first"),
            Para("p2", "sect", 6, 100, 680, 300, 12, "second"),
            Para("p3", "sect", 7, 100, 660, 300, 12, "third")
        }
    };

    private static TreeNode Sect(string id, string parent, string tag) => new()
    {
        Id = id,
        Parent = parent,
        Text = tag,
        Page = 1,
        Mcid = -1
    };

    private static TreeNode Para(string id, string parent, int mcid,
        double x, double y, double w, double h, string content) => new()
    {
        Id = id,
        Parent = parent,
        Text = "P",
        Page = 1,
        Mcid = mcid,
        X = x,
        Y = y,
        Width = w,
        Height = h,
        Content = content
    };

    // ============================================================================
    //  End-to-end tests (corpus + writer)
    // ============================================================================

    [FactIfSample(PdfPath)]
    public void EndToEndEditProducesPdfContainingNewText()
    {
        var docPath = TestSamples.Resolve(DocPath);
        var geomPath = TestSamples.Resolve(GeomPath);
        var pdfPath = TestSamples.Resolve(PdfPath);

        var doc = DocumentJsonLoader.Load(docPath);
        var geom = GeometryJsonLoader.Load(geomPath);
        geom.Should().NotBeNull();

        var newText = "smart notes";
        var edits = new EditsJson { Operations = { SetTextOp.Of("e1", "3", newText) } };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForSample(SampleDir), SampleDir + "_edit.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().Contain(newText);
        page1.Should().NotContain("North Dakota Office of State Tax Commissioner");
        page1.Should().Contain("Form 40X Amended Corporation");
    }

    [FactIfSample(PdfPath)]
    public void EndToEndAddParagraphStampsTaggedBlock()
    {
        var docPath = TestSamples.Resolve(DocPath);
        var pdfPath = TestSamples.Resolve(PdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        var newText = "APEX-ADDED-POC";
        var edits = new EditsJson
        {
            Operations = { AddParagraphOp.Of("a1", "2", -1, "P", newText, null) }
        };

        // Baseline P count from empty-plan write.
        var baselineOut = new MemoryStream();
        new SourceBasedWriter(pdfPath).Write(EditPlan.Empty(), baselineOut);
        int pRolesBaseline;
        using (var r = new PdfReader(new MemoryStream(baselineOut.ToArray())))
        using (var baseline = new PdfDocument(r))
        {
            pRolesBaseline = CountPRoles(baseline);
        }

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            result.AppliedOpIds.Should().Equal("a1");
            result.Plan.AddParagraphOverlays.Should().HaveCount(1);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForSample(SampleDir), SampleDir + "_add.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().Contain(newText);
        page1.Should().Contain("North Dakota Office of State Tax Commissioner");
        page1.Should().Contain("Form 40X Amended Corporation");
        CountPRoles(pdf).Should().Be(pRolesBaseline + 1);
    }

    [FactIfSample(PdfPath)]
    public void EndToEndDeleteNodeDropsTaggedBlock()
    {
        var docPath = TestSamples.Resolve(DocPath);
        var pdfPath = TestSamples.Resolve(PdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        var edits = new EditsJson { Operations = { DeleteNodeOp.Of("d1", "3") } };

        var baselineOut = new MemoryStream();
        new SourceBasedWriter(pdfPath).Write(EditPlan.Empty(), baselineOut);
        int pRolesBaseline;
        using (var r = new PdfReader(new MemoryStream(baselineOut.ToArray())))
        using (var baseline = new PdfDocument(r))
        {
            pRolesBaseline = CountPRoles(baseline);
        }

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            result.AppliedOpIds.Should().Equal("d1");
            result.Plan.DeleteOverlays.Should().HaveCount(1);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForSample(SampleDir), SampleDir + "_delete.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().NotContain("North Dakota Office of State Tax Commissioner");
        page1.Should().Contain("Form 40X Amended Corporation");
        CountPRoles(pdf).Should().Be(pRolesBaseline - 1);
    }

    private const string ProxyDir = "2026 Proxy 2.24.26_WEB_ADA";
    private const string ProxyPdfPath = ProxyDir + "/" + ProxyDir + ".pdf";

    [FactIfSample(ProxyPdfPath)]
    public void DeleteInsideSharedBtPreservesDownstreamTextMatrix()
    {
        var docPath = TestSamples.Resolve(ProxyDir + "/" + ProxyDir + "-document.json");
        var pdfPath = TestSamples.Resolve(ProxyPdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        // Delete node id 164 — Proxy p6 MCID 20, H2 "Voting".
        var edits = new EditsJson { Operations = { DeleteNodeOp.Of("d1", "164") } };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            result.AppliedOpIds.Should().Equal("d1");
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);

        double minX = double.PositiveInfinity;
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (string.IsNullOrWhiteSpace(s)) return;
            double x = tri.GetBaseline().GetStartPoint().Get(0);
            if (x < minX) minX = x;
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(6));
        minX.Should().BeGreaterThanOrEqualTo(0.0,
            "no rendered chunk on p6 at negative x (pre-fix regressed left-shift)");
    }

    private const string CareDir = "CARE Application_Espanol-Remediated";
    private const string CarePdfPath = CareDir + "/" + CareDir + ".pdf";

    [FactIfSample(CarePdfPath)]
    public void DeletePreservesColorStateForDownstreamMcids()
    {
        var docPath = TestSamples.Resolve(CareDir + "/" + CareDir + "-document.json");
        var pdfPath = TestSamples.Resolve(CarePdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        var edits = new EditsJson { Operations = { DeleteNodeOp.Of("d1", "188") } };
        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);

        float maxComp = -1f;
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (s is null || !s.Contains("INGRESO", StringComparison.Ordinal)) return;
            var color = tri.GetFillColor();
            if (color is null) return;
            float m = 0f;
            foreach (var c in color.GetColorValue())
            {
                if (c > m) m = c;
            }
            if (m > maxComp) maxComp = m;
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(2));
        maxComp.Should().BeGreaterThanOrEqualTo(0f)
            .And.BeLessThan(0.5f, "INGRESO chunk on p2 should have a dark fill (max RGB < 0.5)");
    }

    private const string BoardDir = "05-15-2025 Board Packet-Remediated";
    private const string BoardPdfPath = BoardDir + "/" + BoardDir + ".pdf";

    [FactIfSample(BoardPdfPath)]
    public void SetTextRestoresGlobalCursorForRelativeDownstreamTd()
    {
        var docPath = TestSamples.Resolve(BoardDir + "/" + BoardDir + "-document.json");
        var pdfPath = TestSamples.Resolve(BoardPdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        var edits = new EditsJson
        {
            Operations =
            {
                SetTextOp.Of("s1", "124",
                    "APEX EDIT: Commissioner Pearson opened the session at 4:00 p.m")
            }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var strategy = new LocationTextExtractionStrategy();
        var p3 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(3), strategy);
        int apexIdx = p3.IndexOf("APEX EDIT:", StringComparison.Ordinal);
        int uponIdx = p3.IndexOf("Upon roll call", StringComparison.Ordinal);
        apexIdx.Should().BeGreaterThanOrEqualTo(0, "APEX EDIT marker present on p3");
        uponIdx.Should().BeGreaterThanOrEqualTo(0, "Upon roll call present on p3");
        uponIdx.Should().BeGreaterThan(apexIdx, "line order preserved");
        var between = p3.Substring(apexIdx, uponIdx - apexIdx);
        between.Should().Contain("\n",
            "newline separates setText replacement from downstream MCID (Td compensation ok)");
    }

    private const string EaDir = "EA Application_English-Remediated";
    private const string EaPdfPath = EaDir + "/" + EaDir + ".pdf";

    [FactIfSample(EaPdfPath)]
    public void SetTextPicksExactSourceFontByObjNumberForTwinFonts()
    {
        var docPath = TestSamples.Resolve(EaDir + "/" + EaDir + "-document.json");
        var pdfPath = TestSamples.Resolve(EaPdfPath);
        var doc = DocumentJsonLoader.Load(docPath);

        var edits = new EditsJson
        {
            Operations =
            {
                SetTextOp.Of("s1", "172",
                    "edit. Your answers will not affect your eligibility for assistance."),
                SetTextOp.Of("s2", "186",
                    "APEX EDIT: I skipped meals so that I could pay my energy bill")
            }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var p1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        p1.Should().Contain("edit. Your answers will not affect your eligibility for assistance.",
            "target 172 (source /C2_0 Type0 Bold) — full text extractable via exact-font pick");
        p1.Should().Contain("APEX EDIT: I skipped meals so that I could pay my energy bill",
            "target 186 (source /TT0 simple Regular) — full text extractable via exact-font pick");
    }

    private static int CountPRoles(PdfDocument pdf)
        => CountKidsWithRole(pdf.GetStructTreeRoot().GetKids(), PdfName.P);

    private static int CountKidsWithRole(IList<IStructureNode>? kids, PdfName role)
    {
        if (kids is null) return 0;
        int total = 0;
        foreach (var kid in kids)
        {
            if (kid is null) continue;
            // Only count PdfStructElem — MCR.GetRole() delegates to parent so double-counts.
            if (kid is PdfStructElem && role.Equals(kid.GetRole()))
            {
                total++;
            }
            total += CountKidsWithRole(kid.GetKids(), role);
        }
        return total;
    }

    /// <summary>
    /// Callback-based <see cref="IEventListener"/> for capturing text render events in
    /// tests. Java uses inline anonymous classes; C# uses this small helper + a lambda.
    /// </summary>
    private sealed class TextRenderListener : IEventListener
    {
        private static readonly ICollection<EventType> Supported = new HashSet<EventType> { EventType.RENDER_TEXT };
        private readonly Action<TextRenderInfo> _onText;

        public TextRenderListener(Action<TextRenderInfo> onText) { _onText = onText; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            if (data is TextRenderInfo tri) _onText(tri);
        }

        public ICollection<EventType> GetSupportedEvents() => Supported;
    }
}
