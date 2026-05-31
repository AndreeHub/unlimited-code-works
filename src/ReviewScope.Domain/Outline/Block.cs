namespace ReviewScope.Domain.Outline;

/// <summary>
/// A single bullet in an outline document — the equivalent of a Logseq block.
/// A document is an in-memory tree of these with an ordered <see cref="Children"/>
/// list (NOT Logseq's parent+left pointers). The saved markdown stays the source of
/// truth: parse builds the tree, edits mutate it, serialize writes it back.
/// </summary>
public sealed class Block
{
    /// <summary>
    /// The persistent <c>^xxxxxxxx</c> anchor id (Logseq <c>id::</c>). Allocated lazily
    /// the first time a bullet is referenced or collapsed; null until then.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The visible text of the bullet, WITHOUT the leading indent/bullet prefix and
    /// WITHOUT the trailing <c>^id</c> anchor suffix.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Ordered child bullets.</summary>
    public List<Block> Children { get; } = new();

    /// <summary>
    /// Whether this block is collapsed (its subtree hidden). Collapse is persisted
    /// outside the markdown body (in <c>BoardItemStyle.OutlineCollapsedItems</c>, keyed
    /// on <see cref="Id"/>), so it is populated on parse and ignored on serialize.
    /// </summary>
    public bool Collapsed { get; set; }

    /// <summary>Reserved for Phase 6 (code blocks, typed references, etc.). Null = plain bullet.</summary>
    public string? Type { get; set; }

    // --- byte-stable round-trip metadata --------------------------------------
    // Captured verbatim on parse so an unedited tree serializes back to identical
    // markdown even when the source uses tabs, odd indentation, or "*"/"•" bullets.
    // Left null on blocks created during editing, in which case Serialize synthesizes
    // a canonical 2-spaces-per-depth indent and a "- " bullet.

    /// <summary>Exact leading whitespace from the source line, or null for new blocks.</summary>
    internal string? RawIndent { get; set; }

    /// <summary>
    /// Exact bullet token from the source line: "- ", "* ", "• ", "" (a non-bullet
    /// line), or null for new blocks (serialize emits "- ").
    /// </summary>
    internal string? RawBullet { get; set; }

    public bool HasChildren => Children.Count > 0;
}
