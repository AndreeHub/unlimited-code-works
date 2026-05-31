using System.Text;

namespace ReviewScope.Domain.Outline;

/// <summary>
/// An in-memory tree of <see cref="Block"/>s parsed from an outline's markdown body.
/// One source line maps to exactly one block; the parent/child structure is derived
/// from indentation using the same level algorithm as the canvas renderer
/// (<c>OutlineDocument.Parse</c>), so the tree and the flat render layout always agree.
///
/// <para><see cref="Serialize"/> is the inverse of <see cref="Parse"/>: an unedited
/// tree round-trips back to byte-identical markdown (anchors and raw indentation
/// preserved). Collapse state lives outside the body and is therefore populated on
/// parse but never written on serialize.</para>
/// </summary>
public sealed class OutlineTree
{
    /// <summary>Top-level blocks, in document order.</summary>
    public List<Block> Roots { get; } = new();

    public static OutlineTree Parse(string? markdown, IReadOnlySet<string>? collapsedIds = null)
    {
        string text = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var parsed = new List<(Block Block, int Level)>();
        int start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            parsed.Add(ParseLine(text[start..end], collapsedIds));
            if (end == text.Length) break;
            start = end + 1;
        }

        var tree = new OutlineTree();
        var stack = new Stack<(Block Block, int Level)>();
        foreach (var item in parsed)
        {
            while (stack.Count > 0 && stack.Peek().Level >= item.Level)
                stack.Pop();
            if (stack.Count > 0)
                stack.Peek().Block.Children.Add(item.Block);
            else
                tree.Roots.Add(item.Block);
            stack.Push(item);
        }
        return tree;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var root in Roots)
            WriteBlock(sb, root, 0, ref first, null);
        return sb.ToString();
    }

    /// <summary>
    /// Serialize, also recording each block's content span (start/end offsets into the
    /// returned text). Used by <see cref="OutlineEdit"/> to map a caret offset to the
    /// block + within-content offset it falls in, and back again after a tree mutation.
    /// Shares the exact write path as <see cref="Serialize"/> so layout offsets and the
    /// canonical text never diverge.
    /// </summary>
    internal List<BlockSpan> SerializeWithLayout(out string text)
    {
        var sb = new StringBuilder();
        var layout = new List<BlockSpan>();
        bool first = true;
        foreach (var root in Roots)
            WriteBlock(sb, root, 0, ref first, layout);
        text = sb.ToString();
        return layout;
    }

    internal readonly record struct BlockSpan(Block Block, int ContentStart, int ContentEnd);

    /// <summary>
    /// Serialize a single block and its subtree to canonical markdown, re-based to depth 0
    /// (raw indentation ignored, 2 spaces per level, "- " bullets). Used to mirror a referenced
    /// bullet onto a canvas as a read-only transclusion, regardless of how deep it sat in its
    /// source document.
    /// </summary>
    public static string SerializeSubtree(Block root)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, root, 0);
        return sb.ToString();
    }

    private static void WriteCanonical(StringBuilder sb, Block block, int depth)
    {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(' ', depth * 2);
        sb.Append("- ");
        sb.Append(block.Content);
        if (block.Id is { Length: > 0 } id)
        {
            sb.Append(" ^");
            sb.Append(id);
        }
        foreach (var child in block.Children)
            WriteCanonical(sb, child, depth + 1);
    }

    private static void WriteBlock(StringBuilder sb, Block block, int depth, ref bool first, List<BlockSpan>? layout)
    {
        if (!first) sb.Append('\n');
        first = false;

        sb.Append(block.RawIndent ?? new string(' ', depth * 2));
        sb.Append(block.RawBullet ?? "- ");
        int contentStart = sb.Length;
        sb.Append(block.Content);
        layout?.Add(new BlockSpan(block, contentStart, sb.Length));
        if (block.Id is not null)
        {
            sb.Append(" ^");
            sb.Append(block.Id);
        }

        foreach (var child in block.Children)
            WriteBlock(sb, child, depth + 1, ref first, layout);
    }

    /// <summary>
    /// Parse one raw source line into a block plus its indentation level. Mirrors
    /// <c>OutlineDocument.ParseLine</c> exactly so structure stays consistent with the
    /// renderer: tabs count one level each, every two leading spaces count one level.
    /// </summary>
    private static (Block Block, int Level) ParseLine(string line, IReadOnlySet<string>? collapsedIds)
    {
        int indentChars = 0;
        int level = 0;
        foreach (char c in line)
        {
            if (c == '\t') { indentChars++; level++; }
            else if (c == ' ') indentChars++;
            else break;
        }
        level += line.TakeWhile(c => c == ' ').Count() / 2;

        string rawIndent = line[..indentChars];
        string rest = line[indentChars..];

        string rawBullet = string.Empty;
        if (rest.StartsWith("- ", StringComparison.Ordinal)
            || rest.StartsWith("* ", StringComparison.Ordinal)
            || rest.StartsWith("• ", StringComparison.Ordinal))
            rawBullet = rest[..2];

        string display = rest[rawBullet.Length..];
        string? anchorId = ParseAnchorId(display);
        if (anchorId is not null)
            display = display[..^10]; // " ^" + 8 hex

        var block = new Block
        {
            Id = anchorId,
            Content = display,
            Collapsed = anchorId is not null && collapsedIds is not null && collapsedIds.Contains(anchorId),
            RawIndent = rawIndent,
            RawBullet = rawBullet,
        };
        return (block, Math.Max(0, level));
    }

    /// <summary>
    /// Returns the 8-hex-char anchor id if <paramref name="display"/> ends with
    /// " ^xxxxxxxx", otherwise null. Mirrors <c>OutlineDocument.ParseBulletAnchorId</c>.
    /// </summary>
    private static string? ParseAnchorId(string display)
    {
        if (display.Length < 10) return null;
        int anchorStart = display.Length - 10;
        if (display[anchorStart] != ' ' || display[anchorStart + 1] != '^') return null;
        string hex = display[(anchorStart + 2)..];
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return null;
        return hex;
    }
}
