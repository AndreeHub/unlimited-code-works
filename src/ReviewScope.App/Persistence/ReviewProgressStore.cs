using ReviewScope.Domain;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReviewScope.App.Persistence;

/// <summary>
/// Workspace-scoped reading-progress store persisted to <c>.reviewscope/review-progress.json</c>.
/// Mirrors <see cref="TagIndexStore"/>: reads serve an in-memory snapshot, writes are debounced to
/// a background task so the marking gesture never blocks. Reviewed spans are kept as a normalized,
/// non-overlapping, sorted set of <see cref="ReviewedRange"/> per file; toggling unions or subtracts
/// an interval from that set. Files are keyed by path relative to the workspace root (lower-cased,
/// forward slashes) so progress travels with the repository.
/// </summary>
public sealed class ReviewProgressStore : IReviewProgressStore
{
    private readonly SessionRepository _repo;
    private readonly ConcurrentDictionary<string, WorkspaceState> _byWorkspace = new(StringComparer.OrdinalIgnoreCase);
    // Staleness cache: keyed by "workspaceKey|fullpath", invalidated when the file's mtime changes, so
    // the resolver (called per-frame per code block) only re-hashes a file when it actually changed.
    private readonly ConcurrentDictionary<string, (DateTime Mtime, bool Stale)> _staleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    public ReviewProgressStore(SessionRepository repo)
    {
        _repo = repo;
    }

    /// <summary>Stable hash of file text used to detect when a file changed after lines were marked.</summary>
    public static string ComputeContentHash(string fileText)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fileText ?? string.Empty));
        return Convert.ToHexString(bytes);
    }

    public ReviewProgressSnapshot GetSnapshot(string workspaceKey) =>
        _byWorkspace.TryGetValue(workspaceKey, out var state) ? state.Current : ReviewProgressSnapshot.Empty;

    public IReadOnlyList<ReviewedRange> GetRanges(string workspaceKey, string filePath)
    {
        if (!_byWorkspace.TryGetValue(workspaceKey, out var state)) return Array.Empty<ReviewedRange>();
        string key = ToRelativeKey(workspaceKey, filePath);
        return state.Current.Files.TryGetValue(key, out var fp) ? fp.Ranges : Array.Empty<ReviewedRange>();
    }

    public ReviewedFileState GetFileState(string workspaceKey, string filePath)
    {
        if (!_byWorkspace.TryGetValue(workspaceKey, out var state)) return ReviewedFileState.None;
        string key = ToRelativeKey(workspaceKey, filePath);
        if (!state.Current.Files.TryGetValue(key, out var fp) || fp.Ranges.Count == 0)
            return ReviewedFileState.None;
        return new ReviewedFileState(fp.Ranges, IsStale(workspaceKey, filePath, fp.ContentHash));
    }

    /// <summary>True when the file's current content differs from <paramref name="storedHash"/>. Re-hashes
    /// only when the file's last-write time has changed since the previous check.</summary>
    private bool IsStale(string workspaceKey, string filePath, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false; // no baseline recorded -> can't judge drift
        string full;
        DateTime mtime;
        try
        {
            full = Path.GetFullPath(filePath);
            if (!File.Exists(full)) return false;
            mtime = File.GetLastWriteTimeUtc(full);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }

        string cacheKey = StaleCacheKey(workspaceKey, full);
        if (_staleCache.TryGetValue(cacheKey, out var cached) && cached.Mtime == mtime)
            return cached.Stale;

        bool stale;
        try
        {
            stale = !string.Equals(ComputeContentHash(File.ReadAllText(full)), storedHash, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        _staleCache[cacheKey] = (mtime, stale);
        return stale;
    }

    private static string StaleCacheKey(string workspaceKey, string fullPath) =>
        $"{workspaceKey}|{fullPath.ToLowerInvariant()}";

    public async Task<ReviewProgressSnapshot> LoadAsync(string workspaceKey, CancellationToken cancellationToken)
    {
        string path = GetPath(workspaceKey);
        var files = new Dictionary<string, FileReviewProgress>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var file = await JsonSerializer.DeserializeAsync<ReviewProgressFile>(stream, _jsonOptions, cancellationToken);
                foreach (var entry in file?.Files ?? Array.Empty<FileEntry>())
                {
                    if (string.IsNullOrWhiteSpace(entry.RelativePath)) continue;
                    var ranges = NormalizeRanges(
                        (entry.Ranges ?? Array.Empty<RangeEntry>())
                            .Select(r => new ReviewedRange(r.Start, r.End)));
                    string key = NormalizeKey(entry.RelativePath);
                    files[key] = new FileReviewProgress(entry.RelativePath, entry.ContentHash ?? string.Empty, ranges, entry.UpdatedAt);
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var state = _byWorkspace.GetOrAdd(workspaceKey, _ => new WorkspaceState());
        state.Set(files);
        return state.Current;
    }

    public ReviewProgressSnapshot ToggleRange(string workspaceKey, string filePath, int startLine, int endLine, string contentHash)
    {
        if (endLine < startLine) (startLine, endLine) = (endLine, startLine);
        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);

        var state = _byWorkspace.GetOrAdd(workspaceKey, _ => new WorkspaceState());
        string key = ToRelativeKey(workspaceKey, filePath);
        string relativeForDisplay = ToRelativePath(workspaceKey, filePath);
        state.Toggle(key, relativeForDisplay, contentHash, startLine, endLine);
        // The toggle recorded the current file content, so any cached "stale" verdict is now void.
        try { _staleCache.TryRemove(StaleCacheKey(workspaceKey, Path.GetFullPath(filePath)), out _); }
        catch (ArgumentException) { }
        ScheduleSave(workspaceKey, state);
        return state.Current;
    }

    private void ScheduleSave(string workspaceKey, WorkspaceState state)
    {
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

    private async Task WriteAsync(string workspaceKey, ReviewProgressSnapshot snapshot, CancellationToken ct)
    {
        string path = GetPath(workspaceKey);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new ReviewProgressFile
        {
            SchemaVersion = 1,
            SavedAt = DateTimeOffset.UtcNow,
            Files = snapshot.Files.Values
                .Where(f => f.Ranges.Count > 0)
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileEntry
                {
                    RelativePath = f.RelativePath,
                    ContentHash = f.ContentHash,
                    UpdatedAt = f.UpdatedAt,
                    Ranges = f.Ranges.Select(r => new RangeEntry { Start = r.StartLine, End = r.EndLine }).ToArray()
                })
                .ToArray()
        };

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, file, _jsonOptions, ct);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath(string workspaceKey) =>
        Path.Combine(_repo.GetReviewScopeDir(workspaceKey), "review-progress.json");

    // --- Relative-path keying -------------------------------------------------

    /// <summary>Path relative to the workspace root, preserving display casing, forward-slashed.</summary>
    private string ToRelativePath(string workspaceKey, string absolutePath)
    {
        string root = _repo.GetWorkspaceRoot(workspaceKey);
        string full = Path.GetFullPath(absolutePath);
        string rel;
        try { rel = Path.GetRelativePath(root, full); }
        catch (ArgumentException) { rel = full; }
        // If the file is outside the workspace root, fall back to the absolute path so it still keys uniquely.
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            rel = full;
        return rel.Replace('\\', '/');
    }

    private string ToRelativeKey(string workspaceKey, string absolutePath) =>
        NormalizeKey(ToRelativePath(workspaceKey, absolutePath));

    private static string NormalizeKey(string relativePath) =>
        relativePath.Replace('\\', '/').ToLowerInvariant();

    // --- Interval-set helpers -------------------------------------------------

    /// <summary>Coalesce a set of spans into sorted, non-overlapping ranges (adjacent spans merge).</summary>
    internal static IReadOnlyList<ReviewedRange> NormalizeRanges(IEnumerable<ReviewedRange> ranges)
    {
        var ordered = ranges
            .Select(r => r.StartLine <= r.EndLine ? r : new ReviewedRange(r.EndLine, r.StartLine))
            .Where(r => r.EndLine >= 1)
            .Select(r => new ReviewedRange(Math.Max(1, r.StartLine), r.EndLine))
            .OrderBy(r => r.StartLine)
            .ToList();

        var result = new List<ReviewedRange>();
        foreach (var r in ordered)
        {
            if (result.Count > 0 && r.StartLine <= result[^1].EndLine + 1)
            {
                var last = result[^1];
                result[^1] = new ReviewedRange(last.StartLine, Math.Max(last.EndLine, r.EndLine));
            }
            else
            {
                result.Add(r);
            }
        }
        return result;
    }

    internal static bool IsFullyCovered(IReadOnlyList<ReviewedRange> ranges, int start, int end)
    {
        int cursor = start;
        foreach (var r in ranges.OrderBy(r => r.StartLine))
        {
            if (r.EndLine < cursor) continue;
            if (r.StartLine > cursor) return false; // gap before this range
            cursor = Math.Max(cursor, r.EndLine + 1);
            if (cursor > end) return true;
        }
        return cursor > end;
    }

    internal static IReadOnlyList<ReviewedRange> Subtract(IReadOnlyList<ReviewedRange> ranges, int start, int end)
    {
        var result = new List<ReviewedRange>();
        foreach (var r in ranges)
        {
            if (r.EndLine < start || r.StartLine > end) { result.Add(r); continue; }
            if (r.StartLine < start) result.Add(new ReviewedRange(r.StartLine, start - 1));
            if (r.EndLine > end) result.Add(new ReviewedRange(end + 1, r.EndLine));
        }
        return NormalizeRanges(result);
    }

    // --- Per-workspace mutable state -----------------------------------------

    private sealed class WorkspaceState
    {
        public ReviewProgressSnapshot Current { get; private set; } = ReviewProgressSnapshot.Empty;
        public CancellationTokenSource? PendingSave;

        private readonly Dictionary<string, FileReviewProgress> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public void Set(Dictionary<string, FileReviewProgress> files)
        {
            lock (_gate)
            {
                _files.Clear();
                foreach (var kv in files) _files[kv.Key] = kv.Value;
                Current = Build();
            }
        }

        public void Toggle(string key, string relativeForDisplay, string contentHash, int start, int end)
        {
            lock (_gate)
            {
                _files.TryGetValue(key, out var existing);
                var ranges = existing?.Ranges ?? Array.Empty<ReviewedRange>();

                IReadOnlyList<ReviewedRange> next = IsFullyCovered(ranges, start, end)
                    ? Subtract(ranges, start, end)
                    : NormalizeRanges(ranges.Append(new ReviewedRange(start, end)));

                if (next.Count == 0)
                    _files.Remove(key);
                else
                    _files[key] = new FileReviewProgress(
                        existing?.RelativePath ?? relativeForDisplay,
                        contentHash,
                        next,
                        DateTimeOffset.UtcNow);

                Current = Build();
            }
        }

        private ReviewProgressSnapshot Build() =>
            new(new Dictionary<string, FileReviewProgress>(_files, StringComparer.OrdinalIgnoreCase));
    }

    // --- Persisted shape ------------------------------------------------------

    private sealed class ReviewProgressFile
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public FileEntry[]? Files { get; set; }
    }

    private sealed class FileEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public RangeEntry[]? Ranges { get; set; }
    }

    private sealed class RangeEntry
    {
        public int Start { get; set; }
        public int End { get; set; }
    }
}
