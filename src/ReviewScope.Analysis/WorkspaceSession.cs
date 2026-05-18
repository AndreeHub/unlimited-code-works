using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Workspace session (holds Roslyn MSBuild workspace + solution) ---

public sealed class WorkspaceSession : IDisposable
{
    private bool _disposed;

    public WorkspaceSession(
        string resolvedPath,
        WorkspaceSnapshot snapshot,
        MSBuildWorkspace? workspace,
        Solution? solution,
        IReadOnlyDictionary<string, DocumentId> documentIdsByFilePath)
    {
        ResolvedPath = resolvedPath;
        Snapshot = snapshot;
        Workspace = workspace;
        Solution = solution;
        DocumentIdsByFilePath = documentIdsByFilePath;
    }

    public string ResolvedPath { get; }
    public WorkspaceSnapshot Snapshot { get; }
    public MSBuildWorkspace? Workspace { get; }
    public Solution? Solution { get; private set; }
    public IReadOnlyDictionary<string, DocumentId> DocumentIdsByFilePath { get; }
    public bool SupportsSemanticNavigation => Solution is not null && DocumentIdsByFilePath.Count > 0;

    public bool TryGetDocumentId(string filePath, out DocumentId documentId) =>
        DocumentIdsByFilePath.TryGetValue(Path.GetFullPath(filePath), out documentId!);

    public void Dispose()
    {
        if (_disposed) return;
        Workspace?.Dispose();
        _disposed = true;
    }
}
