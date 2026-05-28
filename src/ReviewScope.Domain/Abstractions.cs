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
