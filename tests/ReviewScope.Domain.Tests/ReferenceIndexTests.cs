using ReviewScope.Domain;
using ReviewScope.Domain.Outline;
using Xunit;

namespace ReviewScope.Domain.Tests;

public class OutlineReferencesScanTests
{
    [Fact]
    public void Scan_finds_page_links()
    {
        var refs = OutlineReferences.Scan("see [[Design Notes]] and [[2026-05-29]]").ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal(new OutlineReference(OutlineReferenceKind.Page, "Design Notes"), refs[0]);
        Assert.Equal(new OutlineReference(OutlineReferenceKind.Page, "2026-05-29"), refs[1]);
    }

    [Fact]
    public void Scan_finds_block_refs_and_strips_caret()
    {
        var refs = OutlineReferences.Scan("as in ((^1a2b3c4d)) above").ToList();
        Assert.Single(refs);
        Assert.Equal(new OutlineReference(OutlineReferenceKind.Block, "1a2b3c4d"), refs[0]);
    }

    [Fact]
    public void Scan_handles_mixed_and_ignores_unterminated()
    {
        var refs = OutlineReferences.Scan("[[A]] ((^deadbeef)) [[unterminated ((also").ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal(OutlineReferenceKind.Page, refs[0].Kind);
        Assert.Equal(OutlineReferenceKind.Block, refs[1].Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no refs here")]
    [InlineData("[[]]")]   // empty interior is not a ref
    [InlineData("(())")]
    public void Scan_returns_nothing(string content) =>
        Assert.Empty(OutlineReferences.Scan(content));
}

public class ReferenceIndexTests
{
    private static DocumentSource Page(string name, string body) =>
        new(Guid.NewGuid(), name, DocumentKind.Page, body);

    [Fact]
    public void Resolves_page_by_name_case_insensitively()
    {
        var page = Page("Design Notes", "- hello");
        var index = ReferenceIndex.Build(new[] { page });

        Assert.True(index.TryResolvePage("design notes", out var doc));
        Assert.Equal(page.Id, doc.Id);
        Assert.Equal(DocumentKind.Page, doc.Kind);
    }

    [Fact]
    public void Canvas_documents_are_not_page_link_targets()
    {
        var canvas = new DocumentSource(Guid.NewGuid(), "Board", DocumentKind.Canvas, null);
        var index = ReferenceIndex.Build(new[] { canvas });
        Assert.False(index.TryResolvePage("Board", out _));
    }

    [Fact]
    public void Resolves_block_by_anchor_to_its_document_and_bullet()
    {
        var a = Page("Page A", "- first ^1a2b3c4d\n  - child");
        var b = Page("Page B", "- elsewhere");
        var index = ReferenceIndex.Build(new[] { a, b });

        Assert.True(index.TryResolveBlock("^1a2b3c4d", out var loc));
        Assert.Equal(a.Id, loc.Document.Id);
        Assert.Equal("first", loc.Block.Content);
        Assert.Single(loc.Block.Children);
        Assert.Equal("child", loc.Block.Children[0].Content);
    }

    [Fact]
    public void Unknown_targets_do_not_resolve()
    {
        var index = ReferenceIndex.Build(new[] { Page("P", "- x ^aaaaaaaa") });
        Assert.False(index.TryResolvePage("Missing", out _));
        Assert.False(index.TryResolveBlock("ffffffff", out _));
    }

    [Fact]
    public void Empty_index_resolves_nothing()
    {
        Assert.False(ReferenceIndex.Empty.TryResolvePage("anything", out _));
        Assert.False(ReferenceIndex.Empty.TryResolveBlock("deadbeef", out _));
    }
}
