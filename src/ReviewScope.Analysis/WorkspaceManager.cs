using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

internal static class RoslynWorkspaceLoader
{
    private const string SharedProjectTypeGuid = "{D954291E-2A0B-460D-934E-DC6B0785DB48}";

    private static readonly Regex SlnProjectRegex = new(
        "^Project\\(\"(?<type>\\{[^}]+\\})\"\\) = \"(?<name>[^\"]+)\", \"(?<path>[^\"]+)\", \"(?<id>\\{[^}]+\\})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SharedProjectSourceItemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile",
        "Page",
        "ApplicationDefinition",
        "None",
        "Content",
        "AdditionalFiles"
    };

    public static async Task<WorkspaceSession> LoadAsync(
        string path,
        ILogger logger,
        CancellationToken cancellationToken,
        string? branchName = null)
    {
        string resolvedPath = Path.GetFullPath(path);

        if (File.Exists(resolvedPath))
        {
            string ext = Path.GetExtension(resolvedPath);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                return await OpenSolutionAsync(resolvedPath, logger, cancellationToken, branchName);
            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return await OpenProjectAsync(resolvedPath, logger, cancellationToken, branchName);
            if (ext.Equals(".shproj", StringComparison.OrdinalIgnoreCase))
                return OpenSharedProject(resolvedPath, logger, branchName);
        }

        return await OpenFolderAsync(resolvedPath, logger, cancellationToken, branchName);
    }

    private static async Task<WorkspaceSession> OpenSolutionAsync(
        string solutionPath,
        ILogger logger,
        CancellationToken cancellationToken,
        string? branchName)
    {
        EnsureMSBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild: {Message}", args.Diagnostic.Message);
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        var files = BuildSolutionFileSummaries(solution, solutionPath, logger);
        return new WorkspaceSession(
            solutionPath,
            new WorkspaceSnapshot(solutionPath, CreateDisplayName(solutionPath, branchName), files, branchName),
            workspace,
            solution,
            BuildDocumentMap(solution));
    }

    private static async Task<WorkspaceSession> OpenProjectAsync(
        string projectPath,
        ILogger logger,
        CancellationToken cancellationToken,
        string? branchName)
    {
        EnsureMSBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, args) => logger.LogWarning("MSBuild: {Message}", args.Diagnostic.Message);
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        var files = project.Documents.Select(d => d.FilePath)
            .Concat(project.AdditionalDocuments.Select(d => d.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p) && IsUserSourceFile(p!))
            .Select(p => CreateSummary(project.Name, projectPath, p!))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WorkspaceSession(
            projectPath,
            new WorkspaceSnapshot(projectPath, CreateDisplayName(projectPath, branchName), files, branchName),
            workspace,
            project.Solution,
            BuildDocumentMap(project.Solution));
    }

    private static WorkspaceSession OpenSharedProject(string sharedProjectPath, ILogger logger, string? branchName)
    {
        var files = ReadSharedProjectSummaries(
            Path.GetFileNameWithoutExtension(sharedProjectPath),
            sharedProjectPath,
            sharedProjectPath,
            logger);

        return new WorkspaceSession(
            sharedProjectPath,
            new WorkspaceSnapshot(sharedProjectPath, CreateDisplayName(sharedProjectPath, branchName), files, branchName),
            null,
            null,
            new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<WorkspaceSession> OpenFolderAsync(
        string folderPath,
        ILogger logger,
        CancellationToken cancellationToken,
        string? branchName)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsUserSourceFile)
            .Select(f => CreateSummary(Path.GetFileName(folderPath), folderPath, f))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshot = new WorkspaceSnapshot(folderPath, CreateDisplayName(folderPath, branchName), files, branchName);

        // Try to find a .sln or .csproj to back semantic features.
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

    private static List<WorkspaceFileSummary> BuildSolutionFileSummaries(
        Solution solution,
        string solutionPath,
        ILogger logger)
    {
        var regularFiles = BuildFileSummaries(solution, solutionPath);
        var sharedFiles = BuildSharedProjectSummaries(solutionPath, logger);
        return MergeFileSummaries(regularFiles, sharedFiles);
    }

    private static List<WorkspaceFileSummary> BuildFileSummaries(Solution solution, string rootPath)
    {
        return solution.Projects
            .SelectMany(p =>
            {
                var roslynFiles = p.Documents.Select(d => d.FilePath)
                    .Concat(p.AdditionalDocuments.Select(d => d.FilePath))
                    .Where(path => !string.IsNullOrWhiteSpace(path) && IsUserSourceFile(path!))
                    .Select(path => CreateSummary(p.Name, rootPath, path!))
                    .ToList();

                if (roslynFiles.Count > 0)
                    return roslynFiles;

                // Roslyn couldn't load this project's documents (e.g. platform-specific SDK missing).
                // Fall back to filesystem enumeration of the project directory.
                if (string.IsNullOrWhiteSpace(p.FilePath))
                    return Enumerable.Empty<WorkspaceFileSummary>();

                string projDir = Path.GetDirectoryName(p.FilePath!)!;
                if (!Directory.Exists(projDir))
                    return Enumerable.Empty<WorkspaceFileSummary>();

                return Directory.EnumerateFiles(projDir, "*.*", SearchOption.AllDirectories)
                    .Where(IsUserSourceFile)
                    .Select(f => CreateSummary(p.Name, rootPath, f));
            })
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WorkspaceFileSummary> BuildSharedProjectSummaries(string solutionPath, ILogger logger)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory;
        var files = new List<WorkspaceFileSummary>();

        foreach (var project in ParseSolutionProjects(solutionPath).Where(p => p.IsSharedProject))
        {
            string sharedProjectPath = Path.GetFullPath(Path.Combine(solutionDir, project.RelativePath));
            files.AddRange(ReadSharedProjectSummaries(project.Name, sharedProjectPath, solutionPath, logger));
        }

        return files
            .GroupBy(f => Path.GetFullPath(f.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WorkspaceFileSummary> MergeFileSummaries(
        IReadOnlyList<WorkspaceFileSummary> regularFiles,
        IReadOnlyList<WorkspaceFileSummary> sharedFiles)
    {
        var sharedPaths = new HashSet<string>(
            sharedFiles.Select(f => Path.GetFullPath(f.FilePath)),
            StringComparer.OrdinalIgnoreCase);

        return sharedFiles
            .Concat(regularFiles.Where(f => !sharedPaths.Contains(Path.GetFullPath(f.FilePath))))
            .OrderBy(f => f.ClusterKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<SolutionProjectEntry> ParseSolutionProjects(string solutionPath)
    {
        foreach (string line in File.ReadLines(solutionPath))
        {
            Match match = SlnProjectRegex.Match(line);
            if (!match.Success) continue;

            yield return new SolutionProjectEntry(
                match.Groups["type"].Value,
                match.Groups["name"].Value,
                match.Groups["path"].Value,
                match.Groups["id"].Value);
        }
    }

    private static IReadOnlyList<WorkspaceFileSummary> ReadSharedProjectSummaries(
        string projectName,
        string sharedProjectPath,
        string rootPath,
        ILogger logger)
    {
        if (!File.Exists(sharedProjectPath))
        {
            logger.LogWarning("Shared project not found: {SharedProjectPath}", sharedProjectPath);
            return Array.Empty<WorkspaceFileSummary>();
        }

        try
        {
            return ResolveProjItemsPaths(sharedProjectPath)
                .SelectMany(path => EnumerateProjItemsFiles(path, logger))
                .Where(IsUserSourceFile)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => CreateSummary(projectName, rootPath, path))
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read shared project {SharedProjectPath}.", sharedProjectPath);
            return Array.Empty<WorkspaceFileSummary>();
        }
    }

    private static IReadOnlyList<string> ResolveProjItemsPaths(string sharedProjectPath)
    {
        string baseDir = Path.GetDirectoryName(sharedProjectPath) ?? Environment.CurrentDirectory;
        var doc = XDocument.Load(sharedProjectPath);
        var paths = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("Project")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && path.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase))
            .Select(path => ResolveMSBuildPath(path!, baseDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            string fallback = Path.ChangeExtension(sharedProjectPath, ".projitems");
            if (File.Exists(fallback))
                paths.Add(fallback);
        }

        return paths;
    }

    private static IEnumerable<string> EnumerateProjItemsFiles(string projItemsPath, ILogger logger)
    {
        if (!File.Exists(projItemsPath))
        {
            logger.LogWarning("Shared project items file not found: {ProjItemsPath}", projItemsPath);
            yield break;
        }

        string baseDir = Path.GetDirectoryName(projItemsPath) ?? Environment.CurrentDirectory;
        XDocument doc = XDocument.Load(projItemsPath);

        foreach (var item in doc.Descendants().Where(e => SharedProjectSourceItemNames.Contains(e.Name.LocalName)))
        {
            string? include = item.Attribute("Include")?.Value;
            foreach (string filePath in ResolveIncludeFiles(include, baseDir))
                yield return filePath;
        }
    }

    private static IEnumerable<string> ResolveIncludeFiles(string? include, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(include))
            yield break;

        string resolved = ResolveMSBuildPath(include, baseDir);
        if (resolved.IndexOfAny(new[] { '*', '?' }) < 0)
        {
            yield return resolved;
            yield break;
        }

        foreach (string match in ExpandWildcard(resolved))
            yield return match;
    }

    private static IEnumerable<string> ExpandWildcard(string wildcardPath)
    {
        string normalized = wildcardPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int wildcardIndex = normalized.IndexOfAny(new[] { '*', '?' });
        if (wildcardIndex < 0)
        {
            yield return normalized;
            yield break;
        }

        int rootEnd = normalized.LastIndexOf(Path.DirectorySeparatorChar, wildcardIndex);
        if (rootEnd < 0)
            yield break;

        string root = normalized[..rootEnd];
        string pattern = normalized[(rootEnd + 1)..];
        bool recursive = pattern.Contains("**", StringComparison.Ordinal);
        pattern = pattern.Replace("**" + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal);

        if (!Directory.Exists(root))
            yield break;

        foreach (string filePath in Directory.EnumerateFiles(
            root,
            pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            yield return filePath;
        }
    }

    private static string ResolveMSBuildPath(string pathExpression, string baseDir)
    {
        string expanded = pathExpression
            .Replace("$(MSBuildThisFileDirectory)", EnsureTrailingSeparator(baseDir), StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", baseDir, StringComparison.OrdinalIgnoreCase);

        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(baseDir, expanded));
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static WorkspaceFileSummary CreateSummary(string projectName, string root, string filePath)
    {
        string rootDir = Directory.Exists(root) ? root : (Path.GetDirectoryName(root) ?? root);
        string rel = Path.GetRelativePath(rootDir, filePath).Replace('\\', '/');
        string clusterKey = projectName;
        return new WorkspaceFileSummary(projectName, filePath, rel, clusterKey);
    }

    private static string CreateDisplayName(string path, string? branchName)
    {
        string name = Directory.Exists(path)
            ? new DirectoryInfo(path).Name
            : Path.GetFileNameWithoutExtension(path);

        return string.IsNullOrWhiteSpace(branchName) ? name : $"{name} [{branchName}]";
    }

    private static bool IsUserSourceFile(string filePath)
    {
        string normalized = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string ext = Path.GetExtension(filePath);
        bool supported = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase);
        return supported
            && !normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
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

    private sealed record SolutionProjectEntry(string TypeGuid, string Name, string RelativePath, string ProjectGuid)
    {
        public bool IsSharedProject => TypeGuid.Equals(SharedProjectTypeGuid, StringComparison.OrdinalIgnoreCase)
            || RelativePath.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class GitBranchWorkspaceResolver
{
    public static async Task<string> ResolveAsync(
        string requestedPath,
        string branchName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string fullRequestedPath = Path.GetFullPath(requestedPath);
        string probeDirectory = Directory.Exists(fullRequestedPath)
            ? fullRequestedPath
            : (Path.GetDirectoryName(fullRequestedPath) ?? Environment.CurrentDirectory);

        string repoRoot = (await RunGitAsync(probeDirectory, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new InvalidOperationException($"No git repository found for {requestedPath}.");

        repoRoot = Path.GetFullPath(repoRoot);
        string relativePath = Path.GetRelativePath(repoRoot, fullRequestedPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"{requestedPath} is not inside repository {repoRoot}.");

        string branchRef = await ResolveBranchRefAsync(repoRoot, branchName, cancellationToken);
        string worktreeRoot = BuildWorktreePath(repoRoot, branchName);
        await EnsureWorktreeAsync(repoRoot, worktreeRoot, branchRef, branchName, cancellationToken);

        string branchPath = Path.GetFullPath(Path.Combine(worktreeRoot, relativePath));
        if (File.Exists(fullRequestedPath) && !File.Exists(branchPath))
            throw new FileNotFoundException($"Branch '{branchName}' does not contain {relativePath}.", branchPath);
        if (Directory.Exists(fullRequestedPath) && !Directory.Exists(branchPath))
            throw new DirectoryNotFoundException($"Branch '{branchName}' does not contain {relativePath}.");

        logger.LogInformation("Resolved {RequestedPath} on {BranchName} to {BranchPath}.", requestedPath, branchName, branchPath);
        return branchPath;
    }

    private static async Task<string> ResolveBranchRefAsync(
        string repoRoot,
        string branchName,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string> { branchName };
        if (!branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            && !branchName.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"origin/{branchName}");
        }

        foreach (string candidate in candidates)
        {
            try
            {
                await RunGitAsync(repoRoot, cancellationToken, "rev-parse", "--verify", $"{candidate}^{{commit}}");
                return candidate;
            }
            catch (InvalidOperationException)
            {
                // Try the next common branch/ref spelling.
            }
        }

        throw new InvalidOperationException($"Branch or ref not found: {branchName}. Fetch it first if it exists only on a remote.");
    }

    private static async Task EnsureWorktreeAsync(
        string repoRoot,
        string worktreeRoot,
        string branchRef,
        string branchName,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktreeRoot))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(worktreeRoot)!);
            await RunGitAsync(repoRoot, cancellationToken, "worktree", "add", "--detach", worktreeRoot, branchRef);
            return;
        }

        string gitMarker = Path.Combine(worktreeRoot, ".git");
        if (!File.Exists(gitMarker) && !Directory.Exists(gitMarker))
            throw new InvalidOperationException($"Branch workspace already exists but is not a git worktree: {worktreeRoot}");

        string status = await RunGitAsync(worktreeRoot, cancellationToken, "status", "--porcelain");
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException($"Branch workspace for '{branchName}' has local changes: {worktreeRoot}");

        await RunGitAsync(worktreeRoot, cancellationToken, "checkout", "--detach", branchRef);
    }

    private static string BuildWorktreePath(string repoRoot, string branchName)
    {
        string repoName = SanitizePathPart(new DirectoryInfo(repoRoot).Name);
        string repoHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot)))
            .ToLowerInvariant()[..12];
        string branchPart = SanitizePathPart(branchName);
        return Path.Combine(Path.GetTempPath(), "ReviewScope", "worktrees", $"{repoName}-{repoHash}", branchPart);
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        return builder.Length == 0 ? "branch" : builder.ToString();
    }

    public static Task<string> RunGitPublicAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments) => RunGitAsync(workingDirectory, cancellationToken, arguments);

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr.Trim()}");

        return stdout;
    }
}
