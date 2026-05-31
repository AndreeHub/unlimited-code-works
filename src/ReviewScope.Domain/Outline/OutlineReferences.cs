namespace ReviewScope.Domain.Outline;

/// <summary>The two kinds of cross-document reference an outline bullet can carry.</summary>
public enum OutlineReferenceKind
{
    /// <summary><c>[[Page Name]]</c> — a link to another document by name.</summary>
    Page,

    /// <summary><c>((^anchor))</c> — a link to a specific bullet by its anchor id.</summary>
    Block,
}

/// <summary>A single reference scanned out of a bullet's content.</summary>
/// <param name="Kind">Page link or block ref.</param>
/// <param name="Target">For <see cref="OutlineReferenceKind.Page"/>, the page name; for
/// <see cref="OutlineReferenceKind.Block"/>, the bare anchor id (leading <c>^</c> stripped),
/// so it compares directly against <see cref="Block.Id"/>.</param>
public readonly record struct OutlineReference(OutlineReferenceKind Kind, string Target);

/// <summary>
/// Scans bullet content for the two cross-document reference syntaxes — <c>[[Page]]</c> and
/// <c>((^anchor))</c>. This is the reference-extraction half of the inline grammar that the
/// canvas renderer (<c>OutlineDocument.ParseInlineSpans</c>) draws; it is duplicated here, in a
/// deliberately minimal form, because the renderer lives in the Canvas project which depends on
/// Domain (not the other way round). The matching rules are kept identical: open marker, then the
/// nearest closing marker, with a non-empty interior.
/// </summary>
public static class OutlineReferences
{
    public static IEnumerable<OutlineReference> Scan(string? content)
    {
        if (string.IsNullOrEmpty(content)) yield break;

        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];

            // [[Page Name]]
            if (c == '[' && i + 1 < content.Length && content[i + 1] == '[')
            {
                int end = content.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new OutlineReference(OutlineReferenceKind.Page, content[(i + 2)..end]);
                    i = end + 2;
                    continue;
                }
            }
            // ((^anchor))
            else if (c == '(' && i + 1 < content.Length && content[i + 1] == '(')
            {
                int end = content.IndexOf("))", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    string inner = content[(i + 2)..end];
                    if (inner.StartsWith('^')) inner = inner[1..];
                    yield return new OutlineReference(OutlineReferenceKind.Block, inner);
                    i = end + 2;
                    continue;
                }
            }
            i++;
        }
    }
}
