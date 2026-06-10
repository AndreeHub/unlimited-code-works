using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReviewScope.Domain;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace ReviewScope.Canvas;

/*
 * File: ExcalidrawLibraryImporter.cs
 * Purpose: Phase 1 of native Excalidraw-library support. Parses an `.excalidrawlib`
 * (or a raw `.excalidraw` scene) JSON document and maps the elements onto this app's
 * native shape model (RenderBlock + BoardItemStyle), so library items render in the
 * existing Direct2D engine with no web/WebView dependency.
 *
 * Scope (Phase 1): the element types that map cleanly to existing shapes —
 *   rectangle, ellipse, diamond, line, arrow, and (best-effort) standalone text.
 * Out of scope (later phases): freedraw strokes (Phase 3), images (Phase 5),
 * grouping (Phase 2), rotation + per-element roughjs seed fidelity (Phase 4).
 * Unsupported element types are counted and reported via ImportResult.SkippedTypes
 * rather than silently dropped.
 *
 * Coordinate handling: every supported element is mapped to an absolute world rect,
 * then the whole collection is translated so its top-left bounding-box corner lands
 * at the requested drop point. Linear shapes encode their vertices with the same
 * normalized "points:x,y;..." body format the interactive ShapeTool produces, so an
 * imported line/arrow is indistinguishable from a hand-drawn one.
 */
public static class ExcalidrawLibraryImporter
{
    /// <summary>Result of an import: the blocks to add to the scene plus a tally of element
    /// types that were skipped (e.g. "freedraw", "image"), so the UI can tell the user.</summary>
    public sealed record ImportResult(
        IReadOnlyList<RenderBlock> Blocks,
        IReadOnlyDictionary<string, int> SkippedTypes)
    {
        public static ImportResult Empty { get; } =
            new(Array.Empty<RenderBlock>(), new Dictionary<string, int>());

        public int SkippedCount => SkippedTypes.Values.Sum();
    }

    /// <summary>One importable library item: a friendly name plus its shapes normalized so the
    /// item's bounding-box top-left sits at (0,0). Used to populate the reusable Library panel.</summary>
    public sealed record ImportedItem(string Name, IReadOnlyList<RenderBlock> Blocks, double Width, double Height);

    /// <summary>Reads and imports an `.excalidrawlib` / `.excalidraw` file from disk.</summary>
    public static ImportResult ImportFile(string path, WpfPoint dropAt, string? imageAssetDir = null)
        => Import(File.ReadAllText(path), dropAt, imageAssetDir);

    /// <summary>Reads a file and returns its library items separately (for the Library panel).</summary>
    public static IReadOnlyList<ImportedItem> ImportItemsFromFile(string path, string? imageAssetDir = null)
        => ImportItems(File.ReadAllText(path), imageAssetDir);

    /// <summary>
    /// Parses library/scene JSON into discrete items (one per `.excalidrawlib` libraryItem, or the
    /// whole scene as a single item). Each item's shapes are normalized to its own origin so the
    /// panel can render a thumbnail and re-place it anywhere. Items with no mappable shapes are
    /// dropped. Returns an empty list on malformed input.
    /// </summary>
    public static IReadOnlyList<ImportedItem> ImportItems(string json, string? imageAssetDir = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ImportedItem>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Array.Empty<ImportedItem>(); }

        using (doc)
        {
            var filesMap = ReadFilesMap(doc.RootElement);
            var result = new List<ImportedItem>();
            int index = 0;
            foreach (var raw in CollectItems(doc.RootElement))
            {
                index++;
                var mapped = MapItemElements(raw.Elements, skipped: null, filesMap, imageAssetDir);
                if (mapped.Count == 0) continue;

                double minX = mapped.Min(m => m.Bounds.X);
                double minY = mapped.Min(m => m.Bounds.Y);
                double maxX = mapped.Max(m => m.Bounds.Right);
                double maxY = mapped.Max(m => m.Bounds.Bottom);
                // Normalize each block so the item's top-left is at (0,0).
                var blocks = mapped.Select(m => m.ToBlock(-minX, -minY)).ToList();
                string name = string.IsNullOrWhiteSpace(raw.Name) ? $"Item {index}" : raw.Name!;
                result.Add(new ImportedItem(name, blocks, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)));
            }
            return result;
        }
    }

    /// <summary>
    /// Imports library/scene JSON, positioning the imported content's top-left corner at
    /// <paramref name="dropAt"/> in world coordinates.
    /// </summary>
    public static ImportResult Import(string json, WpfPoint dropAt, string? imageAssetDir = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return ImportResult.Empty;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return ImportResult.Empty; }

        using (doc)
        {
            var filesMap = ReadFilesMap(doc.RootElement);
            var items = CollectItems(doc.RootElement).ToList();
            if (items.Count == 0) return ImportResult.Empty;

            var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var mapped = new List<MappedElement>();

            foreach (var rawItem in items)
            {
                var itemMapped = MapItemElements(rawItem.Elements, skipped, filesMap, imageAssetDir);

                // A multi-element library item becomes one logical group so it moves/selects as a
                // unit. A single-element item needs no group.
                string? groupKey = itemMapped.Count >= 2 ? $"group::{Guid.NewGuid():N}" : null;
                foreach (var m in itemMapped)
                    mapped.Add(m with { GroupKey = groupKey });
            }

            if (mapped.Count == 0)
                return new ImportResult(Array.Empty<RenderBlock>(), skipped);

            // Translate the whole collection so its bounding-box top-left lands at dropAt.
            double minX = mapped.Min(m => m.Bounds.X);
            double minY = mapped.Min(m => m.Bounds.Y);
            double offX = dropAt.X - minX;
            double offY = dropAt.Y - minY;

            var blocks = mapped
                .Select(m => m.ToBlock(offX, offY) with { GroupKey = m.GroupKey })
                .ToList();

            return new ImportResult(blocks, skipped);
        }
    }

    // -----------------------------------------------------------------------
    // Element collection: handle both `.excalidrawlib` and raw `.excalidraw`.
    // Each returned list is one "item" whose elements should be grouped together: a library item,
    // or the whole scene / bare array (treated as a single item).
    // -----------------------------------------------------------------------
    private sealed record RawItem(string? Name, List<JsonElement> Elements);

    private static IEnumerable<RawItem> CollectItems(JsonElement root)
    {
        // `.excalidrawlib`: { type:"excalidrawlib", libraryItems:[ { name?, elements:[...] }, ... ] }
        if (root.TryGetProperty("libraryItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                // Newer format: item is an object with an `elements` array (and maybe a name).
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("elements", out var itemEls) && itemEls.ValueKind == JsonValueKind.Array)
                {
                    yield return new RawItem(GetString(item, "name"), itemEls.EnumerateArray().ToList());
                }
                // Older format: item is itself an array of elements.
                else if (item.ValueKind == JsonValueKind.Array)
                {
                    yield return new RawItem(null, item.EnumerateArray().ToList());
                }
            }
            yield break;
        }

        // `.excalidraw` scene: { type:"excalidraw", elements:[...] } — one item.
        if (root.TryGetProperty("elements", out var sceneEls) && sceneEls.ValueKind == JsonValueKind.Array)
        {
            yield return new RawItem(null, sceneEls.EnumerateArray().ToList());
            yield break;
        }

        // Bare array of elements — one item.
        if (root.ValueKind == JsonValueKind.Array)
        {
            yield return new RawItem(null, root.EnumerateArray().ToList());
        }
    }

    // -----------------------------------------------------------------------
    // Per-element mapping.
    // -----------------------------------------------------------------------
    private sealed record MappedElement(WpfRect Bounds, Func<double, double, RenderBlock> ToBlock, string? GroupKey = null);

    /// <summary>
    /// Maps one item's raw elements to shapes. Text elements bound to a closed container shape
    /// (Excalidraw's containerId) become that shape's centered label instead of a standalone
    /// floating text box, matching how Excalidraw renders them.
    /// </summary>
    private static List<MappedElement> MapItemElements(
        List<JsonElement> elements,
        Dictionary<string, int>? skipped,
        JsonElement? filesMap = null,
        string? imageAssetDir = null)
    {
        // First pass: index container types by element id, then collect bound labels.
        var typeById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in elements)
        {
            if (IsDeleted(el)) continue;
            if (GetString(el, "id") is string id)
                typeById[id] = GetString(el, "type") ?? "";
        }

        var labels = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var el in elements)
        {
            if (IsDeleted(el) || (GetString(el, "type") ?? "") != "text") continue;
            if (GetString(el, "containerId") is not string cid) continue;
            if (typeById.GetValueOrDefault(cid) is "rectangle" or "ellipse" or "diamond")
                labels[cid] = el;
        }
        var consumedLabels = new HashSet<string>(labels.Values
            .Select(l => GetString(l, "id") ?? string.Empty)
            .Where(id => id.Length > 0), StringComparer.Ordinal);

        var mapped = new List<MappedElement>();
        foreach (var el in elements)
        {
            if (IsDeleted(el)) continue;
            string type = GetString(el, "type") ?? "";
            if (type == "text" && GetString(el, "id") is string textId && consumedLabels.Contains(textId))
                continue; // rendered as its container's label

            JsonElement? label = GetString(el, "id") is string elId && labels.TryGetValue(elId, out var l) ? l : null;
            var m = MapElement(el, type, label, filesMap, imageAssetDir);
            if (m is null)
            {
                if (skipped is not null)
                {
                    string key = string.IsNullOrEmpty(type) ? "unknown" : type;
                    skipped[key] = skipped.GetValueOrDefault(key) + 1;
                }
                continue;
            }
            mapped.Add(m);
        }
        return mapped;
    }

    private static MappedElement? MapElement(JsonElement el, string type, JsonElement? boundLabel = null, JsonElement? filesMap = null, string? imageAssetDir = null)
    {
        switch (type)
        {
            case "rectangle":
            case "ellipse":
            case "diamond":
                return MapClosedShape(el, type, boundLabel);
            case "line":
            case "arrow":
                return MapLinear(el, type);
            case "freedraw":
                return MapFreedraw(el);
            case "text":
                return MapText(el);
            case "image":
                return MapImage(el, filesMap, imageAssetDir);
            default:
                // frame/embeddable/etc — skipped.
                return null;
        }
    }

    /// <summary>The root-level `files` map of a `.excalidraw` scene: fileId → { mimeType, dataURL }.</summary>
    private static JsonElement? ReadFilesMap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("files", out var files)
        && files.ValueKind == JsonValueKind.Object
            ? files
            : null;

    /// <summary>
    /// Maps an `image` element by decoding its base64 dataURL from the scene's `files` map into
    /// <paramref name="imageAssetDir"/> (fileId-named, so re-imports de-duplicate) and emitting a
    /// frameless Image block stretched over the element bounds. Returns null (= counted skipped)
    /// when no asset dir was provided, the file entry is missing, or the format is undecodable.
    /// </summary>
    private static MappedElement? MapImage(JsonElement el, JsonElement? filesMap, string? imageAssetDir)
    {
        if (imageAssetDir is null || filesMap is null) return null;
        if (GetString(el, "fileId") is not string fileId) return null;
        if (!filesMap.Value.TryGetProperty(fileId, out var fileEntry)) return null;

        string? path = TryWriteImageAsset(fileId, GetString(fileEntry, "dataURL"), imageAssetDir);
        if (path is null) return null;

        double x = GetDouble(el, "x");
        double y = GetDouble(el, "y");
        double w = Math.Max(1, GetDouble(el, "width"));
        double h = Math.Max(1, GetDouble(el, "height"));
        var bounds = new WpfRect(x, y, w, h);

        double opacity = GetDouble(el, "opacity", 100) / 100.0;
        double angle = GetDouble(el, "angle");
        var style = new BoardItemStyle(
            Fill: "#00FFFFFF", Stroke: "#00FFFFFF", Text: "#1E1E1E",
            StrokeWidth: 1, Opacity: opacity, CornerRadius: 0, FillStyle: "solid",
            Rotation: Math.Abs(angle) > 1e-6 ? angle * 180.0 / Math.PI : 0);

        return new MappedElement(bounds, (offX, offY) =>
        {
            var id = Guid.NewGuid();
            return new RenderBlock(
                id, $"image::{id:N}", BlockKind.Image,
                Title: string.Empty, Subtitle: string.Empty,
                X: x + offX, Y: y + offY, Width: w, Height: h,
                ShapeType: "image-raw", Style: style,
                Source: new BoardSourceBinding(AssetPath: path, SourceLanguage: "image"));
        });
    }

    /// <summary>Decodes a `data:image/...;base64,...` URL to a file named after the fileId
    /// (Excalidraw's content hash). Returns null for undecodable formats (svg, webp) or IO errors.</summary>
    private static string? TryWriteImageAsset(string fileId, string? dataUrl, string dir)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        int comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        string header = dataUrl[5..comma]; // e.g. "image/png;base64"
        int semi = header.IndexOf(';');
        string mime = (semi >= 0 ? header[..semi] : header).Trim().ToLowerInvariant();
        string? ext = mime switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => null, // svg / webp aren't decodable by the WPF/D2D loader
        };
        if (ext is null || !header.EndsWith("base64", StringComparison.OrdinalIgnoreCase)) return null;

        var safe = new string(fileId.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = Guid.NewGuid().ToString("N");
        string path = Path.Combine(dir, safe + ext);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, Convert.FromBase64String(dataUrl[(comma + 1)..]));
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static MappedElement MapClosedShape(JsonElement el, string type, JsonElement? boundLabel)
    {
        double x = GetDouble(el, "x");
        double y = GetDouble(el, "y");
        double w = Math.Max(1, GetDouble(el, "width"));
        double h = Math.Max(1, GetDouble(el, "height"));
        var bounds = new WpfRect(x, y, w, h);

        string shapeType = type switch
        {
            "ellipse" => "oval",
            "diamond" => "diamond",
            _ => "rectangle",
        };
        var style = MapClosedStyle(el, type);

        // Excalidraw stores `angle` in radians, rotating around the element center; x/y/w/h stay
        // the unrotated frame. Our renderer mirrors that via Style.Rotation (degrees).
        double angle = GetDouble(el, "angle");
        if (Math.Abs(angle) > 1e-6)
            style = style with { Rotation = angle * 180.0 / Math.PI };

        string? labelText = null;
        if (boundLabel is JsonElement label)
        {
            labelText = GetString(label, "text");
            if (!string.IsNullOrWhiteSpace(labelText))
            {
                style = style with
                {
                    Text = NormalizeColor(GetString(label, "strokeColor"), "#1E1E1E"),
                    FontSize = GetDouble(label, "fontSize", 16),
                    TextAlign = MapTextAlign(GetString(label, "textAlign"), "Center"),
                    VerticalAlign = (GetString(label, "verticalAlign") ?? "middle") switch
                    {
                        "top" => "Top",
                        "bottom" => "Bottom",
                        _ => "Middle",
                    },
                };
            }
            else
            {
                labelText = null;
            }
        }

        return new MappedElement(bounds, (offX, offY) =>
        {
            var id = Guid.NewGuid();
            return new RenderBlock(
                id, $"{shapeType}::{id:N}", BlockKind.Shape,
                Title: string.Empty, Subtitle: string.Empty,
                X: x + offX, Y: y + offY, Width: w, Height: h,
                Body: labelText, ShapeType: shapeType, Style: style);
        });
    }

    private static MappedElement? MapLinear(JsonElement el, string type)
    {
        var verts = ReadAbsoluteRotatedPoints(el);
        if (verts.Count < 2) return null;

        // Derive a tight bbox from the (rotation-baked) absolute points rather than trusting
        // width/height (robust to negative offsets and rotated elements).
        double minPx = verts.Min(p => p.X), minPy = verts.Min(p => p.Y);
        double maxPx = verts.Max(p => p.X), maxPy = verts.Max(p => p.Y);
        double w = Math.Max(1, maxPx - minPx);
        double h = Math.Max(1, maxPy - minPy);
        double bx = minPx, by = minPy;
        var bounds = new WpfRect(bx, by, w, h);

        // Encode as normalized vertices in the same body format the ShapeTool produces.
        // A non-null `roundness` means Excalidraw renders the polyline smoothed; our renderer
        // treats interior vertices flagged "curved" as Bézier controls, which is the closest match.
        bool[]? curvedFlags = null;
        if (verts.Count > 2 && HasRoundness(el))
        {
            curvedFlags = new bool[verts.Count];
            for (int i = 1; i < verts.Count - 1; i++) curvedFlags[i] = true;
        }
        string body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, verts, curvedFlags: curvedFlags);

        string shapeType = type == "arrow" ? "arrow" : (verts.Count > 2 ? "polyline" : "line");
        var style = MapLinearStyle(el);

        return new MappedElement(bounds, (offX, offY) =>
        {
            var id = Guid.NewGuid();
            return new RenderBlock(
                id, $"{shapeType}::{id:N}", BlockKind.Shape,
                Title: string.Empty, Subtitle: string.Empty,
                X: bx + offX, Y: by + offY, Width: w, Height: h,
                Body: body, ShapeType: shapeType, Style: style);
        });
    }

    private static MappedElement? MapFreedraw(JsonElement el)
    {
        var verts = ReadAbsoluteRotatedPoints(el);
        if (verts.Count < 2) return null;

        double minPx = verts.Min(p => p.X), minPy = verts.Min(p => p.Y);
        double maxPx = verts.Max(p => p.X), maxPy = verts.Max(p => p.Y);
        double w = Math.Max(1, maxPx - minPx);
        double h = Math.Max(1, maxPy - minPy);
        double bx = minPx, by = minPy;
        var bounds = new WpfRect(bx, by, w, h);

        string body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, verts);

        string stroke = NormalizeColor(GetString(el, "strokeColor"), "#1E1E1E");
        double strokeWidth = Math.Clamp(GetDouble(el, "strokeWidth", 2), 0.5, 8);
        double opacity = GetDouble(el, "opacity", 100) / 100.0;
        var style = new BoardItemStyle(
            Fill: "#00FFFFFF", Stroke: stroke, Text: stroke,
            StrokeWidth: strokeWidth, Opacity: opacity, CornerRadius: 0);

        return new MappedElement(bounds, (offX, offY) =>
        {
            var id = Guid.NewGuid();
            return new RenderBlock(
                id, $"freedraw::{id:N}", BlockKind.Shape,
                Title: string.Empty, Subtitle: string.Empty,
                X: bx + offX, Y: by + offY, Width: w, Height: h,
                Body: body, ShapeType: "freedraw", Style: style);
        });
    }

    private static MappedElement? MapText(JsonElement el)
    {
        string text = GetString(el, "text") ?? "";
        if (string.IsNullOrWhiteSpace(text)) return null;

        double x = GetDouble(el, "x");
        double y = GetDouble(el, "y");
        double w = Math.Max(8, GetDouble(el, "width"));
        double h = Math.Max(8, GetDouble(el, "height"));
        var bounds = new WpfRect(x, y, w, h);

        string textColor = NormalizeColor(GetString(el, "strokeColor"), "#1E1E1E");
        double opacity = (GetDouble(el, "opacity", 100)) / 100.0;
        double fontSize = GetDouble(el, "fontSize", 16);
        string align = MapTextAlign(GetString(el, "textAlign"), "Left");

        // Render a standalone text element as a fully transparent shape that only carries a label.
        var style = new BoardItemStyle(
            Fill: "#00FFFFFF", Stroke: "#00FFFFFF", Text: textColor,
            StrokeWidth: 1.0, Opacity: opacity, CornerRadius: 0,
            TextAlign: align, FillStyle: "solid", FontSize: fontSize,
            VerticalAlign: "Middle");

        double angle = GetDouble(el, "angle");
        if (Math.Abs(angle) > 1e-6)
            style = style with { Rotation = angle * 180.0 / Math.PI };

        return new MappedElement(bounds, (offX, offY) =>
        {
            var id = Guid.NewGuid();
            return new RenderBlock(
                id, $"text::{id:N}", BlockKind.Shape,
                Title: string.Empty, Subtitle: string.Empty,
                X: x + offX, Y: y + offY, Width: w, Height: h,
                Body: text, ShapeType: "rectangle", Style: style);
        });
    }

    // -----------------------------------------------------------------------
    // Style mapping.
    // -----------------------------------------------------------------------
    private static BoardItemStyle MapClosedStyle(JsonElement el, string type)
    {
        string stroke = NormalizeColor(GetString(el, "strokeColor"), "#1E1E1E");
        string fill = NormalizeColor(GetString(el, "backgroundColor"), "#00FFFFFF");
        double strokeWidth = Math.Clamp(GetDouble(el, "strokeWidth", 1), 0.5, 8);
        string strokeStyle = GetString(el, "strokeStyle") ?? "solid";
        double opacity = GetDouble(el, "opacity", 100) / 100.0;
        string fillStyle = (GetString(el, "fillStyle") ?? "hachure") switch
        {
            "solid" => "solid",
            "cross-hatch" => "cross-hatch",
            "zigzag" => "zigzag",
            "dots" => "dots",
            _ => "hatch",
        };
        // rectangles in Excalidraw round their corners when `roundness` is non-null.
        double cornerRadius = type == "rectangle" && HasRoundness(el) ? 12 : 0;

        return new BoardItemStyle(
            Fill: fill, Stroke: stroke, Text: "#1E1E1E",
            StrokeWidth: strokeWidth, Dashed: strokeStyle == "dashed", Opacity: opacity,
            CornerRadius: cornerRadius, FillStyle: fillStyle,
            Dotted: strokeStyle == "dotted");
    }

    private static BoardItemStyle MapLinearStyle(JsonElement el)
    {
        string stroke = NormalizeColor(GetString(el, "strokeColor"), "#1E1E1E");
        double strokeWidth = Math.Clamp(GetDouble(el, "strokeWidth", 1), 0.5, 8);
        string strokeStyle = GetString(el, "strokeStyle") ?? "solid";
        double opacity = GetDouble(el, "opacity", 100) / 100.0;

        return new BoardItemStyle(
            Fill: "#00FFFFFF", Stroke: stroke, Text: "#172033",
            StrokeWidth: strokeWidth, Dashed: strokeStyle == "dashed", Opacity: opacity,
            CornerRadius: 0,
            Dotted: strokeStyle == "dotted");
    }

    // -----------------------------------------------------------------------
    // JSON helpers.
    // -----------------------------------------------------------------------
    private static bool IsDeleted(JsonElement el) =>
        el.TryGetProperty("isDeleted", out var d) && d.ValueKind == JsonValueKind.True;

    private static bool HasRoundness(JsonElement el) =>
        el.TryGetProperty("roundness", out var r) && r.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static List<WpfPoint> ReadPoints(JsonElement el)
    {
        var result = new List<WpfPoint>();
        if (!el.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var p in pts.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array) continue;
            var coords = p.EnumerateArray().ToArray();
            if (coords.Length < 2) continue;
            if (coords[0].TryGetDouble(out double px) && coords[1].TryGetDouble(out double py))
                result.Add(new WpfPoint(px, py));
        }
        return result;
    }

    /// <summary>
    /// Reads a linear/freedraw element's points as ABSOLUTE world coordinates with any `angle`
    /// baked in: Excalidraw rotates around the element's declared (x + w/2, y + h/2) center, and
    /// since our linear shapes carry no rotation of their own, rotating the vertices up front
    /// reproduces the element exactly.
    /// </summary>
    private static List<WpfPoint> ReadAbsoluteRotatedPoints(JsonElement el)
    {
        var pts = ReadPoints(el);
        double ex = GetDouble(el, "x");
        double ey = GetDouble(el, "y");
        var abs = pts.Select(p => new WpfPoint(ex + p.X, ey + p.Y)).ToList();

        double angle = GetDouble(el, "angle");
        if (Math.Abs(angle) <= 1e-6 || abs.Count == 0) return abs;

        double cx = ex + GetDouble(el, "width") / 2;
        double cy = ey + GetDouble(el, "height") / 2;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return abs.Select(p =>
        {
            double dx = p.X - cx, dy = p.Y - cy;
            return new WpfPoint(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }).ToList();
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement el, string name, double fallback = 0)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
            ? d : fallback;

    private static string MapTextAlign(string? excalidrawAlign, string fallback) =>
        excalidrawAlign switch
        {
            "center" => "Center",
            "right" => "Right",
            "left" => "Left",
            _ => fallback,
        };

    /// <summary>
    /// Excalidraw uses the literal "transparent" and CSS hex strings: #rgb / #rgba shorthand and
    /// #rrggbbaa with the alpha LAST. The renderer's parser expects #rrggbb or #aarrggbb (alpha
    /// first), so expand shorthand and move the alpha channel. Named colors fall back.
    /// </summary>
    private static string NormalizeColor(string? c, string fallback)
    {
        if (string.IsNullOrWhiteSpace(c)) return fallback;
        c = c.Trim();
        if (c.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "#00FFFFFF";
        if (!c.StartsWith('#')) return fallback;

        string hex = c[1..];
        if (!hex.All(Uri.IsHexDigit)) return fallback;
        if (hex.Length is 3 or 4)
            hex = string.Concat(hex.Select(ch => new string(ch, 2)));
        return hex.Length switch
        {
            6 => "#" + hex,
            8 => "#" + hex[6..] + hex[..6], // RGBA → ARGB
            _ => fallback,
        };
    }
}
