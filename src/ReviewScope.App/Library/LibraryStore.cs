using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ReviewScope.Domain;

namespace ReviewScope.App.Library;

/// <summary>One shape inside a saved library item, stored relative to the item's own origin (0,0).
/// <see cref="AssetPath"/> is set only for image elements (the decoded bitmap on disk); such
/// entries place as frameless Image blocks rather than shapes.</summary>
public sealed record LibraryShape(
    string ShapeType,
    double X,
    double Y,
    double Width,
    double Height,
    string? Body,
    BoardItemStyle Style,
    string? AssetPath = null);

/// <summary>
/// A reusable library entry — a named group of shapes (e.g. one Excalidraw library item) the user
/// can drag onto any board repeatedly. Shapes are normalized so the item's top-left is at (0,0);
/// <see cref="Width"/>/<see cref="Height"/> are the item's bounds (for thumbnail aspect + placement).
/// </summary>
public sealed record LibraryItemModel(
    string Id,
    string Name,
    double Width,
    double Height,
    IReadOnlyList<LibraryShape> Shapes);

/// <summary>
/// Global, cross-project persistence for the shape Library. Backed by a single JSON file under
/// %LocalAppData%\ReviewScope\library.json. All methods are resilient: a missing or corrupt file
/// yields an empty library rather than throwing.
/// </summary>
public sealed class LibraryStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public LibraryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReviewScope", "library.json");
    }

    public IReadOnlyList<LibraryItemModel> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<LibraryItemModel>();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<LibraryItemModel>>(json, Options)
                   ?? new List<LibraryItemModel>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<LibraryItemModel>();
        }
    }

    public void Save(IEnumerable<LibraryItemModel> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(items, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed library save shouldn't crash the app.
        }
    }
}
