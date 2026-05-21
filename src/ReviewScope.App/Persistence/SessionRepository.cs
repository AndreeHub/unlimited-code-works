using ReviewScope.Domain;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReviewScope.App.Persistence;

public sealed class SessionRepository : ISessionRepository
{
    private readonly string _rootPath;
    private readonly string _legacyRootPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SessionRepository() : this(null) { }

    public SessionRepository(string? rootPath)
    {
        _legacyRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReviewScope", "sessions");
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? _legacyRootPath : rootPath;
    }

    public async Task<IReadOnlyList<ReviewSession>> GetSessionsAsync(string workspaceKey, CancellationToken ct)
    {
        string dir = GetDir(workspaceKey);
        var project = await EnsureProjectAsync(workspaceKey, ct);
        await MigrateLegacySessionsAsync(workspaceKey, dir, ct);
        if (!Directory.Exists(dir)) return Array.Empty<ReviewSession>();
        var sessions = new List<ReviewSession>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var session = await JsonSerializer.DeserializeAsync<ReviewSession>(stream, _jsonOptions, ct);
                if (session is not null) sessions.Add(session);
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (project.SessionOrder is { Count: > 0 } order)
        {
            var orderIndexes = order.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            return sessions
                .OrderBy(s => orderIndexes.TryGetValue(s.Id, out int index) ? index : int.MaxValue)
                .ThenByDescending(s => s.UpdatedAt)
                .ToList();
        }

        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task SaveSessionAsync(ReviewSession session, CancellationToken ct)
    {
        string dir = GetDir(session.WorkspaceKey);
        await EnsureProjectAsync(session.WorkspaceKey, ct);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{session.Id:N}.json");
        await WriteJsonAtomicAsync(path, session, ct);
    }

    public async Task SaveSessionOrderAsync(string workspaceKey, IReadOnlyList<Guid> sessionOrder, CancellationToken ct)
    {
        var project = await EnsureProjectAsync(workspaceKey, ct);
        string projectPath = GetProjectPath(workspaceKey);
        await WriteJsonAtomicAsync(projectPath, project with { SessionOrder = sessionOrder }, ct);
    }

    public async Task DeleteSessionAsync(Guid sessionId, string workspaceKey, CancellationToken ct)
    {
        string path = Path.Combine(GetDir(workspaceKey), $"{sessionId:N}.json");
        if (File.Exists(path)) File.Delete(path);
        var project = await EnsureProjectAsync(workspaceKey, ct);
        if (project.SessionOrder is null || !project.SessionOrder.Contains(sessionId))
            return;

        await SaveSessionOrderAsync(workspaceKey, project.SessionOrder.Where(id => id != sessionId).ToArray(), ct);
    }

    private string GetDir(string workspaceKey)
    {
        return Path.Combine(GetReviewScopeDir(workspaceKey), "sessions");
    }

    public string GetReviewScopeDir(string workspaceKey)
    {
        string root = ResolveWorkspaceRoot(workspaceKey);
        return Path.Combine(root, ".reviewscope");
    }

    public string GetAssetDir(string workspaceKey, string kind)
    {
        string safeKind = string.IsNullOrWhiteSpace(kind) ? "assets" : kind;
        return Path.Combine(GetReviewScopeDir(workspaceKey), "assets", safeKind);
    }

    public string GetExportDir(string workspaceKey)
    {
        return Path.Combine(GetReviewScopeDir(workspaceKey), "exports");
    }

    private string GetProjectPath(string workspaceKey)
    {
        return Path.Combine(GetReviewScopeDir(workspaceKey), "project.json");
    }

    private async Task<ReviewScopeProject> EnsureProjectAsync(string workspaceKey, CancellationToken ct)
    {
        string root = GetReviewScopeDir(workspaceKey);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "assets", "docs"));
        Directory.CreateDirectory(Path.Combine(root, "assets", "images"));
        Directory.CreateDirectory(Path.Combine(root, "exports"));

        string projectPath = GetProjectPath(workspaceKey);
        if (File.Exists(projectPath))
        {
            try
            {
                await using var read = File.OpenRead(projectPath);
                var existing = await JsonSerializer.DeserializeAsync<ReviewScopeProject>(read, _jsonOptions, ct);
                if (existing is not null)
                    return existing;
            }
            catch (JsonException)
            {
                QuarantineProjectFile(projectPath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Could not read ReviewScope project metadata: {projectPath}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Could not access ReviewScope project metadata: {projectPath}", ex);
            }
        }

        var project = new ReviewScopeProject(
            ResolveWorkspaceRoot(workspaceKey),
            Path.GetFileName(ResolveWorkspaceRoot(workspaceKey)),
            DateTimeOffset.UtcNow,
            null);
        await WriteJsonAtomicAsync(projectPath, project, ct);
        return project;
    }

    private async Task MigrateLegacySessionsAsync(string workspaceKey, string targetDir, CancellationToken ct)
    {
        string legacyDir = GetLegacyDir(workspaceKey);
        if (!Directory.Exists(legacyDir)) return;
        Directory.CreateDirectory(targetDir);
        foreach (string legacyFile in Directory.EnumerateFiles(legacyDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string targetFile = Path.Combine(targetDir, Path.GetFileName(legacyFile));
            if (File.Exists(targetFile)) continue;
            try
            {
                await using var read = File.OpenRead(legacyFile);
                var session = await JsonSerializer.DeserializeAsync<ReviewSession>(read, _jsonOptions, ct);
                if (session is null) continue;
                session = session with { WorkspaceKey = workspaceKey };
                await WriteJsonAtomicAsync(targetFile, session, ct);
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void QuarantineProjectFile(string projectPath)
    {
        if (!File.Exists(projectPath))
            return;

        string dir = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory;
        string name = Path.GetFileName(projectPath);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        string backupPath = Path.Combine(dir, $"{name}.corrupt-{stamp}-{Guid.NewGuid():N}");
        File.Move(projectPath, backupPath);
    }

    private string GetLegacyDir(string workspaceKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceKey)));
        return Path.Combine(_legacyRootPath, hash);
    }

    private static string ResolveWorkspaceRoot(string workspaceKey)
    {
        int sep = workspaceKey.IndexOf("::", StringComparison.Ordinal);
        string filePath = sep >= 0 ? workspaceKey[..sep] : workspaceKey;
        string path = Path.GetFullPath(filePath);
        if (Directory.Exists(path)) return path;
        return Path.GetDirectoryName(path) ?? path;
    }

    private sealed record ReviewScopeProject(
        string WorkspaceRoot,
        string Name,
        DateTimeOffset CreatedAt,
        IReadOnlyList<Guid>? SessionOrder = null);
}
