namespace ReviewScope.Domain;

public interface IWorkspaceLoader
{
    Task<WorkspaceSnapshot> LoadAsync(string path, CancellationToken cancellationToken, string? branchName = null);
}

public interface IFileStructureService
{
    Task<FileStructureInfo?> GetFileStructureAsync(string filePath, CancellationToken cancellationToken);
}

public interface ISemanticSpanService
{
    Task<IReadOnlyList<SemanticTokenSpan>> GetTokenSpansAsync(string filePath, int startLine, int endLine, CancellationToken cancellationToken);
}

public interface ISymbolScopeService
{
    // Returns (startLine, endLine) of the symbol at the given position, 1-based
    Task<(int StartLine, int EndLine, string SymbolName, string ContainingType)?> GetSymbolScopeAsync(string filePath, int line, int column, CancellationToken cancellationToken);

    // Returns the source definition scope for a function-like symbol at the given position, 1-based.
    Task<(string FilePath, int StartLine, int EndLine, string SymbolName, string ContainingType)?> GetFunctionDefinitionScopeAsync(string filePath, int line, int column, CancellationToken cancellationToken);
}

public interface ISessionRepository
{
    Task<IReadOnlyList<ReviewSession>> GetSessionsAsync(string workspaceKey, CancellationToken cancellationToken);
    Task SaveSessionAsync(ReviewSession session, CancellationToken cancellationToken);
    Task DeleteSessionAsync(Guid sessionId, string workspaceKey, CancellationToken cancellationToken);
}

/// <summary>
/// Workspace-scoped store of reading progress (the set of reviewed line ranges per file),
/// persisted to <c>.reviewscope/review-progress.json</c>. Reads serve a cached in-memory
/// snapshot; mutations return the updated snapshot synchronously and persist in the background,
/// so the marking gesture never blocks. File paths passed in are absolute; the store keys them
/// by path relative to the workspace root so progress travels with the repository.
/// </summary>
public interface IReviewProgressStore
{
    /// <summary>Currently cached snapshot for <paramref name="workspaceKey"/>. Empty until <see cref="LoadAsync"/> has run.</summary>
    ReviewProgressSnapshot GetSnapshot(string workspaceKey);

    Task<ReviewProgressSnapshot> LoadAsync(string workspaceKey, CancellationToken cancellationToken);

    /// <summary>Reviewed ranges for the given absolute file path (empty if none / unknown file).</summary>
    IReadOnlyList<ReviewedRange> GetRanges(string workspaceKey, string filePath);

    /// <summary>
    /// Reviewed ranges plus a staleness flag for the given absolute file path. The file is considered
    /// stale when its current content hash differs from the hash recorded when the lines were marked
    /// (i.e. the file changed and the stored line numbers may have drifted). Cheap to call repeatedly:
    /// the hash is only recomputed when the file's last-write time changes.
    /// </summary>
    ReviewedFileState GetFileState(string workspaceKey, string filePath);

    /// <summary>
    /// Toggle reviewed state for the 1-based inclusive span <paramref name="startLine"/>..<paramref name="endLine"/>
    /// of <paramref name="filePath"/>. If the whole span is already reviewed it is cleared; otherwise it is
    /// added (unioned with any existing ranges). <paramref name="contentHash"/> is the hash of the current
    /// file text, recorded so staleness can be detected later. Returns the updated snapshot synchronously.
    /// </summary>
    ReviewProgressSnapshot ToggleRange(string workspaceKey, string filePath, int startLine, int endLine, string contentHash);
}

public sealed record TagIndexSnapshot(IReadOnlyList<string> Tags, IReadOnlyList<string> WikiLinks)
{
    public static TagIndexSnapshot Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Per-workspace index of every #tag and [[wiki link]] the user has ever typed.
/// Drives autocomplete in the outline editor and exposes a tag vocabulary for
/// annotating non-text objects.
/// </summary>
public interface ITagIndex
{
    /// <summary>Currently cached snapshot for <paramref name="workspaceKey"/>. Empty until <see cref="LoadAsync"/> has been called.</summary>
    TagIndexSnapshot GetSnapshot(string workspaceKey);

    Task<TagIndexSnapshot> LoadAsync(string workspaceKey, CancellationToken cancellationToken);

    /// <summary>
    /// Merge the supplied vocabulary into the workspace index. New values are
    /// persisted in the background; the call returns the updated snapshot
    /// synchronously so the UI can react without awaiting.
    /// </summary>
    TagIndexSnapshot Record(string workspaceKey, IEnumerable<string> tags, IEnumerable<string> wikiLinks);
}
