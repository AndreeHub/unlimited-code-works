using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;

namespace ReviewScope.Analysis;

/*
 * File: WorkspaceManager.cs
 * Purpose: Manages the lifecycle of the active Roslyn workspace and session state.
 * Functions:
 * - LoadAsync: Resolves paths (including Git branches) and initializes a WorkspaceSession.
 * - GetBranchNamesAsync: Retrieves available Git branches for a given path.
 * - CurrentSession / CurrentSnapshot: Access to the active code analysis state.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

public sealed class WorkspaceManager : IWorkspaceLoader
{
    private readonly ILogger<WorkspaceManager> _logger;
    private readonly SemanticCache _cache;

    public WorkspaceSession? CurrentSession { get; private set; }
    public WorkspaceSnapshot? CurrentSnapshot => CurrentSession?.Snapshot;

    public WorkspaceManager(ILogger<WorkspaceManager> logger, SemanticCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public static async Task<IReadOnlyList<string>> GetBranchNamesAsync(string path, CancellationToken cancellationToken)
    {
        string probeDir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        try
        {
            string output = await GitBranchWorkspaceResolver.RunGitPublicAsync(
                probeDir, cancellationToken, "branch", "-a", "--format=%(refname:short)");
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.StartsWith("origin/HEAD", StringComparison.OrdinalIgnoreCase) ? null : b)
                .Where(b => b is not null)
                .Select(b => b!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<WorkspaceSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken,
        string? branchName = null)
    {
        string? normalizedBranch = NormalizeBranchName(branchName);
        string loadPath = normalizedBranch is null
            ? path
            : await GitBranchWorkspaceResolver.ResolveAsync(path, normalizedBranch, _logger, cancellationToken);

        var session = await RoslynWorkspaceLoader.LoadAsync(loadPath, _logger, cancellationToken, normalizedBranch);
        CurrentSession?.Dispose();
        CurrentSession = session;
        _cache.Clear();
        return session.Snapshot;
    }

    private static string? NormalizeBranchName(string? branchName)
    {
        string? trimmed = branchName?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
