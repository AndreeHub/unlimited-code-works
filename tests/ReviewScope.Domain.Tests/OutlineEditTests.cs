using ReviewScope.Domain.Outline;
using Xunit;

namespace ReviewScope.Domain.Tests;

public class OutlineEditTests
{
    // --- caret helpers: offsets into the serialized markdown ----------------------

    private static int StartOfContent(string md, int line)
    {
        string[] parts = md.Replace("\r\n", "\n").Split('\n');
        int pos = 0;
        for (int i = 0; i < line; i++) pos += parts[i].Length + 1;
        string t = parts[line];
        int p = 0;
        while (p < t.Length && (t[p] == ' ' || t[p] == '\t')) p++;
        if (p + 2 <= t.Length)
        {
            string token = t.Substring(p, 2);
            if (token == "- " || token == "* " || token == "• ") p += 2;
        }
        return pos + p;
    }

    private static int EndOfLine(string md, int line)
    {
        string[] parts = md.Replace("\r\n", "\n").Split('\n');
        int pos = 0;
        for (int i = 0; i < line; i++) pos += parts[i].Length + 1;
        return pos + parts[line].Length;
    }

    // --- Enter --------------------------------------------------------------------

    [Fact]
    public void Enter_at_end_of_expanded_parent_inserts_first_child()
    {
        var tree = OutlineTree.Parse("- a\n  - b");
        int caret = OutlineEdit.EnterKey(tree, EndOfLine("- a\n  - b", 0));

        Assert.Equal("- a\n  - \n  - b", tree.Serialize());
        Assert.Equal(8, caret); // start of the new (empty) first child's content
    }

    [Fact]
    public void Enter_at_end_of_collapsed_block_inserts_sibling_after_subtree()
    {
        const string md = "- a ^11111111\n  - b";
        var tree = OutlineTree.Parse(md, new HashSet<string> { "11111111" });
        int caret = OutlineEdit.EnterKey(tree, StartOfContent(md, 0) + 1); // end of "a"

        Assert.Equal("- a ^11111111\n  - b\n- ", tree.Serialize());
        Assert.Equal(22, caret);
    }

    [Fact]
    public void Enter_at_end_of_leaf_inserts_sibling()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        int caret = OutlineEdit.EnterKey(tree, EndOfLine("- a\n- b", 0));

        Assert.Equal("- a\n- \n- b", tree.Serialize());
        Assert.Equal(6, caret);
    }

    [Fact]
    public void Enter_mid_text_splits_keeping_text_after_caret()
    {
        var tree = OutlineTree.Parse("- hello");
        int caret = OutlineEdit.EnterKey(tree, StartOfContent("- hello", 0) + 2); // "he|llo"

        Assert.Equal("- he\n- llo", tree.Serialize());
        Assert.Equal(7, caret); // start of "llo"
    }

    [Fact]
    public void Enter_mid_text_of_expanded_parent_puts_remainder_as_first_child()
    {
        var tree = OutlineTree.Parse("- hello\n  - c");
        int caret = OutlineEdit.EnterKey(tree, StartOfContent("- hello\n  - c", 0) + 2);

        Assert.Equal("- he\n  - llo\n  - c", tree.Serialize());
        Assert.Equal(9, caret);
    }

    [Fact]
    public void Enter_on_indented_empty_bullet_outdents_it()
    {
        var tree = OutlineTree.Parse("- a\n  - ");
        int caret = OutlineEdit.EnterKey(tree, EndOfLine("- a\n  - ", 1));

        Assert.Equal("- a\n- ", tree.Serialize());
        Assert.Equal(6, caret);
    }

    [Fact]
    public void Enter_on_top_level_empty_bullet_strips_to_blank_line()
    {
        var tree = OutlineTree.Parse("- a\n- ");
        int caret = OutlineEdit.EnterKey(tree, EndOfLine("- a\n- ", 1));

        Assert.Equal("- a\n", tree.Serialize());
        Assert.Equal(4, caret);
    }

    // --- Backspace ----------------------------------------------------------------

    [Fact]
    public void Backspace_at_start_merges_into_previous_block()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.Backspace(tree, StartOfContent("- a\n- b", 1));

        Assert.True(r.Handled);
        Assert.Equal("- ab", tree.Serialize());
        Assert.Equal(3, r.Caret); // at the join point, between "a" and "b"
    }

    [Fact]
    public void Backspace_at_start_reparents_merged_children_to_previous()
    {
        var tree = OutlineTree.Parse("- a\n- b\n  - c");
        var r = OutlineEdit.Backspace(tree, StartOfContent("- a\n- b\n  - c", 1));

        Assert.True(r.Handled);
        Assert.Equal("- ab\n  - c", tree.Serialize());
        Assert.Equal(3, r.Caret);
    }

    [Fact]
    public void Backspace_not_at_start_is_not_handled()
    {
        var tree = OutlineTree.Parse("- ab");
        var r = OutlineEdit.Backspace(tree, EndOfLine("- ab", 0)); // offset 2, mid/end content

        Assert.False(r.Handled);
        Assert.Equal("- ab", tree.Serialize());
    }

    [Fact]
    public void Backspace_at_start_of_first_block_is_not_handled()
    {
        var tree = OutlineTree.Parse("- a");
        var r = OutlineEdit.Backspace(tree, StartOfContent("- a", 0));

        Assert.False(r.Handled);
        Assert.Equal("- a", tree.Serialize());
    }

    // --- Tab / Shift+Tab ----------------------------------------------------------

    [Fact]
    public void Indent_demotes_under_previous_sibling()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.Indent(tree, StartOfContent("- a\n- b", 1));

        Assert.True(r.Handled);
        Assert.Equal("- a\n  - b", tree.Serialize());
        Assert.Equal(8, r.Caret);
    }

    [Fact]
    public void Indent_without_previous_sibling_is_no_op()
    {
        var tree = OutlineTree.Parse("- a\n  - b");
        var r = OutlineEdit.Indent(tree, StartOfContent("- a\n  - b", 1)); // b is first child

        Assert.False(r.Handled);
        Assert.Equal("- a\n  - b", tree.Serialize());
    }

    [Fact]
    public void Indent_of_top_level_first_block_is_no_op()
    {
        var tree = OutlineTree.Parse("- a");
        var r = OutlineEdit.Indent(tree, StartOfContent("- a", 0));

        Assert.False(r.Handled);
        Assert.Equal("- a", tree.Serialize());
    }

    [Fact]
    public void Outdent_promotes_and_adopts_following_siblings()
    {
        var tree = OutlineTree.Parse("- a\n  - b\n  - c");
        var r = OutlineEdit.Outdent(tree, StartOfContent("- a\n  - b\n  - c", 1)); // b

        Assert.True(r.Handled);
        Assert.Equal("- a\n- b\n  - c", tree.Serialize());
        Assert.Equal(6, r.Caret);
    }

    [Fact]
    public void Outdent_at_top_level_is_no_op()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.Outdent(tree, StartOfContent("- a\n- b", 0));

        Assert.False(r.Handled);
        Assert.Equal("- a\n- b", tree.Serialize());
    }

    // --- Move ---------------------------------------------------------------------

    [Fact]
    public void MoveUp_swaps_with_previous_sibling()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.MoveUp(tree, StartOfContent("- a\n- b", 1)); // b

        Assert.True(r.Handled);
        Assert.Equal("- b\n- a", tree.Serialize());
        Assert.Equal(2, r.Caret);
    }

    [Fact]
    public void MoveUp_at_top_is_no_op()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.MoveUp(tree, StartOfContent("- a\n- b", 0));

        Assert.False(r.Handled);
        Assert.Equal("- a\n- b", tree.Serialize());
    }

    [Fact]
    public void MoveDown_swaps_with_next_sibling()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.MoveDown(tree, StartOfContent("- a\n- b", 0)); // a

        Assert.True(r.Handled);
        Assert.Equal("- b\n- a", tree.Serialize());
        Assert.Equal(6, r.Caret);
    }

    [Fact]
    public void MoveDown_at_bottom_is_no_op()
    {
        var tree = OutlineTree.Parse("- a\n- b");
        var r = OutlineEdit.MoveDown(tree, StartOfContent("- a\n- b", 1));

        Assert.False(r.Handled);
        Assert.Equal("- a\n- b", tree.Serialize());
    }

    [Fact]
    public void Move_keeps_subtree_attached()
    {
        var tree = OutlineTree.Parse("- a\n  - a1\n- b");
        var r = OutlineEdit.MoveDown(tree, StartOfContent("- a\n  - a1\n- b", 0)); // a + its child

        Assert.True(r.Handled);
        Assert.Equal("- b\n- a\n  - a1", tree.Serialize());
    }
}
