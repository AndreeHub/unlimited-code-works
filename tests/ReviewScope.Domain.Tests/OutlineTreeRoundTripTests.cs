using ReviewScope.Domain.Outline;
using Xunit;

namespace ReviewScope.Domain.Tests;

public class OutlineTreeRoundTripTests
{
    [Theory]
    [InlineData("")]
    [InlineData("- a")]
    [InlineData("- a\n- b\n- c")]
    [InlineData("- a\n  - b\n  - c\n- d")]
    [InlineData("- a\n  - b\n    - c\n- d")]
    [InlineData("- parent\n  - child ^1a2b3c4d")]
    [InlineData("- TODO write it\n- DONE shipped")]
    [InlineData("- with **bold** and #tag and [[ref]]")]
    [InlineData("- trailing space \n- next")]
    [InlineData("- a\n")]                  // trailing empty line
    [InlineData("- a\n\n- b")]             // blank line between bullets
    [InlineData("plain line no bullet")]   // non-bullet line
    [InlineData("- a\n* star\n• dot")]     // alternate bullet glyphs
    [InlineData("- a\n\t- tab indented")]  // tab indentation
    public void Parse_then_Serialize_is_byte_stable(string markdown)
    {
        var tree = OutlineTree.Parse(markdown);
        Assert.Equal(markdown, tree.Serialize());
    }

    [Fact]
    public void Parse_builds_nested_structure()
    {
        var tree = OutlineTree.Parse("- a\n  - b\n  - c\n- d");

        Assert.Equal(2, tree.Roots.Count);
        Block a = tree.Roots[0];
        Assert.Equal("a", a.Content);
        Assert.Equal(2, a.Children.Count);
        Assert.Equal("b", a.Children[0].Content);
        Assert.Equal("c", a.Children[1].Content);
        Assert.Equal("d", tree.Roots[1].Content);
        Assert.Empty(tree.Roots[1].Children);
    }

    [Fact]
    public void Parse_splits_content_from_prefix_and_anchor()
    {
        var tree = OutlineTree.Parse("  - hello world ^deadbeef");
        Block b = tree.Roots[0];

        Assert.Equal("hello world", b.Content);
        Assert.Equal("deadbeef", b.Id);
    }

    [Fact]
    public void Collapsed_is_set_from_anchor_ids_and_not_serialized()
    {
        const string md = "- parent ^11111111\n  - child";
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "11111111" };

        var tree = OutlineTree.Parse(md, collapsed);

        Assert.True(tree.Roots[0].Collapsed);
        Assert.False(tree.Roots[0].Children[0].Collapsed);
        // Collapse lives outside the body — serialization must not encode it.
        Assert.Equal(md, tree.Serialize());
    }

    [Fact]
    public void Anchor_only_recognized_as_exactly_eight_hex_chars_at_end()
    {
        // " ^xyz" is not a valid 8-hex anchor, so it stays part of the content.
        var tree = OutlineTree.Parse("- not an anchor ^xyz");
        Assert.Null(tree.Roots[0].Id);
        Assert.Equal("not an anchor ^xyz", tree.Roots[0].Content);
    }

    [Fact]
    public void SerializeSubtree_rebases_deep_bullet_to_depth_zero()
    {
        // A bullet referenced for transclusion may sit arbitrarily deep in its source doc;
        // SerializeSubtree must re-base it (and its children) to canonical depth-0 markdown.
        var tree = OutlineTree.Parse("- root\n    - target ^abcdef12\n      - kid\n        - grandkid");
        Block target = tree.Roots[0].Children[0];

        string subtree = OutlineTree.SerializeSubtree(target);

        Assert.Equal("- target ^abcdef12\n  - kid\n    - grandkid", subtree);
    }

    [Fact]
    public void SerializeSubtree_of_leaf_emits_single_line_with_anchor()
    {
        var tree = OutlineTree.Parse("- a\n  - leaf ^00ff00ff");
        Block leaf = tree.Roots[0].Children[0];

        Assert.Equal("- leaf ^00ff00ff", OutlineTree.SerializeSubtree(leaf));
    }
}
