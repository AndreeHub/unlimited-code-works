using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ReviewScope.Analysis;

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
