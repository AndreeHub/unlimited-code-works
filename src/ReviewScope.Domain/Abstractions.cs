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
}

public interface ISessionRepository
{
    Task<IReadOnlyList<ReviewSession>> GetSessionsAsync(string workspaceKey, CancellationToken cancellationToken);
    Task SaveSessionAsync(ReviewSession session, CancellationToken cancellationToken);
    Task DeleteSessionAsync(Guid sessionId, string workspaceKey, CancellationToken cancellationToken);
}
