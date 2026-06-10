using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ReviewScope.App.Library;
using ReviewScope.Canvas;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels;

/// <summary>One entry shown in the right-side Library tab: a rendered thumbnail plus its saved model.</summary>
public sealed class LibraryItemViewModel
{
    public LibraryItemModel Model { get; }
    public ImageSource Thumbnail { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;

    public LibraryItemViewModel(LibraryItemModel model)
    {
        Model = model;
        Thumbnail = LibraryThumbnailRenderer.Render(model);
    }
}

public partial class MainWindowViewModel
{
    /// <summary>Index of the "Library" tab in the right inspector (after Inspector/Style/Text/Arrange).</summary>
    public const int LibraryTabIndex = 4;

    private readonly LibraryStore _libraryStore = new();
    private readonly List<LibraryItemModel> _libraryModels = new();

    /// <summary>Reusable shapes available in the Library panel; drag or click to drop onto the canvas.</summary>
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = new();

    private void LoadLibrary()
    {
        _libraryModels.Clear();
        _libraryModels.AddRange(_libraryStore.Load());
        LibraryItems.Clear();
        foreach (var m in _libraryModels)
            LibraryItems.Add(new LibraryItemViewModel(m));
    }

    /// <summary>Adds freshly-imported Excalidraw items to the Library panel and persists them.</summary>
    internal int AddImportedItemsToLibrary(IReadOnlyList<ExcalidrawLibraryImporter.ImportedItem> items)
    {
        int added = 0;
        foreach (var item in items)
        {
            var shapes = item.Blocks.Select(b => new LibraryShape(
                b.ShapeType ?? "rectangle", b.X, b.Y, b.Width, b.Height, b.Body,
                b.Style ?? new BoardItemStyle(),
                AssetPath: b.Source?.AssetPath)).ToList();
            if (shapes.Count == 0) continue;

            var model = new LibraryItemModel($"lib::{Guid.NewGuid():N}", item.Name, item.Width, item.Height, shapes);
            _libraryModels.Add(model);
            LibraryItems.Add(new LibraryItemViewModel(model));
            added++;
        }
        if (added > 0) _libraryStore.Save(_libraryModels);
        return added;
    }

    /// <summary>Saves the currently selected canvas shapes as a reusable library item (Excalidraw's
    /// "add to library"). Non-shape blocks (notes, code cards, ...) are skipped.</summary>
    [RelayCommand]
    private void AddSelectionToLibrary()
    {
        var shapes = Scene.Blocks
            .Where(b => b.IsSelected && b.Style is not null
                && (b.Kind == BlockKind.Shape
                    || (b.Kind == BlockKind.Image && !string.IsNullOrEmpty(b.Source?.AssetPath))))
            .ToList();
        if (shapes.Count == 0)
        {
            StatusMessage = "Select one or more shapes on the canvas first.";
            return;
        }

        double minX = shapes.Min(b => b.X);
        double minY = shapes.Min(b => b.Y);
        double maxX = shapes.Max(b => b.X + b.Width);
        double maxY = shapes.Max(b => b.Y + b.Height);

        var libShapes = shapes.Select(b => new LibraryShape(
            b.ShapeType ?? "rectangle", b.X - minX, b.Y - minY, b.Width, b.Height,
            StripAttachments(b.Body), b.Style!,
            AssetPath: b.Kind == BlockKind.Image ? b.Source?.AssetPath : null)).ToList();

        string name = $"Selection {LibraryItems.Count + 1}";
        var model = new LibraryItemModel($"lib::{Guid.NewGuid():N}", name,
            Math.Max(1, maxX - minX), Math.Max(1, maxY - minY), libShapes);
        _libraryModels.Add(model);
        LibraryItems.Add(new LibraryItemViewModel(model));
        _libraryStore.Save(_libraryModels);
        SelectedRightTabIndex = LibraryTabIndex;
        StatusMessage = $"Saved {shapes.Count} shape(s) to the Library as “{name}”.";
    }

    /// <summary>Block-attachment references don't survive outside the source board, so keep only
    /// the geometry ("points:...") section of a linear shape's body when saving to the library.</summary>
    private static string? StripAttachments(string? body)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains('|')) return body;
        var points = body.Split('|').FirstOrDefault(s => s.StartsWith("points:", StringComparison.OrdinalIgnoreCase));
        return points ?? body;
    }

    [RelayCommand]
    private void RemoveLibraryItem(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _libraryModels.RemoveAll(m => m.Id == id);
        var vm = LibraryItems.FirstOrDefault(i => i.Id == id);
        if (vm is not null) LibraryItems.Remove(vm);
        _libraryStore.Save(_libraryModels);
    }

    /// <summary>Drops a library item onto the canvas centered at (worldX, worldY).</summary>
    public async System.Threading.Tasks.Task PlaceLibraryItemAtAsync(string id, double worldX, double worldY)
    {
        var model = _libraryModels.FirstOrDefault(m => m.Id == id);
        if (model is null || model.Shapes.Count == 0) return;

        // Center the item on the drop point.
        double offX = worldX - model.Width / 2;
        double offY = worldY - model.Height / 2;
        // A multi-shape item drops as one logical group so it moves/selects as a unit.
        string? groupKey = model.Shapes.Count >= 2 ? $"group::{Guid.NewGuid():N}" : null;

        var newBlocks = model.Shapes.Select(s =>
        {
            var bid = Guid.NewGuid();
            string shapeType = s.ShapeType;
            // Image entries place as frameless Image blocks pointing at the decoded asset.
            if (!string.IsNullOrEmpty(s.AssetPath))
            {
                return new RenderBlock(
                    bid, $"image::{bid:N}", BlockKind.Image,
                    string.Empty, string.Empty,
                    s.X + offX, s.Y + offY, s.Width, s.Height,
                    ShapeType: s.ShapeType is "image" ? "image" : "image-raw", Style: s.Style,
                    Source: new BoardSourceBinding(AssetPath: s.AssetPath, SourceLanguage: "image"),
                    IsSelected: true, GroupKey: groupKey);
            }
            return new RenderBlock(
                bid, $"{shapeType}::{bid:N}", BlockKind.Shape,
                string.Empty, string.Empty,
                s.X + offX, s.Y + offY, s.Width, s.Height,
                Body: s.Body, ShapeType: shapeType, Style: s.Style,
                IsSelected: true, GroupKey: groupKey);
        }).ToList();

        var blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).Concat(newBlocks).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, $"Placed library item: {model.Name}");
        await PersistSessionAsync();
    }

    /// <summary>Click-to-place fallback: drops the item near the top-left of the board.</summary>
    public async System.Threading.Tasks.Task PlaceLibraryItemAsync(string id)
    {
        var model = _libraryModels.FirstOrDefault(m => m.Id == id);
        if (model is null) return;
        double cx = 200 + Scene.Blocks.Count * 18 + model.Width / 2;
        double cy = 160 + Scene.Blocks.Count * 14 + model.Height / 2;
        await PlaceLibraryItemAtAsync(id, cx, cy);
    }
}
