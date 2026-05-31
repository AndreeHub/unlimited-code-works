namespace ReviewScope.Domain.Outline;

/// <summary>
/// The shared, UI-free editing engine for an outline document. It owns the editable text
/// (<see cref="Body"/>) and caret state (<see cref="CursorPos"/>, <see cref="SelectionAnchor"/>)
/// and exposes the full set of edit operations — character entry, caret navigation/selection,
/// and the Logseq structural edits (Enter/Tab/Shift+Tab/Backspace-merge/move) built on
/// <see cref="OutlineEdit"/>.
///
/// <para>It contains no WPF, rendering, clipboard, or autocomplete concerns: those stay on the
/// host (the canvas block editor and the dedicated outline window), which drives this controller
/// from its key handlers and renders the result. Both hosts share this one engine so editing
/// fidelity stays identical everywhere.</para>
///
/// <para>Collapse state lives outside the body, so the host injects it via
/// <see cref="CollapsedProvider"/> — Enter consults it to decide whether a block with children
/// gets a new first child (expanded) or a following sibling (collapsed).</para>
/// </summary>
public sealed class OutlineEditController
{
    /// <summary>The full markdown body being edited (the canonical text form).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Caret offset into <see cref="Body"/>.</summary>
    public int CursorPos { get; set; }

    /// <summary>Selection start anchor, or -1 when there is no selection.</summary>
    public int SelectionAnchor { get; set; } = -1;

    /// <summary>Caret blink visibility, owned here so the host's blink timer can toggle it.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>
    /// Supplies the set of collapsed anchor ids for the document under edit (keyed on
    /// <see cref="Block.Id"/>). Injected by the host; null when nothing is collapsed.
    /// </summary>
    public Func<IReadOnlySet<string>?>? CollapsedProvider { get; set; }

    private IReadOnlySet<string>? Collapsed => CollapsedProvider?.Invoke();

    // --- selection / text primitives ----------------------------------------------

    /// <summary>The ordered (start, end) of the current selection, or null if none/empty.</summary>
    public (int Start, int End)? GetSelection()
    {
        if (SelectionAnchor < 0 || SelectionAnchor == CursorPos) return null;
        int a = SelectionAnchor, b = CursorPos;
        return a < b ? (a, b) : (b, a);
    }

    /// <summary>Delete the current selection if any; returns whether something was deleted.</summary>
    public bool DeleteSelection()
    {
        if (GetSelection() is not { } sel) return false;
        Body = Body.Remove(sel.Start, sel.End - sel.Start);
        CursorPos = sel.Start;
        SelectionAnchor = -1;
        return true;
    }

    /// <summary>Replace any selection with <paramref name="s"/> and advance the caret.</summary>
    public void InsertText(string s)
    {
        DeleteSelection();
        Body = Body.Insert(CursorPos, s);
        CursorPos += s.Length;
        SelectionAnchor = -1;
    }

    /// <summary>Backspace: delete selection, else merge at a block start, else delete one char.</summary>
    public bool Backspace()
    {
        if (DeleteSelection()) return true;

        var tree = OutlineTree.Parse(Body, Collapsed);
        if (OutlineEdit.IsAtBlockStart(tree, CursorPos))
        {
            var r = OutlineEdit.Backspace(tree, CursorPos);
            if (r.Handled) ApplyTree(tree, r.Caret);
            // At a block start we always consume: merged, or no previous block (no-op). Never
            // fall through to deleting the bullet prefix.
            return true;
        }

        if (CursorPos > 0)
        {
            Body = Body.Remove(CursorPos - 1, 1);
            CursorPos--;
        }
        SelectionAnchor = -1;
        return true;
    }

    /// <summary>Forward delete: delete selection, else delete the character after the caret.</summary>
    public void Delete()
    {
        if (DeleteSelection()) return;
        if (CursorPos < Body.Length) Body = Body.Remove(CursorPos, 1);
        SelectionAnchor = -1;
    }

    // --- structural edits (Logseq fidelity, via OutlineEdit) -----------------------

    /// <summary>Enter: split at the caret per the expanded-children / sibling rules.</summary>
    public void Enter()
    {
        DeleteSelection();
        var tree = OutlineTree.Parse(Body, Collapsed);
        ApplyTree(tree, OutlineEdit.EnterKey(tree, CursorPos));
    }

    /// <summary>Tab: demote the current block under its previous sibling (no-op without one).</summary>
    public void Indent()
    {
        DeleteSelection();
        var tree = OutlineTree.Parse(Body, Collapsed);
        var r = OutlineEdit.Indent(tree, CursorPos);
        if (r.Handled) ApplyTree(tree, r.Caret);
    }

    /// <summary>Shift+Tab: promote the current block, adopting its following siblings.</summary>
    public void Outdent()
    {
        DeleteSelection();
        var tree = OutlineTree.Parse(Body, Collapsed);
        var r = OutlineEdit.Outdent(tree, CursorPos);
        if (r.Handled) ApplyTree(tree, r.Caret);
    }

    /// <summary>Alt+Up: move the current block (and subtree) above its previous sibling.</summary>
    public void MoveUp()
    {
        var tree = OutlineTree.Parse(Body, Collapsed);
        var r = OutlineEdit.MoveUp(tree, CursorPos);
        if (r.Handled) ApplyTree(tree, r.Caret);
    }

    /// <summary>Alt+Down: move the current block (and subtree) below its next sibling.</summary>
    public void MoveDown()
    {
        var tree = OutlineTree.Parse(Body, Collapsed);
        var r = OutlineEdit.MoveDown(tree, CursorPos);
        if (r.Handled) ApplyTree(tree, r.Caret);
    }

    private void ApplyTree(OutlineTree tree, int caret)
    {
        Body = tree.Serialize();
        CursorPos = Math.Clamp(caret, 0, Body.Length);
        SelectionAnchor = -1;
    }

    // --- inline-markdown wrap -------------------------------------------------------

    /// <summary>
    /// Wrap the selection in <paramref name="open"/>/<paramref name="close"/> markers (toggles
    /// off if already wrapped); with no selection, insert the pair and park the caret between.
    /// </summary>
    public void WrapSelection(string open, string close)
    {
        if (GetSelection() is not { } sel)
        {
            Body = Body.Insert(CursorPos, open + close);
            CursorPos += open.Length;
            SelectionAnchor = -1;
            return;
        }

        int s = sel.Start, e = sel.End;
        if (s >= open.Length && e + close.Length <= Body.Length
            && Body.Substring(s - open.Length, open.Length) == open
            && Body.Substring(e, close.Length) == close)
        {
            Body = Body.Remove(e, close.Length).Remove(s - open.Length, open.Length);
            SelectionAnchor = s - open.Length;
            CursorPos = e - open.Length;
            return;
        }

        Body = Body.Insert(e, close).Insert(s, open);
        SelectionAnchor = s + open.Length;
        CursorPos = e + open.Length;
    }

    // --- caret navigation -----------------------------------------------------------

    public void SelectAll()
    {
        SelectionAnchor = 0;
        CursorPos = Body.Length;
    }

    public void MoveLeft(bool extend)
    {
        UpdateAnchor(extend);
        if (!extend && GetSelection() is { } sel) CursorPos = sel.Start;
        else if (CursorPos > 0) CursorPos--;
        if (!extend) SelectionAnchor = -1;
    }

    public void MoveRight(bool extend)
    {
        UpdateAnchor(extend);
        if (!extend && GetSelection() is { } sel) CursorPos = sel.End;
        else if (CursorPos < Body.Length) CursorPos++;
        if (!extend) SelectionAnchor = -1;
    }

    public void MoveLineUp(bool extend)
    {
        UpdateAnchor(extend);
        CursorPos = OffsetByLine(Body, CursorPos, -1);
        if (!extend) SelectionAnchor = -1;
    }

    public void MoveLineDown(bool extend)
    {
        UpdateAnchor(extend);
        CursorPos = OffsetByLine(Body, CursorPos, +1);
        if (!extend) SelectionAnchor = -1;
    }

    public void MoveHome(bool extend)
    {
        UpdateAnchor(extend);
        CursorPos = LineStart(Body, CursorPos);
        if (!extend) SelectionAnchor = -1;
    }

    public void MoveEnd(bool extend)
    {
        UpdateAnchor(extend);
        CursorPos = LineEnd(Body, CursorPos);
        if (!extend) SelectionAnchor = -1;
    }

    private void UpdateAnchor(bool extend)
    {
        if (extend && SelectionAnchor < 0) SelectionAnchor = CursorPos;
    }

    // --- line geometry (shared with the host's caret math) -------------------------

    public static int LineStart(string text, int pos)
    {
        int i = Math.Min(pos, text.Length) - 1;
        while (i >= 0 && text[i] != '\n') i--;
        return i + 1;
    }

    public static int LineEnd(string text, int pos)
    {
        int i = Math.Min(pos, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return i;
    }

    private static int OffsetByLine(string text, int pos, int dir)
    {
        int curStart = LineStart(text, pos);
        int col = pos - curStart;
        if (dir < 0)
        {
            if (curStart == 0) return pos;
            int prevEnd = curStart - 1;
            int prevStart = LineStart(text, prevEnd);
            return prevStart + Math.Min(col, prevEnd - prevStart);
        }
        int curEnd = LineEnd(text, pos);
        if (curEnd >= text.Length) return pos;
        int nextStart = curEnd + 1;
        int nextEnd = LineEnd(text, nextStart);
        return nextStart + Math.Min(col, nextEnd - nextStart);
    }
}
