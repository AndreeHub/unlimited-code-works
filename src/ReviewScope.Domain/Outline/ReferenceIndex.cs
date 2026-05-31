namespace ReviewScope.Domain.Outline;

/// <summary>One document fed into <see cref="ReferenceIndex.Build"/>.</summary>
/// <param name="Id">The owning <c>ReviewSession.Id</c>.</param>
/// <param name="Name">The document name — the target of a <c>[[Name]]</c> link.</param>
/// <param name="Kind">Canvas / Page / Journal.</param>
/// <param name="Body">The outline markdown (null/empty for canvases, which contribute no bullets).</param>
public readonly record struct DocumentSource(Guid Id, string Name, DocumentKind Kind, string? Body);

/// <summary>
/// A derived, read-only index over every document in the project, rebuilt from their bodies.
/// It answers the two resolution questions Phase-5 cross-document references need:
/// <c>[[Name]]</c> → which document (navigation), and <c>((^anchor))</c> → which bullet
/// (transclusion). It holds no UI state and is cheap enough to rebuild whenever a body changes.
/// </summary>
public sealed class ReferenceIndex
{
    /// <summary>Identity of a document, without its body.</summary>
    public readonly record struct DocumentInfo(Guid Id, string Name, DocumentKind Kind);

    /// <summary>A resolved block ref: the bullet plus the document (and parsed tree) it lives in.</summary>
    public readonly record struct BlockLocation(DocumentInfo Document, OutlineTree Tree, Block Block);

    private readonly Dictionary<string, DocumentInfo> _pagesByName;
    private readonly Dictionary<string, BlockLocation> _blocksById;

    private ReferenceIndex(Dictionary<string, DocumentInfo> pagesByName, Dictionary<string, BlockLocation> blocksById)
    {
        _pagesByName = pagesByName;
        _blocksById = blocksById;
    }

    public static ReferenceIndex Empty { get; } = new(
        new Dictionary<string, DocumentInfo>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, BlockLocation>(StringComparer.Ordinal));

    public static ReferenceIndex Build(IEnumerable<DocumentSource> documents)
    {
        // Page names compare case-insensitively (Logseq treats [[Foo]] and [[foo]] alike);
        // anchor ids are exact 8-hex tokens, compared ordinally.
        var pagesByName = new Dictionary<string, DocumentInfo>(StringComparer.OrdinalIgnoreCase);
        var blocksById = new Dictionary<string, BlockLocation>(StringComparer.Ordinal);

        foreach (var doc in documents)
        {
            var info = new DocumentInfo(doc.Id, doc.Name, doc.Kind);

            // Pages and journals are link targets; canvases are not navigable by [[name]].
            if (doc.Kind != DocumentKind.Canvas && !pagesByName.ContainsKey(doc.Name))
                pagesByName[doc.Name] = info;

            if (string.IsNullOrEmpty(doc.Body)) continue;

            var tree = OutlineTree.Parse(doc.Body);
            foreach (var block in EnumerateBlocks(tree.Roots))
            {
                // First writer wins on duplicate anchor ids (ids are meant to be unique; a clash
                // means a copy/paste duplicated one — keep the earliest so resolution is stable).
                if (block.Id is { Length: > 0 } id && !blocksById.ContainsKey(id))
                    blocksById[id] = new BlockLocation(info, tree, block);
            }
        }

        return new ReferenceIndex(pagesByName, blocksById);
    }

    /// <summary>Resolve a <c>[[Name]]</c> page link to its document, if one exists.</summary>
    public bool TryResolvePage(string name, out DocumentInfo document) =>
        _pagesByName.TryGetValue(name?.Trim() ?? string.Empty, out document);

    /// <summary>Resolve a <c>((^anchor))</c> block ref to its bullet, if one exists.</summary>
    public bool TryResolveBlock(string anchorId, out BlockLocation location)
    {
        anchorId = (anchorId ?? string.Empty).TrimStart('^');
        return _blocksById.TryGetValue(anchorId, out location);
    }

    private static IEnumerable<Block> EnumerateBlocks(IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            foreach (var descendant in EnumerateBlocks(block.Children))
                yield return descendant;
        }
    }
}
