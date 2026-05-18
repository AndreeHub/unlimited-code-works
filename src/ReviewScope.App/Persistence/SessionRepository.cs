using ReviewScope.Domain;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReviewScope.App.Persistence;

public sealed class SessionRepository : ISessionRepository
{
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SessionRepository() : this(null) { }

    public SessionRepository(string? rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReviewScope", "sessions")
            : rootPath;
    }

    public async Task<IReadOnlyList<ReviewSession>> GetSessionsAsync(string workspaceKey, CancellationToken ct)
    {
        string dir = GetDir(workspaceKey);
        if (!Directory.Exists(dir)) return Array.Empty<ReviewSession>();
        var sessions = new List<ReviewSession>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var stream = File.OpenRead(file);
            var session = await JsonSerializer.DeserializeAsync<ReviewSession>(stream, _jsonOptions, ct);
            if (session is not null) sessions.Add(session);
        }
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task SaveSessionAsync(ReviewSession session, CancellationToken ct)
    {
        string dir = GetDir(session.WorkspaceKey);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{session.Id:N}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, session, _jsonOptions, ct);
    }

    public Task DeleteSessionAsync(Guid sessionId, string workspaceKey, CancellationToken ct)
    {
        string path = Path.Combine(GetDir(workspaceKey), $"{sessionId:N}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetDir(string workspaceKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceKey)));
        return Path.Combine(_rootPath, hash);
    }
}
