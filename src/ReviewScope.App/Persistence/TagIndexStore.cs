using ReviewScope.Domain;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace ReviewScope.App.Persistence;

/// <summary>
/// Workspace-scoped, lazily-loaded #tag / [[wiki link]] vocabulary persisted to
/// <c>.reviewscope/tag-index.json</c>. Reads cache an in-memory snapshot; writes
/// are debounced to a background task so callers in the edit hot path never block.
/// </summary>
public sealed class TagIndexStore : ITagIndex
{
    private readonly SessionRepository _repo;
    private readonly ConcurrentDictionary<string, WorkspaceState> _byWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    public TagIndexStore(SessionRepository repo)
    {
        _repo = repo;
    }

    public TagIndexSnapshot GetSnapshot(string workspaceKey) =>
        _byWorkspace.TryGetValue(workspaceKey, out var state) ? state.Current : TagIndexSnapshot.Empty;

    public async Task<TagIndexSnapshot> LoadAsync(string workspaceKey, CancellationToken cancellationToken)
    {
        string path = GetPath(workspaceKey);
        TagIndexSnapshot snapshot = TagIndexSnapshot.Empty;
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var file = await JsonSerializer.DeserializeAsync<TagIndexFile>(stream, _jsonOptions, cancellationToken);
                if (file is not null)
                    snapshot = new TagIndexSnapshot(file.Tags ?? Array.Empty<string>(), file.WikiLinks ?? Array.Empty<string>());
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var state = _byWorkspace.GetOrAdd(workspaceKey, _ => new WorkspaceState());
        state.Set(snapshot);
        return snapshot;
    }

    public TagIndexSnapshot Record(string workspaceKey, IEnumerable<string> tags, IEnumerable<string> wikiLinks)
    {
        var state = _byWorkspace.GetOrAdd(workspaceKey, _ => new WorkspaceState());
        bool changed = state.Merge(tags, wikiLinks, out var next);
        if (changed)
            ScheduleSave(workspaceKey, state);
        return next;
    }

    private void ScheduleSave(string workspaceKey, WorkspaceState state)
    {
        // Replace any in-flight debounced save. We don't await — the edit path stays free.
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref state.PendingSave, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay, cts.Token);
                await WriteAsync(workspaceKey, state.Current, cts.Token);
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    private async Task WriteAsync(string workspaceKey, TagIndexSnapshot snapshot, CancellationToken ct)
    {
        string path = GetPath(workspaceKey);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            var file = new TagIndexFile
            {
                SchemaVersion = 1,
                SavedAt = DateTimeOffset.UtcNow,
                Tags = snapshot.Tags.ToArray(),
                WikiLinks = snapshot.WikiLinks.ToArray()
            };
            await JsonSerializer.SerializeAsync(stream, file, _jsonOptions, ct);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath(string workspaceKey) =>
        Path.Combine(_repo.GetReviewScopeDir(workspaceKey), "tag-index.json");

    private sealed class WorkspaceState
    {
        public TagIndexSnapshot Current { get; private set; } = TagIndexSnapshot.Empty;
        public CancellationTokenSource? PendingSave;

        private readonly SortedSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _wikiLinks = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public void Set(TagIndexSnapshot snapshot)
        {
            lock (_gate)
            {
                _tags.Clear();
                _wikiLinks.Clear();
                foreach (var t in snapshot.Tags) _tags.Add(t);
                foreach (var w in snapshot.WikiLinks) _wikiLinks.Add(w);
                Current = Build();
            }
        }

        public bool Merge(IEnumerable<string> tags, IEnumerable<string> wikiLinks, out TagIndexSnapshot next)
        {
            lock (_gate)
            {
                bool added = false;
                foreach (var t in tags)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (_tags.Add(t.Trim())) added = true;
                }
                foreach (var w in wikiLinks)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    if (_wikiLinks.Add(w.Trim())) added = true;
                }
                if (added) Current = Build();
                next = Current;
                return added;
            }
        }

        private TagIndexSnapshot Build() =>
            new(_tags.ToArray(), _wikiLinks.ToArray());
    }

    private sealed class TagIndexFile
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public string[]? Tags { get; set; }
        public string[]? WikiLinks { get; set; }
    }
}
