namespace ReviewScope.Domain.Outline;

/// <summary>
/// Caret-driven structural edits on an <see cref="OutlineTree"/>. Each operation takes a
/// caret offset into the tree's serialized markdown (the same text the editor holds),
/// mutates the tree in place, and returns the caret offset into the re-serialized text.
///
/// <para>These encode the Logseq editing-fidelity rules (see the Phase-1 spec):
/// Enter at a block with expanded children inserts a first child; otherwise a sibling after
/// the whole subtree; Backspace at the start of a block merges it into the previous block;
/// Tab with no previous sibling is a no-op; Shift+Tab outdents and adopts following siblings;
/// Enter mid-text splits, keeping the text after the caret in the new block.</para>
///
/// <para>Operations that can decline (Backspace not at start, Tab/Shift+Tab/Move at a
/// boundary) return <see cref="EditResult.Handled"/> = false so the host can fall back to its
/// default (e.g. plain character delete).</para>
/// </summary>
public static class OutlineEdit
{
    public readonly record struct EditResult(bool Handled, int Caret);

    /// <summary>
    /// True when <paramref name="caret"/> sits at the start of some block's content.
    /// Lets the host tell a structural Backspace (start-of-block → merge/no-op) apart from
    /// an ordinary character delete (mid-content), without re-deriving offsets itself.
    /// </summary>
    public static bool IsAtBlockStart(OutlineTree tree, int caret)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return false;
        LocateIndex(layout, caret, out _, out int offset);
        return offset == 0;
    }

    /// <summary>Split at the caret per the expanded-children / sibling fidelity rules.</summary>
    public static int EnterKey(OutlineTree tree, int caret)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return caret;
        var (block, offset) = Locate(layout, caret);

        // Empty-bullet behavior (preserved from the canvas editor): Enter on an empty bullet
        // outdents it when indented, or strips it to a plain blank line at the top level.
        if (block.Content.Length == 0)
        {
            if (TryLocate(tree, block, out _, out _, out var parent) && parent is not null)
            {
                Outdent(tree, caret);
                return RawCaret(tree, block, 0);
            }

            block.RawBullet = "";
            block.RawIndent = "";
            return RawCaret(tree, block, 0);
        }

        string before = block.Content[..offset];
        string after = block.Content[offset..];
        block.Content = before;

        var inserted = new Block { Content = after };

        if (block.HasChildren && !block.Collapsed)
        {
            // Expanded parent: the new block is its first child; existing children follow.
            block.Children.Insert(0, inserted);
        }
        else if (TryLocate(tree, block, out var siblings, out int index, out _))
        {
            // Collapsed or leaf: a sibling immediately after this block (i.e. after its subtree).
            siblings.Insert(index + 1, inserted);
        }
        else
        {
            tree.Roots.Insert(0, inserted);
        }

        return RawCaret(tree, inserted, 0);
    }

    /// <summary>Backspace at the start of a block merges it into the previous block.</summary>
    public static EditResult Backspace(OutlineTree tree, int caret)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return new EditResult(false, caret);
        int idx = LocateIndex(layout, caret, out var block, out int offset);
        if (offset != 0) return new EditResult(false, caret);
        if (idx == 0) return new EditResult(false, caret); // first block: nothing precedes it

        Block prev = layout[idx - 1].Block;
        int joinAt = prev.Content.Length;
        prev.Content += block.Content;

        // The merged block's children rise to become the previous block's trailing children.
        foreach (var child in block.Children)
            prev.Children.Add(child);
        block.Children.Clear();

        if (TryLocate(tree, block, out var siblings, out int index, out _))
            siblings.RemoveAt(index);

        return new EditResult(true, RawCaret(tree, prev, joinAt));
    }

    /// <summary>Tab: demote the block under its previous sibling. No-op without one.</summary>
    public static EditResult Indent(OutlineTree tree, int caret)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return new EditResult(false, caret);
        var (block, offset) = Locate(layout, caret);

        if (!TryLocate(tree, block, out var siblings, out int index, out _) || index == 0)
            return new EditResult(false, caret);

        Block newParent = siblings[index - 1];
        siblings.RemoveAt(index);
        newParent.Children.Add(block);
        ClearRawIndent(block);

        return new EditResult(true, RawCaret(tree, block, offset));
    }

    /// <summary>Shift+Tab: promote the block one level, adopting its following siblings.</summary>
    public static EditResult Outdent(OutlineTree tree, int caret)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return new EditResult(false, caret);
        var (block, offset) = Locate(layout, caret);

        if (!TryLocate(tree, block, out var siblings, out int index, out var parent) || parent is null)
            return new EditResult(false, caret); // already top-level

        // Following siblings become this block's trailing children (Logseq follower adoption),
        // preserving their order.
        var followers = siblings.GetRange(index + 1, siblings.Count - index - 1);
        siblings.RemoveRange(index + 1, followers.Count);
        block.Children.AddRange(followers);

        siblings.RemoveAt(index);

        // Insert the promoted block as the next sibling of its old parent.
        TryLocate(tree, parent, out var grandSiblings, out int parentIndex, out _);
        grandSiblings.Insert(parentIndex + 1, block);
        ClearRawIndent(block);

        return new EditResult(true, RawCaret(tree, block, offset));
    }

    /// <summary>Alt+Up: swap the block with its previous sibling. No-op at the top.</summary>
    public static EditResult MoveUp(OutlineTree tree, int caret) => Move(tree, caret, -1);

    /// <summary>Alt+Down: swap the block with its next sibling. No-op at the bottom.</summary>
    public static EditResult MoveDown(OutlineTree tree, int caret) => Move(tree, caret, +1);

    private static EditResult Move(OutlineTree tree, int caret, int direction)
    {
        var layout = tree.SerializeWithLayout(out _);
        if (layout.Count == 0) return new EditResult(false, caret);
        var (block, offset) = Locate(layout, caret);

        if (!TryLocate(tree, block, out var siblings, out int index, out _))
            return new EditResult(false, caret);

        int target = index + direction;
        if (target < 0 || target >= siblings.Count) return new EditResult(false, caret);

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        return new EditResult(true, RawCaret(tree, block, offset));
    }

    // --- caret <-> tree mapping ----------------------------------------------------

    private static (Block Block, int Offset) Locate(IReadOnlyList<OutlineTree.BlockSpan> layout, int caret)
    {
        LocateIndex(layout, caret, out var block, out int offset);
        return (block, offset);
    }

    private static int LocateIndex(IReadOnlyList<OutlineTree.BlockSpan> layout, int caret, out Block block, out int offset)
    {
        for (int i = 0; i < layout.Count; i++)
        {
            var span = layout[i];
            if (caret <= span.ContentEnd)
            {
                block = span.Block;
                offset = Math.Clamp(caret - span.ContentStart, 0, block.Content.Length);
                return i;
            }
        }
        var last = layout[^1];
        block = last.Block;
        offset = block.Content.Length;
        return layout.Count - 1;
    }

    private static int RawCaret(OutlineTree tree, Block target, int offsetInContent)
    {
        var layout = tree.SerializeWithLayout(out _);
        foreach (var span in layout)
            if (ReferenceEquals(span.Block, target))
                return span.ContentStart + Math.Clamp(offsetInContent, 0, target.Content.Length);
        return 0;
    }

    // --- tree navigation -----------------------------------------------------------

    private static bool TryLocate(OutlineTree tree, Block target, out List<Block> siblings, out int index, out Block? parent)
        => TryLocate(tree.Roots, null, target, out siblings, out index, out parent);

    private static bool TryLocate(List<Block> level, Block? levelParent, Block target,
        out List<Block> siblings, out int index, out Block? parent)
    {
        int i = level.IndexOf(target);
        if (i >= 0)
        {
            siblings = level;
            index = i;
            parent = levelParent;
            return true;
        }
        foreach (var child in level)
            if (TryLocate(child.Children, child, target, out siblings, out index, out parent))
                return true;

        siblings = null!;
        index = -1;
        parent = null;
        return false;
    }

    private static void ClearRawIndent(Block block)
    {
        block.RawIndent = null;
        foreach (var child in block.Children)
            ClearRawIndent(child);
    }
}
