using ReviewScope.Domain;
using System.Windows;
using System.Windows.Media;

namespace ReviewScope.Canvas;

public sealed record SceneBlockVisual(RenderBlock Block, Rect Bounds);
public sealed record SceneConnectionVisual(RenderConnection Connection, Point Start, Point End, Rect Bounds);
public sealed record SceneSwimLaneVisual(RenderSwimLane Lane, Rect Bounds);

public sealed class SceneSnapshot
{
    private const double CellSize = 320;
    private readonly Dictionary<long, List<SceneBlockVisual>> _blockCells;
    private readonly Dictionary<long, List<SceneConnectionVisual>> _connectionCells;
    private readonly Dictionary<string, List<SceneConnectionVisual>> _connectionsByKey;

    public SceneSnapshot(
        IReadOnlyList<SceneBlockVisual> blocks,
        IReadOnlyList<SceneConnectionVisual> connections,
        IReadOnlyList<SceneSwimLaneVisual> swimLanes)
    {
        Blocks = blocks;
        Connections = connections;
        SwimLanes = swimLanes;
        WorldBounds = blocks.Count > 0 ? BuildWorldBounds(blocks) : Rect.Empty;
        _blockCells = BuildBlockCells(blocks);
        _connectionCells = BuildConnectionCells(connections);
        _connectionsByKey = BuildConnectionLookup(connections);
    }

    public static SceneSnapshot Empty { get; } = new(
        Array.Empty<SceneBlockVisual>(),
        Array.Empty<SceneConnectionVisual>(),
        Array.Empty<SceneSwimLaneVisual>());

    public IReadOnlyList<SceneBlockVisual> Blocks { get; }
    public IReadOnlyList<SceneConnectionVisual> Connections { get; }
    public IReadOnlyList<SceneSwimLaneVisual> SwimLanes { get; }
    public Rect WorldBounds { get; }

    public IReadOnlyList<SceneBlockVisual> QueryPoint(Point worldPoint)
    {
        long cellKey = BuildCellKey(GetCell(worldPoint.X), GetCell(worldPoint.Y));
        return _blockCells.TryGetValue(cellKey, out var matches) ? matches : Array.Empty<SceneBlockVisual>();
    }

    public IReadOnlyList<SceneBlockVisual> QueryBlocks(Rect worldRect)
    {
        if (worldRect.IsEmpty) return Array.Empty<SceneBlockVisual>();
        var matches = new Dictionary<string, SceneBlockVisual>(StringComparer.OrdinalIgnoreCase);
        foreach (long key in EnumerateCellKeys(worldRect))
        {
            if (!_blockCells.TryGetValue(key, out var bucket)) continue;
            foreach (var b in bucket)
                if (!matches.ContainsKey(b.Block.Key) && b.Bounds.IntersectsWith(worldRect))
                    matches[b.Block.Key] = b;
        }
        return matches.Values.ToList();
    }

    public IReadOnlyList<SceneConnectionVisual> QueryConnections(Rect worldRect, ISet<string> visibleKeys)
    {
        var matches = new Dictionary<Guid, SceneConnectionVisual>();
        foreach (string key in visibleKeys)
        {
            if (!_connectionsByKey.TryGetValue(key, out var connected)) continue;
            foreach (var c in connected) matches[c.Connection.Id] = c;
        }
        foreach (long cellKey in EnumerateCellKeys(worldRect))
        {
            if (!_connectionCells.TryGetValue(cellKey, out var bucket)) continue;
            foreach (var c in bucket)
                if (!matches.ContainsKey(c.Connection.Id) && c.Bounds.IntersectsWith(worldRect))
                    matches[c.Connection.Id] = c;
        }
        return matches.Values.ToList();
    }

    private static Dictionary<long, List<SceneBlockVisual>> BuildBlockCells(IReadOnlyList<SceneBlockVisual> blocks)
    {
        var cells = new Dictionary<long, List<SceneBlockVisual>>();
        foreach (var b in blocks) AddToCells(cells, b.Bounds, b);
        return cells;
    }

    private static Dictionary<long, List<SceneConnectionVisual>> BuildConnectionCells(IReadOnlyList<SceneConnectionVisual> connections)
    {
        var cells = new Dictionary<long, List<SceneConnectionVisual>>();
        foreach (var c in connections) AddToCells(cells, c.Bounds, c);
        return cells;
    }

    private static Dictionary<string, List<SceneConnectionVisual>> BuildConnectionLookup(IReadOnlyList<SceneConnectionVisual> connections)
    {
        var lookup = new Dictionary<string, List<SceneConnectionVisual>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            AddLookupEntry(lookup, c.Connection.SourceKey, c);
            AddLookupEntry(lookup, c.Connection.TargetKey, c);
        }
        return lookup;
    }

    private static void AddLookupEntry(Dictionary<string, List<SceneConnectionVisual>> lookup, string key, SceneConnectionVisual c)
    {
        if (!lookup.TryGetValue(key, out var bucket)) { bucket = new List<SceneConnectionVisual>(); lookup[key] = bucket; }
        bucket.Add(c);
    }

    private static void AddToCells<T>(Dictionary<long, List<T>> cells, Rect bounds, T item)
    {
        foreach (long key in EnumerateCellKeys(bounds))
        {
            if (!cells.TryGetValue(key, out var bucket)) { bucket = new List<T>(); cells[key] = bucket; }
            bucket.Add(item);
        }
    }

    private static IEnumerable<long> EnumerateCellKeys(Rect bounds)
    {
        int minX = GetCell(bounds.Left), maxX = GetCell(bounds.Right);
        int minY = GetCell(bounds.Top), maxY = GetCell(bounds.Bottom);
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                yield return BuildCellKey(x, y);
    }

    private static Rect BuildWorldBounds(IReadOnlyList<SceneBlockVisual> blocks)
    {
        Rect bounds = blocks[0].Bounds;
        for (int i = 1; i < blocks.Count; i++) bounds.Union(blocks[i].Bounds);
        bounds.Inflate(60, 60);
        return bounds;
    }

    private static int GetCell(double value) => (int)Math.Floor(value / CellSize);
    private static long BuildCellKey(int x, int y) => ((long)x << 32) ^ (uint)y;
}
