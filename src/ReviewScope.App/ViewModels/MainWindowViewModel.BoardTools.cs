using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ReviewScope.Domain;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly BoardItemStyle[] ReviewStencilStyles =
    {
        new("#EFF6FF", "#2E7DD7", "#172033"),
        new("#ECFDF5", "#23A26D", "#172033"),
        new("#FFF7ED", "#D97706", "#172033"),
        new("#FEF2F2", "#DC2626", "#172033"),
        new("#F5F3FF", "#7C3AED", "#172033")
    };

    [RelayCommand]
    public async Task AddNoteCardAsync()
    {
        var id = Guid.NewGuid();
        var note = new RenderBlock(
            id, $"note::{id:N}", BlockKind.Note,
            "New note", string.Empty,
            160 + Scene.Blocks.Count * 24, 140 + Scene.Blocks.Count * 18, 280, 130,
            Body: "New note...",
            ZIndex: NextBlockZIndex(),
            LayerKey: "layer::notes",
            Style: new BoardItemStyle("#FFF3C7", "#E2BA4C", "#3C3412"));
        var annotations = Scene.Annotations.Append(new RenderAnnotation(id, note.Key, note.Body!, note.X, note.Y)).ToList();
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(note).ToList(), Annotations = annotations }, "Added note");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task AddTextCardAsync()
    {
        var id = Guid.NewGuid();
        var text = new RenderBlock(
            id, $"text::{id:N}", BlockKind.Text,
            "Text", string.Empty,
            180 + Scene.Blocks.Count * 22, 120 + Scene.Blocks.Count * 18, 300, 110,
            Body: "Double-click note-style text editing is coming next; edit from persistence for now.",
            LayerKey: "layer::architecture",
            Style: new BoardItemStyle("#FFFFFF", "#CBD5E1", "#111827"));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(text).ToList() }, "Added text");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task AddContainerCardAsync()
    {
        var id = Guid.NewGuid();
        var container = new RenderBlock(
            id, $"container::{id:N}", BlockKind.Container,
            $"Architecture area {Scene.Blocks.Count(b => b.Kind == BlockKind.Container) + 1}", string.Empty,
            120 + Scene.Blocks.Count * 20, 100 + Scene.Blocks.Count * 16, 640, 420,
            LayerKey: "layer::architecture",
            ShapeType: "container",
            Style: new BoardItemStyle("#F8FAFC", "#64748B", "#334155", 1.4, Dashed: true, Opacity: 0.85));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(container).ToList() }, "Added container");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task AddShapeAsync(string? shapeType)
    {
        shapeType = string.IsNullOrWhiteSpace(shapeType) ? "service" : shapeType.Trim().ToLowerInvariant();
        var id = Guid.NewGuid();
        var size = MeasureShapeBlockSize(shapeType);
        var placement = FindOpenCanvasPlacement(size.Width, size.Height);
        var title = ToTitle(shapeType);
        var shape = new RenderBlock(
            id, $"shape::{id:N}", BlockKind.Shape,
            title, string.Empty,
            placement.X, placement.Y, size.Width, size.Height,
            Body: title,
            ZIndex: NextBlockZIndex(),
            LayerKey: shapeType is "risk" or "todo" or "bug" or "test" ? "layer::risks" : "layer::architecture",
            ShapeType: shapeType,
            Style: ResolveShapeStyle(shapeType));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(shape).ToList() }, $"Added {shape.Title}");
        await PersistSessionAsync();
    }

    private static Size MeasureShapeBlockSize(string shapeType) => shapeType switch
    {
        "square" or "circle" or "star" or "hexagon" => new Size(150, 150),
        "triangle" or "diamond" or "decision" => new Size(170, 145),
        "line" or "arrow" or "polyline" => new Size(220, 80),
        "oval" => new Size(220, 130),
        "rectangle" => new Size(220, 130),
        _ => new Size(220, 120)
    };

    private static BoardItemStyle ResolveShapeStyle(string shapeType)
    {
        if (shapeType is "line" or "arrow" or "polyline")
            return new BoardItemStyle("#00FFFFFF", "#2E7DD7", "#172033", 2.2, CornerRadius: 0);

        if (shapeType is "square" or "rectangle" or "circle" or "oval" or "triangle" or "star" or "diamond" or "hexagon")
            return new BoardItemStyle("#FFFFFF", "#2E7DD7", "#172033", 1.4, CornerRadius: 3);

        return ReviewStencilStyles[Math.Abs(shapeType.GetHashCode()) % ReviewStencilStyles.Length];
    }

    [RelayCommand]
    public async Task ImportMarkdownDocAsync()
    {
        if (_currentSnapshot is null) return;
        var dlg = new OpenFileDialog { Title = "Import Architecture Markdown", Filter = "Markdown|*.md|Text|*.txt|All Files|*.*" };
        if (dlg.ShowDialog() != true) return;
        string body = await File.ReadAllTextAsync(dlg.FileName);
        string assetPath = CopyToAssetFolder(dlg.FileName, "docs");
        var id = Guid.NewGuid();
        var doc = new RenderBlock(
            id, $"doc::{id:N}", BlockKind.MarkdownDoc,
            Path.GetFileName(dlg.FileName), "Architecture document",
            120 + Scene.Blocks.Count * 20, 110 + Scene.Blocks.Count * 16, 620, 520,
            Body: body,
            LayerKey: "layer::architecture",
            ShapeType: "markdown",
            Style: new BoardItemStyle("#FFFFFF", "#E2E8F0", "#111827"),
            Source: new BoardSourceBinding(assetPath, dlg.FileName, SourceLanguage: "markdown"));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(doc).ToList() }, "Imported architecture doc");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task PasteMarkdownDocAsync()
    {
        await PasteMarkdownDocAtAsync(null, null);
    }

    public async Task PasteMarkdownDocAtAsync(double? x, double? y)
    {
        if (_currentSnapshot is null || !Clipboard.ContainsText()) return;
        string body = Clipboard.GetText();
        string assetDir = _sessions.GetAssetDir(_currentSnapshot.WorkspaceKey, "docs");
        Directory.CreateDirectory(assetDir);
        string assetPath = Path.Combine(assetDir, $"pasted-architecture-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(assetPath, body);
        var id = Guid.NewGuid();
        var doc = new RenderBlock(
            id, $"doc::{id:N}", BlockKind.MarkdownDoc,
            "Pasted architecture", "Markdown document",
            x ?? 120 + Scene.Blocks.Count * 20, y ?? 110 + Scene.Blocks.Count * 16, 620, 520,
            Body: body,
            LayerKey: "layer::architecture",
            ShapeType: "markdown",
            Style: new BoardItemStyle("#FFFFFF", "#E2E8F0", "#111827"),
            Source: new BoardSourceBinding(assetPath, null, SourceLanguage: "markdown"));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(doc).ToList() }, "Pasted architecture doc");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task ImportImageCardAsync()
    {
        if (_currentSnapshot is null) return;
        var dlg = new OpenFileDialog { Title = "Import Image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*" };
        if (dlg.ShowDialog() != true) return;
        await AddImageFileToCanvasAsync(dlg.FileName);
    }

    public async Task AddImageFileToCanvasAsync(string filePath, double? x = null, double? y = null)
    {
        if (_currentSnapshot is null || !File.Exists(filePath)) return;
        string assetPath = CopyToAssetFolder(filePath, "images");
        var imageSize = TryReadImagePixelSize(assetPath);
        await AddImageBlockAsync(assetPath, Path.GetFileName(filePath), filePath, "Imported image",
            x,
            y,
            pixelWidth: imageSize is null ? null : (int)Math.Round(imageSize.Value.Width),
            pixelHeight: imageSize is null ? null : (int)Math.Round(imageSize.Value.Height));
    }

    [RelayCommand]
    public async Task PasteImageFromClipboardAsync()
    {
        await PasteImageFromClipboardAtAsync(null, null);
    }

    public async Task PasteImageFromClipboardAtAsync(double? x, double? y)
    {
        if (_currentSnapshot is null || !Clipboard.ContainsImage()) return;
        string assetDir = _sessions.GetAssetDir(_currentSnapshot.WorkspaceKey, "images");
        Directory.CreateDirectory(assetDir);
        string assetPath = Path.Combine(assetDir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        BitmapSource source = ForceOpaqueBgra(Clipboard.GetImage());
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        await using (var stream = File.Create(assetPath))
            encoder.Save(stream);

        await AddImageBlockAsync(assetPath, Path.GetFileName(assetPath), null, "Pasted screenshot", x, y, source.PixelWidth, source.PixelHeight);
    }

    private async Task AddImageBlockAsync(
        string assetPath,
        string title,
        string? sourcePath,
        string status,
        double? x = null,
        double? y = null,
        int? pixelWidth = null,
        int? pixelHeight = null)
    {
        var id = Guid.NewGuid();
        Size blockSize = MeasureImageBlockSize(pixelWidth, pixelHeight);
        var placement = FindOpenCanvasPlacement(blockSize.Width, blockSize.Height, x, y);
        var image = new RenderBlock(
            id, $"image::{id:N}", BlockKind.Image,
            title, "Image / screenshot",
            placement.X, placement.Y, blockSize.Width, blockSize.Height,
            Body: title,
            LayerKey: "layer::screenshots",
            ShapeType: "image",
            Style: new BoardItemStyle("#FFFFFF", "#CBD5E1", "#111827"),
            Source: new BoardSourceBinding(assetPath, sourcePath, SourceLanguage: "image"));
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Append(image).ToList() }, status);
        await PersistSessionAsync();
    }

    private static Size MeasureImageBlockSize(int? pixelWidth, int? pixelHeight)
    {
        const double chromeW = 24;
        const double chromeH = 56;
        const double maxImageW = 760;
        const double maxImageH = 520;
        const double minImageW = 160;
        const double minImageH = 100;

        if (pixelWidth is not > 0 || pixelHeight is not > 0)
            return new Size(420, 260);

        double scale = Math.Min(maxImageW / pixelWidth.Value, maxImageH / pixelHeight.Value);
        scale = Math.Min(1.0, scale);
        double imageW = Math.Max(minImageW, pixelWidth.Value * scale);
        double imageH = Math.Max(minImageH, pixelHeight.Value * scale);
        return new Size(Math.Round(imageW + chromeW), Math.Round(imageH + chromeH));
    }

    private static Size? TryReadImagePixelSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return new Size(frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ForceOpaqueBgra(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var fixedSource = BitmapSource.Create(
            width,
            height,
            bgra.DpiX,
            bgra.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        if (fixedSource.CanFreeze) fixedSource.Freeze();
        return fixedSource;
    }

    [RelayCommand]
    public void RefreshBoardSearch()
    {
        BoardSearchResults.Clear();
        string q = BoardSearchQuery?.Trim() ?? string.Empty;
        if (q.Length == 0) return;
        foreach (var block in Scene.Blocks.Where(b => Matches(b, q)).Take(200))
            BoardSearchResults.Add(new BoardSearchResultViewModel(block.Title, $"{block.Kind}  {block.Subtitle}", block.Key, ResolveBoardIconKind(block)));
        foreach (var connection in Scene.Connections.Where(c => !string.IsNullOrWhiteSpace(c.Label) && c.Label.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(50))
            BoardSearchResults.Add(new BoardSearchResultViewModel(connection.Label!, "Connector label", connection.Id.ToString("N"), "connector"));
    }

    public void RefreshBoardDetails()
    {
        BoardFileUsages.Clear();
        foreach (var group in Scene.Blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.FilePath))
            .GroupBy(b => b.FilePath!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => GetRelativePath(g.Key), StringComparer.OrdinalIgnoreCase))
        {
            string detail = string.Join(", ", group.Select(b => b.Focused?.SymbolName ?? b.Title).Distinct().Take(8));
            BoardFileUsages.Add(new BoardFileUsageViewModel(GetRelativePath(group.Key), detail));
        }
    }

    [RelayCommand]
    public void GenerateLlmExportPreview()
    {
        LlmExportPreview = BuildLlmExport();
        StatusMessage = "Generated LLM review package.";
    }

    [RelayCommand]
    public void CopyLlmExport()
    {
        if (string.IsNullOrWhiteSpace(LlmExportPreview)) LlmExportPreview = BuildLlmExport();
        Clipboard.SetText(LlmExportPreview);
        StatusMessage = "Copied LLM review package.";
    }

    [RelayCommand]
    public async Task SaveLlmExportAsync()
    {
        if (_currentSnapshot is null) return;
        if (string.IsNullOrWhiteSpace(LlmExportPreview)) LlmExportPreview = BuildLlmExport();
        string dir = _sessions.GetExportDir(_currentSnapshot.WorkspaceKey);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"review-export-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, LlmExportPreview);
        StatusMessage = $"Saved LLM export: {Path.GetFileName(path)}";
    }

    [RelayCommand]
    public async Task DuplicateSelectedAsync()
    {
        var selected = Scene.Blocks.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) return;
        var duplicates = selected.Select(DuplicateBlock).ToList();
        SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Concat(duplicates).ToList() }, $"Duplicated {duplicates.Count} item(s)");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public void CopySelectedBoardItems()
    {
        var selected = Scene.Blocks.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) return;
        Clipboard.SetText(JsonSerializer.Serialize(selected));
        StatusMessage = $"Copied {selected.Count} board item(s).";
    }

    [RelayCommand]
    public async Task PasteBoardItemsAsync()
    {
        if (Clipboard.ContainsImage())
        {
            await PasteImageFromClipboardAsync();
            return;
        }

        if (!Clipboard.ContainsText()) return;
        try
        {
            var items = JsonSerializer.Deserialize<List<RenderBlock>>(Clipboard.GetText());
            if (items is null || items.Count == 0) return;
            var pasted = items.Select(DuplicateBlock).ToList();
            SetSceneFromUserAction(Scene with { Blocks = Scene.Blocks.Concat(pasted).ToList() }, $"Pasted {pasted.Count} board item(s)");
            await PersistSessionAsync();
        }
        catch
        {
            StatusMessage = "Clipboard does not contain board items.";
        }
    }

    [RelayCommand]
    public async Task ApplySelectionPropertiesAsync()
    {
        if (SelectedBlock is not null)
        {
            var style = SelectedBlock.Style ?? new BoardItemStyle();
            double x = ParseDoubleOr(SelectedObjectX, SelectedBlock.X);
            double y = ParseDoubleOr(SelectedObjectY, SelectedBlock.Y);
            double width = Math.Max(80, ParseDoubleOr(SelectedObjectWidth, SelectedBlock.Width));
            double height = Math.Max(60, ParseDoubleOr(SelectedObjectHeight, SelectedBlock.Height));
            var nextStyle = style with
            {
                Fill = NormalizeHex(SelectedFill, style.Fill),
                Stroke = NormalizeHex(SelectedStroke, style.Stroke),
                Text = NormalizeHex(SelectedTextColor, style.Text),
                StrokeWidth = Math.Clamp(ParseDoubleOr(SelectedStrokeWidth, style.StrokeWidth), 0.5, 8),
                TextAlign = NormalizeTextAlignment(SelectedTextAlignment),
                Dashed = SelectedDashed
            };
            var blocks = Scene.Blocks.Select(b =>
                b.Key.Equals(SelectedBlock.Key, StringComparison.OrdinalIgnoreCase)
                    ? b with
                    {
                        X = x,
                        Y = y,
                        Width = width,
                        Height = height,
                        Title = string.IsNullOrWhiteSpace(SelectedTitleDraft) ? b.Title : SelectedTitleDraft.Trim(),
                        Body = b.Kind is BlockKind.Shape or BlockKind.Text or BlockKind.Note ? SelectedBodyDraft : b.Body,
                        Style = nextStyle,
                        IsLocked = SelectedLocked
                    }
                    : b).ToList();
            SetSceneFromUserAction(Scene with { Blocks = blocks }, "Updated board item");
            await PersistSessionAsync();
            return;
        }

        if (SelectedSwimLane is not null)
        {
            double x = ParseDoubleOr(SelectedObjectX, SelectedSwimLane.X);
            double y = ParseDoubleOr(SelectedObjectY, SelectedSwimLane.Y);
            double width = Math.Max(200, ParseDoubleOr(SelectedObjectWidth, SelectedSwimLane.Width));
            double height = Math.Max(120, ParseDoubleOr(SelectedObjectHeight, SelectedSwimLane.Height));
            var lanes = Scene.SwimLanes.Select(l =>
                l.Key.Equals(SelectedSwimLane.Key, StringComparison.OrdinalIgnoreCase)
                    ? l with { X = x, Y = y, Width = width, Height = height }
                    : l).ToList();
            SetSceneFromUserAction(Scene with { SwimLanes = lanes }, "Updated frame geometry");
            await PersistSessionAsync();
            return;
        }

        if (SelectedConnection is not null)
        {
            Enum.TryParse(SelectedRouteKind, out ConnectorRouteKind routeKind);
            Enum.TryParse(SelectedArrowKind, out ConnectorArrowKind arrowKind);
            var connections = Scene.Connections.Select(c =>
                c.Id == SelectedConnection.Id
                    ? c with
                    {
                        Label = string.IsNullOrWhiteSpace(SelectedConnectionLabel) ? null : SelectedConnectionLabel.Trim(),
                        Stroke = NormalizeHex(SelectedStroke, c.Stroke),
                        Dashed = SelectedDashed,
                        RouteKind = routeKind,
                        ArrowKind = arrowKind,
                        MidControlBends = routeKind == ConnectorRouteKind.Curved && c.MidControlBends
                    }
                    : c).ToList();
            SetSceneFromUserAction(Scene with { Connections = connections }, "Updated connector");
            await PersistSessionAsync();
        }
    }

    [RelayCommand]
    public async Task SetConnectorRouteAsync(string? route)
    {
        if (SelectedConnection is null || !Enum.TryParse(route, out ConnectorRouteKind routeKind)) return;
        SelectedRouteKind = routeKind.ToString();
        await ApplySelectionPropertiesAsync();
    }

    [RelayCommand]
    public async Task SetConnectorArrowAsync(string? arrow)
    {
        if (SelectedConnection is null || !Enum.TryParse(arrow, out ConnectorArrowKind arrowKind)) return;
        SelectedArrowKind = arrowKind.ToString();
        await ApplySelectionPropertiesAsync();
    }

    [RelayCommand]
    public async Task ChangeZOrderAsync(string? mode)
    {
        if (SelectedBlock is null || string.IsNullOrWhiteSpace(mode)) return;
        int min = Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Min(b => b.ZIndex);
        int max = Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Max(b => b.ZIndex);
        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(SelectedBlock.Key, StringComparison.OrdinalIgnoreCase)) return b;
            int z = mode switch
            {
                "front" => max + 1,
                "forward" => b.ZIndex + 1,
                "backward" => b.ZIndex - 1,
                "back" => min - 1,
                _ => b.ZIndex
            };
            return b with { ZIndex = z };
        }).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, "Changed z-order");
        await PersistSessionAsync();
    }

    [RelayCommand]
    public Task NavigateBoardSearchResultAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;
        if (Guid.TryParseExact(key, "N", out var connectionId))
        {
            Scene = Scene with
            {
                Blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).ToList(),
                SwimLanes = Scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList(),
                Connections = Scene.Connections.Select(c => c with { IsSelected = c.Id == connectionId }).ToList()
            };
            StatusMessage = "Selected search result connector.";
            return Task.CompletedTask;
        }

        Scene = Scene with
        {
            Blocks = Scene.Blocks.Select(b => b with { IsSelected = b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) }).ToList(),
            SwimLanes = Scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList(),
            Connections = Scene.Connections.Select(c => c with { IsSelected = false }).ToList()
        };
        StatusMessage = "Selected search result.";
        return Task.CompletedTask;
    }

    private static string NormalizeHex(string value, string fallback)
    {
        string text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        return text.Length is 7 or 9 && text.Skip(1).All(Uri.IsHexDigit) ? text : fallback;
    }

    private static double ParseDoubleOr(string value, double fallback) =>
        double.TryParse(value, out double parsed) ? parsed : fallback;

    private RenderBlock DuplicateBlock(RenderBlock block)
    {
        var id = Guid.NewGuid();
        return block with
        {
            Id = id,
            Key = $"{block.Kind.ToString().ToLowerInvariant()}::{id:N}",
            X = block.X + 32,
            Y = block.Y + 32,
            IsSelected = true
        };
    }

    private string CopyToAssetFolder(string sourcePath, string kind)
    {
        if (_currentSnapshot is null) return sourcePath;
        string dir = _sessions.GetAssetDir(_currentSnapshot.WorkspaceKey, kind);
        Directory.CreateDirectory(dir);
        string name = $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}";
        string target = Path.Combine(dir, name);
        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    private string BuildLlmExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ReviewScope Code Review Package");
        sb.AppendLine();
        sb.AppendLine($"Project: {_currentSnapshot?.DisplayName ?? "Unknown"}");
        sb.AppendLine($"Board: {_activeSession?.Name ?? "New Session"}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        AppendExportSection(sb, "Global Notes", Scene.Blocks.Where(b => b.Kind == BlockKind.Note && !Scene.Connections.Any(c => c.Label == "__note" && c.TargetKey == b.Key)));
        AppendExportSection(sb, "Architecture Docs", Scene.Blocks.Where(b => b.Kind == BlockKind.MarkdownDoc || (b.Kind == BlockKind.File && b.ShapeType == "markdown")));
        AppendExportSection(sb, "Diagram Elements", Scene.Blocks.Where(b => b.Kind is BlockKind.Shape or BlockKind.Container or BlockKind.Text or BlockKind.Image));

        sb.AppendLine("## Code Evidence and Attached Notes");
        sb.AppendLine();
        foreach (var block in Scene.Blocks.Where(b => b.Kind is BlockKind.File or BlockKind.Extract).OrderBy(b => b.FilePath).ThenBy(b => b.StartLine))
        {
            sb.AppendLine($"### {block.Title}");
            if (!string.IsNullOrWhiteSpace(block.FilePath)) sb.AppendLine($"File: `{GetRelativePath(block.FilePath)}`");
            if (block.StartLine.HasValue && block.EndLine.HasValue) sb.AppendLine($"Lines: {block.StartLine}-{block.EndLine}");
            sb.AppendLine();
            var attachedNotes = Scene.Connections
                .Where(c => c.Label == "__note" && c.SourceKey.Equals(block.Key, StringComparison.OrdinalIgnoreCase))
                .Select(c => Scene.Blocks.FirstOrDefault(b => b.Key.Equals(c.TargetKey, StringComparison.OrdinalIgnoreCase)))
                .Where(b => b is not null)
                .Cast<RenderBlock>()
                .ToList();
            foreach (var note in attachedNotes)
            {
                sb.AppendLine($"- Note: {note.Body ?? note.Title}");
            }
            if (!string.IsNullOrWhiteSpace(block.Body))
            {
                string lang = block.Source?.SourceLanguage == "xaml" ? "xml" : "csharp";
                sb.AppendLine();
                sb.AppendLine($"```{lang}");
                sb.AppendLine(TrimForExport(block.Body));
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendExportSection(StringBuilder sb, string title, IEnumerable<RenderBlock> blocks)
    {
        var list = blocks.ToList();
        if (list.Count == 0) return;
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        foreach (var block in list)
        {
            sb.AppendLine($"### {block.Title}");
            if (!string.IsNullOrWhiteSpace(block.Body)) sb.AppendLine(TrimForExport(block.Body));
            sb.AppendLine();
        }
    }

    private static string TrimForExport(string text)
    {
        const int maxChars = 8000;
        string trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "\n\n[truncated]";
    }

    private static bool Matches(RenderBlock block, string query) =>
        block.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || block.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (block.Body?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        || (block.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        || (block.ShapeType?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private static string ResolveBoardIconKind(RenderBlock block) =>
        block.Kind switch
        {
            BlockKind.File => block.ShapeType is "markdown" ? "markdown" : "file",
            BlockKind.Extract => "method",
            BlockKind.Note => "note",
            BlockKind.MarkdownDoc => "markdown",
            BlockKind.Shape => block.ShapeType switch
            {
                "database" => "database",
                "risk" or "bug" or "todo" or "test" => "risk",
                _ => "shape"
            },
            BlockKind.Text => "text",
            BlockKind.Image => "image",
            BlockKind.Container => "container",
            _ => "search"
        };

    private static string ToTitle(string value) =>
        string.Join(" ", value.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
