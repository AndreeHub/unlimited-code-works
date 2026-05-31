using ReviewScope.Domain.Outline;
using Xunit;

namespace ReviewScope.Domain.Tests;

/// <summary>
/// Exercises the host-independent editing behavior the controller adds on top of
/// <see cref="OutlineEdit"/>: selection primitives, character-level Backspace/Delete,
/// inline-wrap toggling, and caret navigation. The structural ops (Enter/Indent/Move)
/// are covered by <see cref="OutlineEditTests"/>; here we only confirm the controller
/// routes through them and keeps caret/selection state coherent.
/// </summary>
public class OutlineEditControllerTests
{
    private static OutlineEditController At(string body, int cursor) =>
        new() { Body = body, CursorPos = cursor };

    // --- selection primitives -----------------------------------------------------

    [Fact]
    public void GetSelection_is_null_without_anchor_or_when_empty()
    {
        var c = At("hello", 2);
        Assert.Null(c.GetSelection());

        c.SelectionAnchor = 2; // anchored at the caret => empty selection
        Assert.Null(c.GetSelection());
    }

    [Fact]
    public void GetSelection_orders_endpoints_regardless_of_drag_direction()
    {
        var c = At("hello", 1) ;
        c.SelectionAnchor = 4;
        Assert.Equal((1, 4), c.GetSelection());
    }

    [Fact]
    public void DeleteSelection_removes_range_and_collapses_caret()
    {
        var c = At("hello", 4);
        c.SelectionAnchor = 1;
        Assert.True(c.DeleteSelection());
        Assert.Equal("ho", c.Body);
        Assert.Equal(1, c.CursorPos);
        Assert.Equal(-1, c.SelectionAnchor);
    }

    [Fact]
    public void InsertText_replaces_selection_then_advances_caret()
    {
        var c = At("hello", 4);
        c.SelectionAnchor = 1;
        c.InsertText("XY");
        Assert.Equal("hXYo", c.Body);
        Assert.Equal(3, c.CursorPos);
    }

    // --- character delete ---------------------------------------------------------

    [Fact]
    public void Backspace_mid_content_deletes_one_char()
    {
        var c = At("- abc", 5); // caret after "c"
        Assert.True(c.Backspace());
        Assert.Equal("- ab", c.Body);
        Assert.Equal(4, c.CursorPos);
    }

    [Fact]
    public void Backspace_at_block_start_merges_into_previous()
    {
        var c = At("- a\n- b", 6); // caret at start of "b" content
        Assert.True(c.Backspace());
        Assert.Equal("- ab", c.Body);
    }

    [Fact]
    public void Delete_removes_char_after_caret()
    {
        var c = At("abc", 1);
        c.Delete();
        Assert.Equal("ac", c.Body);
        Assert.Equal(1, c.CursorPos);
    }

    // --- inline wrap toggle -------------------------------------------------------

    [Fact]
    public void WrapSelection_wraps_then_unwraps_the_same_selection()
    {
        var c = At("- abc", 2);
        c.SelectionAnchor = 5; // select "abc"
        c.WrapSelection("**", "**");
        Assert.Equal("- **abc**", c.Body);
        Assert.Equal((4, 7), c.GetSelection()); // selection still spans "abc"

        c.WrapSelection("**", "**"); // toggle off
        Assert.Equal("- abc", c.Body);
    }

    [Fact]
    public void WrapSelection_without_selection_parks_caret_between_markers()
    {
        var c = At("- ", 2);
        c.WrapSelection("`", "`");
        Assert.Equal("- ``", c.Body);
        Assert.Equal(3, c.CursorPos); // between the back-ticks
    }

    // --- caret navigation ---------------------------------------------------------

    [Fact]
    public void MoveRight_collapses_an_existing_selection_to_its_end()
    {
        var c = At("hello", 1);
        c.SelectionAnchor = 4;
        c.MoveRight(extend: false);
        Assert.Equal(4, c.CursorPos);
        Assert.Equal(-1, c.SelectionAnchor);
    }

    [Fact]
    public void Shift_arrow_starts_and_extends_a_selection()
    {
        var c = At("hello", 1);
        c.MoveRight(extend: true);
        Assert.Equal((1, 2), c.GetSelection());
    }

    [Fact]
    public void Home_and_End_snap_within_the_current_line()
    {
        var c = At("ab\ncd", 4); // on the second line
        c.MoveHome(extend: false);
        Assert.Equal(3, c.CursorPos);
        c.MoveEnd(extend: false);
        Assert.Equal(5, c.CursorPos);
    }

    [Fact]
    public void LineDown_preserves_column_then_clamps_to_shorter_line()
    {
        var c = At("abcd\nxy", 3); // column 3 on the first line
        c.MoveLineDown(extend: false);
        Assert.Equal(7, c.CursorPos); // clamped to end of "xy"
    }

    [Fact]
    public void SelectAll_anchors_at_zero_and_caret_at_end()
    {
        var c = At("hello", 2);
        c.SelectAll();
        Assert.Equal((0, 5), c.GetSelection());
    }
}
