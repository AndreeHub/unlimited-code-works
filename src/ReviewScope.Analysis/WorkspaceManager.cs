using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;


namespace ReviewScope.Analysis;

// --- Workspace loader / session manager ---

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

    public async Task<WorkspaceSnapshot> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var session = await RoslynWorkspaceLoader.LoadAsync(path, _logger, cancellationToken);
        CurrentSession?.Dispose();
        CurrentSession = session;
        _cache.Clear();
        return session.Snapshot;
    }
}

internal static class RoslynWorkspaceLoader
{
    public static async Task<WorkspaceSession> LoadAsync(string path, ILogger logger, CancellationToken cancellationToken)
    {
        string resolvedPath = Path.GetFullPath(path);

        if (File.Exists(resolvedPath))
        {
            string ext = Path.GetExtension(resolvedPath);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                return await OpenSolutionAsync(resolvedPath, logger, cancellationToken);
            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return await OpenProjectAsync(resolvedPath, logger, cancellationToken);
        }

        return await OpenFolderAsync(resolvedPath, logger, cancellationToken);
    }

    private static async Task<WorkspaceSession> OpenSolutionAsync(string solutionPath, ILogger logger, CancellationToken cancellationToken)
    {
        EnsureMSBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild: {Message}", args.Diagnostic.Message);
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        var files = BuildFileSummaries(solution, solutionPath);
        return new WorkspaceSession(solutionPath,
            new WorkspaceSnapshot(solutionPath, Path.GetFileNameWithoutExtension(solutionPath), files),
            workspace, solution, BuildDocumentMap(solution));
    }

    private static async Task<WorkspaceSession> OpenProjectAsync(string projectPath, ILogger logger, CancellationToken cancellationToken)
    {
        EnsureMSBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild: {Message}", args.Diagnostic.Message);
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        var files = project.Documents
            .Where(d => !string.IsNullOrWhiteSpace(d.FilePath) && IsUserSourceFile(d.FilePath!))
            .Select(d => CreateSummary(project.Name, projectPath, d.FilePath!))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WorkspaceSession(projectPath,
            new WorkspaceSnapshot(projectPath, Path.GetFileNameWithoutExtension(projectPath), files),
            workspace, project.Solution, BuildDocumentMap(project.Solution));
    }

    private static async Task<WorkspaceSession> OpenFolderAsync(string folderPath, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var files = Directory.EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories)
            .Where(IsUserSourceFile)
            .Select(f => CreateSummary(Path.GetFileName(folderPath), folderPath, f))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshot = new WorkspaceSnapshot(folderPath, Path.GetFileName(folderPath), files);

        // Try to find a .sln or .csproj to back semantic features
        var slnPath = Directory.EnumerateFiles(folderPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var csprojPath = slnPath is null
            ? Directory.EnumerateFiles(folderPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        var semanticTarget = slnPath ?? csprojPath;

        if (semanticTarget is null)
        {
            logger.LogInformation("No .sln or .csproj found; semantic features unavailable.");
            return new WorkspaceSession(folderPath, snapshot, null, null,
                new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            EnsureMSBuildRegistered();
            var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild: {Message}", args.Diagnostic.Message);
            Solution solution;
            if (semanticTarget.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                solution = await workspace.OpenSolutionAsync(semanticTarget, cancellationToken: cancellationToken);
            else
            {
                var proj = await workspace.OpenProjectAsync(semanticTarget, cancellationToken: cancellationToken);
                solution = proj.Solution;
            }
            return new WorkspaceSession(folderPath, snapshot, workspace, solution, BuildDocumentMap(solution));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load semantic workspace for {FolderPath}.", folderPath);
            return new WorkspaceSession(folderPath, snapshot, null, null,
                new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static List<WorkspaceFileSummary> BuildFileSummaries(Solution solution, string rootPath)
    {
        return solution.Projects
            .SelectMany(p => p.Documents
                .Where(d => !string.IsNullOrWhiteSpace(d.FilePath) && IsUserSourceFile(d.FilePath!))
                .Select(d => CreateSummary(p.Name, rootPath, d.FilePath!)))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorkspaceFileSummary CreateSummary(string projectName, string root, string filePath)
    {
        string rootDir = Directory.Exists(root) ? root : (Path.GetDirectoryName(root) ?? root);
        string rel = Path.GetRelativePath(rootDir, filePath).Replace('\\', '/');
        string clusterKey = projectName;
        return new WorkspaceFileSummary(projectName, filePath, rel, clusterKey);
    }

    private static bool IsUserSourceFile(string filePath)
    {
        string normalized = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return !normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(filePath).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(filePath).EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(filePath).EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(filePath).EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, DocumentId> BuildDocumentMap(Solution solution)
    {
        return solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrWhiteSpace(d.FilePath))
            .GroupBy(d => Path.GetFullPath(d.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureMSBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }
}
