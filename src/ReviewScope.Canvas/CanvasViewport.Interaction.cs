using ReviewScope.Domain;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using WpfPoint = System.Windows.Point;
using RectangleF = System.Drawing.RectangleF;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.Interaction.cs
 * Purpose: Partial class for CanvasViewport handling hit testing and interaction logic for shapes, blocks, and swim lanes.
 * Functions:
 * - HitShapeTool: Hit testing for the shape tool palette.
 * - CreateShapeBlock: Factory for creating new shape blocks.
 * - HitBlock, HitSwimLane: Hit testing for main canvas items.
 * - IsInResize, HitNoteCorner: Hit testing for resize handles.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

public sealed partial class CanvasViewport
{
    internal sealed record LinearShapeVertexHit(SceneBlockVisual Block, int VertexIndex, WpfPoint Point);
    internal sealed record BlockResizeCornerHit(SceneBlockVisual Block, NoteResizeCorner Corner);

    internal string? HitShapeTool(WpfPoint screen)
    {
        var buttons = GetShapeToolButtonBounds(new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        foreach (var (tool, bounds) in buttons)
        {
            if (bounds.Contains((float)screen.X, (float)screen.Y))
                return tool;
        }

        return null;
    }

    private Dictionary<string, RectangleF> GetShapeToolButtonBounds(Size viewSize)
    {
        var tools = ShapeToolIds;
        float columns = 4;
        float x = (float)ShapeToolPaletteMargin;

        // Note: This logic must mirror UIComponentRenderer.DrawShapeToolPalette exactly
        float rows = (float)Math.Ceiling(tools.Length / columns);
        float h = ShapeToolPalettePadding * 2 + rows * ShapeToolButtonSize + (rows - 1) * ShapeToolButtonGap;
        float startY = (float)viewSize.Height - h - ShapeToolPaletteMargin;

        var result = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tools.Length; i++)
        {
            int col = i % (int)columns;
            int row = i / (int)columns;
            var button = new RectangleF(
                x + ShapeToolPalettePadding + col * (ShapeToolButtonSize + ShapeToolButtonGap),
                startY + ShapeToolPalettePadding + row * (ShapeToolButtonSize + ShapeToolButtonGap),
                ShapeToolButtonSize,
                ShapeToolButtonSize);
            result[tools[i]] = button;
        }
        return result;
    }

    internal static string ShapeToolTitle(string shapeType) =>
        string.Join(" ", shapeType.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    internal static Size ShapeToolDefaultSize(string shapeType) => shapeType switch
    {
        "square" or "circle" or "star" or "hexagon" => new Size(150, 150),
        "triangle" or "diamond" => new Size(170, 145),
        "line" or "arrow" or "polyline" => new Size(220, 80),
        "oval" or "rectangle" => new Size(220, 130),
        _ => new Size(180, 120)
    };

    internal static BoardItemStyle ShapeToolStyle(string shapeType) =>
        IsLinearShapeTool(shapeType)
            ? new BoardItemStyle("#00FFFFFF", "#2E7DD7", "#172033", 2.2, CornerRadius: 0)
            : new BoardItemStyle("#EFF6FF", "#2E7DD7", "#172033", 1.3, CornerRadius: 6);

    internal static bool IsLinearShapeTool(string? shapeType) =>
        shapeType is "line" or "arrow" or "polyline";

    /// <summary>
    /// Remembers the style of the (single) selected shape so the next shape drawn in the same
    /// category starts from it — Excalidraw's "current item style" behavior. Called when a draw
    /// gesture begins, i.e. right before the draw clears the selection.
    /// </summary>
    internal void CaptureSelectedShapeStyle()
    {
        RenderBlock? only = null;
        foreach (var b in Scene.Blocks)
        {
            if (!b.IsSelected) continue;
            if (only is not null) return; // multi-selection: ambiguous, keep current memory
            only = b;
        }
        if (only is null || only.Kind != BlockKind.Shape || only.Style is null) return;

        if (IsFreedrawTool(only.ShapeType)) _lastFreedrawStyle = only.Style;
        else if (IsLinearShapeTool(only.ShapeType)) _lastLinearShapeStyle = only.Style;
        else _lastClosedShapeStyle = only.Style;
    }

    /// <summary>Style for a newly drawn shape: last-used style of the same category, else the default.</summary>
    internal BoardItemStyle EffectiveShapeStyle(string shapeType)
    {
        if (IsFreedrawTool(shapeType)) return _lastFreedrawStyle ?? FreedrawStyle();
        if (IsLinearShapeTool(shapeType)) return _lastLinearShapeStyle ?? ShapeToolStyle(shapeType);
        return _lastClosedShapeStyle ?? ShapeToolStyle(shapeType);
    }

    internal static bool IsFreedrawTool(string? shapeType) =>
        string.Equals(shapeType, "freedraw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default look for a freehand stroke: dark pen on a transparent fill.</summary>
    internal static BoardItemStyle FreedrawStyle() =>
        new BoardItemStyle("#00FFFFFF", "#1E1E1E", "#1E1E1E", 2.5, CornerRadius: 0);

    /// <summary>
    /// Builds a freehand Shape block from world-space stroke samples. Points are stored normalized
    /// to the stroke's bounding box in the shared "points:x,y;..." body format, so the stroke scales
    /// with the block and renders via <see cref="BlockRenderer"/>'s freedraw path.
    /// </summary>
    internal RenderBlock CreateFreedrawBlock(IReadOnlyList<WpfPoint> points)
    {
        Rect bounds = new(points[0], points[0]);
        foreach (var p in points.Skip(1)) bounds.Union(p);
        bounds.Inflate(6, 6);
        if (bounds.Width < 4) bounds = new Rect(bounds.X - 2, bounds.Y, 4, bounds.Height);
        if (bounds.Height < 4) bounds = new Rect(bounds.X, bounds.Y - 2, bounds.Width, 4);

        string body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, points);
        var id = Guid.NewGuid();
        return new RenderBlock(
            id, $"freedraw::{id:N}", BlockKind.Shape,
            string.Empty, string.Empty,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Body: body, ShapeType: "freedraw",
            Style: EffectiveShapeStyle("freedraw"));
    }

    internal RenderBlock CreateShapeBlock(string shapeType, WpfPoint start, WpfPoint current)
    {
        Rect bounds = BuildShapeDraftRect(shapeType, start, current);
        string? body = null;
        if (IsLinearShapeTool(shapeType))
            body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, new[] { start, current });

        var id = Guid.NewGuid();
        return new RenderBlock(
            id,
            $"{shapeType}::{id:N}",
            BlockKind.Shape,
            ShapeToolTitle(shapeType),
            string.Empty,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            Body: body,
            ShapeType: shapeType,
            Style: EffectiveShapeStyle(shapeType));
    }

    internal RenderBlock CreateLinearShapeFromVertices(
        string shapeType,
        IReadOnlyList<WpfPoint> vertices,
        string? attachStartKey,
        string? attachEndKey,
        WpfPoint? startOffset = null,
        WpfPoint? endOffset = null,
        IReadOnlyList<bool>? curvedFlags = null)
    {
        Rect bounds = new(vertices[0], vertices[0]);
        foreach (var v in vertices.Skip(1)) bounds.Union(v);
        bounds.Inflate(4, 4);
        if (bounds.Width < 24) bounds = new Rect(bounds.X - (24 - bounds.Width) / 2, bounds.Y, 24, bounds.Height);
        if (bounds.Height < 24) bounds = new Rect(bounds.X, bounds.Y - (24 - bounds.Height) / 2, bounds.Width, 24);

        string effectiveType = vertices.Count > 2 && (shapeType == "line" || shapeType == "arrow")
            ? (shapeType == "arrow" ? "arrow" : "polyline")
            : shapeType;

        string body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, vertices, attachStartKey, attachEndKey, startOffset, endOffset, curvedFlags);
        var id = Guid.NewGuid();
        return new RenderBlock(
            id, $"{effectiveType}::{id:N}", BlockKind.Shape,
            ShapeToolTitle(effectiveType), string.Empty,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Body: body, ShapeType: effectiveType,
            Style: EffectiveShapeStyle(effectiveType));
    }

    private string BuildLinearShapeBody(string shapeType, Rect bounds, (WpfPoint Start, WpfPoint End) endpoints) =>
        CanvasDrawingUtils.BuildLinearShapeBody(bounds, new[] { endpoints.Start, endpoints.End });

    private Rect BuildShapeDraftRect(string shapeType, WpfPoint start, WpfPoint current)
    {
        if (IsLinearShapeTool(shapeType))
        {
            var endpoints = GetLinearShapeDraft(start, current);
            return BuildLinearShapeBounds(endpoints.Start, endpoints.End);
        }

        bool forceSquare = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                           shapeType is "square" or "circle" or "star" or "hexagon";

        if (forceSquare)
        {
            double dx = current.X - start.X;
            double dy = current.Y - start.Y;
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            
            double endX = dx >= 0 ? start.X + side : start.X - side;
            double endY = dy >= 0 ? start.Y + side : start.Y - side;
            
            return new Rect(start, new WpfPoint(endX, endY));
        }
        else
        {
            Rect r = new(start, current);
            return new Rect(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
        }
    }

    private (WpfPoint Start, WpfPoint End) GetLinearShapeDraft(WpfPoint start, WpfPoint current)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double dx = current.X - start.X;
            double dy = current.Y - start.Y;
            if (Math.Abs(dx) > Math.Abs(dy)) current = new WpfPoint(current.X, start.Y);
            else current = new WpfPoint(start.X, current.Y);
        }
        return (start, current);
    }

    private Rect BuildLinearShapeBounds(WpfPoint start, WpfPoint end)
    {
        Rect r = new(start, end);
        r.Inflate(4, 4);
        return r;
    }

    private static Rect BuildLinearShapeBounds(IReadOnlyList<WpfPoint> vertices)
    {
        Rect bounds = new(vertices[0], vertices[0]);
        foreach (var v in vertices.Skip(1)) bounds.Union(v);
        bounds.Inflate(4, 4);
        if (bounds.Width < 24) bounds = new Rect(bounds.X - (24 - bounds.Width) / 2, bounds.Y, 24, bounds.Height);
        if (bounds.Height < 24) bounds = new Rect(bounds.X, bounds.Y - (24 - bounds.Height) / 2, bounds.Width, 24);
        return bounds;
    }

    private (string? StartKey, string? EndKey) ParseLinearShapeBody(string? body)
    {
        var parsed = CanvasDrawingUtils.ParseLinearShapeBody(body);
        return (parsed.StartKey, parsed.EndKey);
    }

    private Rect GetLinearShapeVisualBounds(RenderBlock block, Rect bounds, Dictionary<string, SceneBlockVisual> lookup)
    {
        var points = CanvasDrawingUtils.ResolveLinearShapePoints(block, bounds, lookup);
        if (points.Count == 0) return bounds;
        Rect r = new(points[0], points[0]);
        foreach (var p in points.Skip(1)) r.Union(p);
        if (r.Width < 24) r = new Rect(r.X - (24 - r.Width) / 2, r.Y, 24, r.Height);
        if (r.Height < 24) r = new Rect(r.X, r.Y - (24 - r.Height) / 2, r.Width, 24);
        return r;
    }

    internal SceneBlockVisual? HitBlock(WpfPoint world)
    {
        Dictionary<string, SceneBlockVisual>? lookup = null;
        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            // Rotated shapes are drawn rotated around their center but keep an axis-aligned
            // frame, so hit-test in the shape's local space by inverse-rotating the probe.
            WpfPoint probe = InverseRotateProbe(block, world);
            if (!block.Bounds.Contains(probe)) continue;

            // Excalidraw-style precision: lines, arrows, polylines and pencil strokes only hit
            // near their actual path — their (often huge) bounding box must not steal clicks
            // from whatever sits underneath.
            if (block.Block.Kind == BlockKind.Shape
                && (IsLinearShapeTool(block.Block.ShapeType) || IsFreedrawTool(block.Block.ShapeType)))
            {
                lookup ??= _snapshot.Blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);
                if (!HitsLinearShapePath(block, probe, lookup)) continue;
            }
            // Hollow (fully transparent) unlabeled shapes hit on their outline only, so clicks
            // through the empty interior reach blocks behind them (Excalidraw behavior).
            else if (IsHollowUnlabeledShape(block.Block) && !HitsShapeOutline(block, probe))
            {
                continue;
            }

            return block;
        }
        return null;
    }

    private static WpfPoint InverseRotateProbe(SceneBlockVisual block, WpfPoint world)
    {
        if (block.Block.Kind != BlockKind.Shape) return world;
        double rotation = (block.Block.Style?.Rotation ?? 0) % 360;
        if (Math.Abs(rotation) < 0.01) return world;
        WpfPoint center = CanvasDrawingUtils.CenterOf(block.Bounds);
        double rad = -rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = world.X - center.X, dy = world.Y - center.Y;
        return new WpfPoint(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    private double StrokeHitThreshold(BoardItemStyle? style)
    {
        double strokePx = Math.Clamp(style?.StrokeWidth ?? 2, 0.5, 8);
        return Math.Max(8.0, strokePx + 5.0) / Math.Max(0.08, _camera.Zoom);
    }

    private bool HitsLinearShapePath(SceneBlockVisual block, WpfPoint world, Dictionary<string, SceneBlockVisual> lookup)
    {
        var points = CanvasDrawingUtils.ResolveLinearShapePoints(block.Block, block.Bounds, lookup);
        if (points.Count < 2) return true;
        double threshold = StrokeHitThreshold(block.Block.Style);
        double thresholdSq = threshold * threshold;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            if (DistanceToSegmentSquared(world, points[i], points[i + 1]) <= thresholdSq)
                return true;
        }
        return false;
    }

    private static bool IsHollowUnlabeledShape(RenderBlock block)
    {
        if (block.Kind != BlockKind.Shape) return false;
        if (block.IsSelected) return false; // selected shapes stay easy to grab and drag
        var style = block.Style;
        if (style is null) return false;
        if (CanvasDrawingUtils.ParseColor(style.Fill).A != 0) return false;
        return string.IsNullOrWhiteSpace(block.Body) && string.IsNullOrWhiteSpace(block.Title);
    }

    /// <summary>Approximate outline-proximity test for hollow closed shapes: the click is a hit when
    /// it lies near the shape's outline point along the ray from the center through the click.</summary>
    private bool HitsShapeOutline(SceneBlockVisual block, WpfPoint world)
    {
        WpfPoint outline = CanvasDrawingUtils.GetBlockOutlinePoint(block.Block, block.Bounds, world);
        double threshold = StrokeHitThreshold(block.Block.Style);
        return CanvasDrawingUtils.DistanceSquared(world, outline) <= threshold * threshold;
    }

    private static double DistanceToSegmentSquared(WpfPoint p, WpfPoint a, WpfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return CanvasDrawingUtils.DistanceSquared(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        return CanvasDrawingUtils.DistanceSquared(p, new WpfPoint(a.X + t * dx, a.Y + t * dy));
    }

    internal LinearShapeVertexHit? HitLinearShapeEndpoint(WpfPoint world)
    {
        double radius = 9 / Math.Max(0.08, _camera.Zoom);
        double best = radius * radius;
        LinearShapeVertexHit? bestHit = null;
        var lookup = _snapshot.Blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (block.Block.Kind != BlockKind.Shape || !IsLinearShapeTool(block.Block.ShapeType)) continue;

            var points = CanvasDrawingUtils.ResolveLinearShapePoints(block.Block, block.Bounds, lookup);
            if (points.Count < 2) continue;

            TryEndpoint(0);
            TryEndpoint(points.Count - 1);

            void TryEndpoint(int index)
            {
                WpfPoint point = points[index];
                double distance = CanvasDrawingUtils.DistanceSquared(point, world);
                if (distance >= best) return;
                best = distance;
                bestHit = new LinearShapeVertexHit(block, index, point);
            }
        }

        return bestHit;
    }

    internal void MoveLinearShapeVertex(string key, int vertexIndex, WpfPoint world)
    {
        var block = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (block is null || block.IsLocked || block.Kind != BlockKind.Shape || !IsLinearShapeTool(block.ShapeType)) return;

        var lookup = _snapshot.Blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);
        var points = CanvasDrawingUtils.ResolveLinearShapePoints(block, new Rect(block.X, block.Y, block.Width, block.Height), lookup).ToList();
        if (points.Count < 2 || vertexIndex < 0 || vertexIndex >= points.Count) return;

        WpfPoint endpoint = SnapLinearShapeEndpoint(world, out string? movedAttachKey, out WpfPoint? movedOffset);
        points[vertexIndex] = endpoint;
        var parsed = CanvasDrawingUtils.ParseLinearShapeBody(block.Body);
        string? attachStart = vertexIndex == 0
            ? movedAttachKey
            : IsLinearShapeBlockKey(parsed.StartKey) ? null : parsed.StartKey;
        string? attachEnd = vertexIndex == points.Count - 1
            ? movedAttachKey
            : IsLinearShapeBlockKey(parsed.EndKey) ? null : parsed.EndKey;
        WpfPoint? startOffset = vertexIndex == 0
            ? movedOffset
            : attachStart is null ? null : parsed.StartOffset;
        WpfPoint? endOffset = vertexIndex == points.Count - 1
            ? movedOffset
            : attachEnd is null ? null : parsed.EndOffset;
        Rect bounds = BuildLinearShapeBounds(points);
        string body = CanvasDrawingUtils.BuildLinearShapeBody(bounds, points, attachStart, attachEnd, startOffset, endOffset, parsed.CurvedFlags);

        var blocks = Scene.Blocks
            .Select(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                ? b with { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height, Body = body, IsSelected = true }
                : b)
            .ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks }));
        RebuildSnapshot();
        RenderNative();
    }

    private bool IsLinearShapeBlockKey(string? key) =>
        key is not null && Scene.Blocks.Any(b =>
            b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            && b.Kind == BlockKind.Shape
            && IsLinearShapeTool(b.ShapeType));

    private WpfPoint SnapLinearShapeEndpoint(WpfPoint world, out string? attachKey, out WpfPoint? relativeOffset)
    {
        attachKey = null;
        relativeOffset = null;

        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (!block.Bounds.Contains(world)) continue;
            if (block.Block.Kind is BlockKind.Note) continue;
            if (block.Block.Kind == BlockKind.Shape && IsLinearShapeTool(block.Block.ShapeType)) continue;

            WpfPoint outlinePoint = CanvasDrawingUtils.GetBlockOutlinePoint(block.Block, block.Bounds, world);
            attachKey = block.Block.Key;
            relativeOffset = new WpfPoint(
                Math.Clamp((outlinePoint.X - block.Bounds.X) / Math.Max(1, block.Bounds.Width), 0, 1),
                Math.Clamp((outlinePoint.Y - block.Bounds.Y) / Math.Max(1, block.Bounds.Height), 0, 1));
            return outlinePoint;
        }

        return world;
    }

    internal SceneSwimLaneVisual? HitSwimLane(WpfPoint world)
    {
        foreach (var lane in _snapshot.SwimLanes.Reverse<SceneSwimLaneVisual>())
        {
            if (lane.Bounds.Contains(world)) return lane;
        }
        return null;
    }

    internal SceneSwimLaneVisual? HitSwimLaneResize(WpfPoint world)
    {
        foreach (var lane in _snapshot.SwimLanes.Reverse<SceneSwimLaneVisual>())
        {
            const double hs = 20;
            Rect handle = new(lane.Bounds.Right - hs, lane.Bounds.Bottom - hs, hs, hs);
            if (handle.Contains(world)) return lane;
        }
        return null;
    }

    internal static NoteResizeCorner HitNoteCorner(Rect bounds, WpfPoint world, double zoom = 1.0)
    {
        // The visible corner handle is centered on the block corner. Keep the
        // hitbox invisible but centered too, so clicking the visible outside half
        // of the handle still starts a resize.
        double hs = Math.Max(22.0, 34.0 / zoom);
        double half = hs / 2.0;
        if (new Rect(bounds.Left - half, bounds.Top - half, hs, hs).Contains(world)) return NoteResizeCorner.TopLeft;
        if (new Rect(bounds.Right - half, bounds.Top - half, hs, hs).Contains(world)) return NoteResizeCorner.TopRight;
        if (new Rect(bounds.Left - half, bounds.Bottom - half, hs, hs).Contains(world)) return NoteResizeCorner.BottomLeft;
        if (new Rect(bounds.Right - half, bounds.Bottom - half, hs, hs).Contains(world)) return NoteResizeCorner.BottomRight;
        return NoteResizeCorner.None;
    }

    internal BlockResizeCornerHit? HitSelectedBlockResizeCorner(WpfPoint world)
    {
        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (!block.Block.IsSelected || block.Block.IsLocked || IsLinearShapeTool(block.Block.ShapeType))
                continue;

            var corner = HitNoteCorner(block.Bounds, world, _camera.Zoom);
            if (corner != NoteResizeCorner.None)
                return new BlockResizeCornerHit(block, corner);
        }

        return null;
    }

    internal bool IsInResize(Rect bounds, WpfPoint world)
    {
        // Keep a minimum of 24 screen-pixels for the bottom-right resize corner.
        double hs = Math.Max(22.0, 24.0 / _camera.Zoom);
        if (bounds.Width < hs || bounds.Height < hs) return false;
        return new Rect(bounds.Right - hs, bounds.Bottom - hs, hs, hs).Contains(world)
            || IsInRightEdgeResize(bounds, world);
    }

    internal bool IsInRightEdgeResize(Rect bounds, WpfPoint world)
    {
        // Keep a minimum of 16 screen-pixels wide for the right-edge resize strip.
        double hs = Math.Max(12.0, 16.0 / _camera.Zoom);
        double edgeH = bounds.Height - HeaderH - FooterH;
        if (edgeH <= 0 || bounds.Width < hs) return false;
        return new Rect(bounds.Right - hs, bounds.Y + HeaderH, hs, edgeH).Contains(world);
    }

    internal static bool IsInRestoreButton(Rect bounds, WpfPoint world) =>
        CanvasDrawingUtils.GetRestoreButtonBounds(bounds).Contains(world);

    internal bool TryHitSymbolToken(SceneBlockVisual block, WpfPoint world, out SemanticTokenSpan token)
    {
        token = default!;
        if (block.Block.SemanticTokens is null || block.Block.SemanticTokens.Count == 0) return false;
        Rect bodyRect = CanvasDrawingUtils.GetBodyRect(block.Bounds);
        if (!bodyRect.Contains(world)) return false;
        double topPadding = block.Block.Focused is not null ? FocusedCodeTopPaddingLines * CodeLineH : 0;
        double relY = world.Y - bodyRect.Y - topPadding;
        if (relY < 0) return false;
        int lineIndex = (int)Math.Floor(relY / CodeLineH);
        int visibleLines = (int)Math.Floor(bodyRect.Height / CodeLineH);
        if (lineIndex < 0 || lineIndex >= visibleLines) return false;
        float codeX = (float)(bodyRect.X + CodeGutterW + CodeTextPadX);
        if (world.X < codeX) return false;
        int startLine = block.Block.Focused?.StartLine ?? block.Block.StartLine ?? 1;
        _codeScrollLines.TryGetValue(block.Block.Key, out int scrollLines);
        int sourceLine = startLine + scrollLines + lineIndex;
        double sourceColumn = ((world.X - codeX) / CodeCharW) + 1;
        var match = block.Block.SemanticTokens.Where(t => t.IsSymbolCandidate && t.Line == sourceLine).OrderBy(t => Math.Abs(sourceColumn - (t.Column + t.Length / 2.0))).FirstOrDefault(t => sourceColumn >= t.Column - 0.35 && sourceColumn <= t.Column + t.Length + 0.35);
        if (match is null) return false;
        token = match;
        return true;
    }

    /// <summary>True when <paramref name="world"/> is inside the line-number gutter of a code block
    /// (the strip left of the code text) — the hit zone that starts a reading-progress selection.</summary>
    internal bool IsInCodeGutter(SceneBlockVisual block, WpfPoint world)
    {
        if (block.Block.Kind is not (BlockKind.File or BlockKind.Extract)) return false;
        if (block.Block.FilePath is null) return false;
        Rect bodyRect = CanvasDrawingUtils.GetBodyRect(block.Bounds);
        if (!bodyRect.Contains(world)) return false;
        return world.X >= bodyRect.X && world.X <= bodyRect.X + CodeGutterW;
    }

    /// <summary>Maps a world point to the absolute (1-based) source line under it in a code block,
    /// using the same line layout as <c>DrawCodeBody</c> (focus padding + scroll offset). When
    /// <paramref name="clamp"/> is true the Y is clamped into the visible range (for drag tracking).</summary>
    internal bool TryResolveCodeLine(SceneBlockVisual block, WpfPoint world, bool clamp, out int srcLine)
    {
        srcLine = 0;
        if (block.Block.Kind is not (BlockKind.File or BlockKind.Extract)) return false;
        Rect bodyRect = CanvasDrawingUtils.GetBodyRect(block.Bounds);
        int topPaddingLines = block.Block.Focused is not null ? FocusedCodeTopPaddingLines : 0;
        double topPadding = topPaddingLines * CodeLineH;
        int visibleLines = (int)Math.Floor(bodyRect.Height / CodeLineH) - topPaddingLines;
        if (visibleLines <= 0) return false;

        double relY = world.Y - bodyRect.Y - topPadding;
        int lineIndex = (int)Math.Floor(relY / CodeLineH);
        if (clamp) lineIndex = Math.Clamp(lineIndex, 0, visibleLines - 1);
        else if (lineIndex < 0 || lineIndex >= visibleLines) return false;

        int startLine = block.Block.Focused?.StartLine ?? block.Block.StartLine ?? 1;
        _codeScrollLines.TryGetValue(block.Block.Key, out int scrollLines);
        srcLine = startLine + scrollLines + lineIndex;
        return true;
    }

    internal void UpdateHoverCursor(WpfPoint screen)
    {
        if (_isMinimapDrag) { Cursor = Cursors.SizeAll; return; }
        if (_isDrawingConnection) { Cursor = _connectionHoverTargetKey is not null ? Cursors.Hand : Cursors.Cross; return; }

        string? oldShapeHover = _hoverShapeTool;
        _hoverShapeTool = HitShapeTool(screen);
        if (_hoverShapeTool is not null)
        {
            Cursor = Cursors.Hand;
            if (oldShapeHover != _hoverShapeTool) RenderNative();
            return;
        }
        if (oldShapeHover is not null) RenderNative();
        if (_activeShapeTool is not null) { Cursor = Cursors.Cross; return; }

        WpfPoint world = ToWorld(screen);
        if (HitLinearShapeEndpoint(world) is not null) { Cursor = Cursors.SizeAll; return; }

        var anchor = HitConnectionAnchor(world);
        string? oldHoverKey = _hoverAnchorBlockKey;
        int? oldHoverIndex = _hoverAnchorIndex;
        _hoverAnchorBlockKey = anchor?.Block.Block.Key;
        _hoverAnchorIndex = anchor?.AnchorIndex;
        bool hoverChanged = oldHoverKey != _hoverAnchorBlockKey || oldHoverIndex != _hoverAnchorIndex;
        if (anchor is not null) { Cursor = Cursors.Cross; if (hoverChanged) RenderNative(); return; }
        if (hoverChanged) RenderNative();

        if (HitConnectionControlNode(world) is not null) { Cursor = Cursors.SizeAll; return; }
        if (HitConnectionArrow(world) is not null) { Cursor = Cursors.Hand; return; }
        if (HitConnectionCurve(world, out _) is not null) { Cursor = Cursors.Cross; return; }

        if (HitSelectedBlockResizeCorner(world) is { } resizeCornerHit)
        {
            Cursor = resizeCornerHit.Corner is NoteResizeCorner.TopLeft or NoteResizeCorner.BottomRight
                ? Cursors.SizeNWSE
                : Cursors.SizeNESW;
            return;
        }

        var hit = HitBlock(world);
        if (hit is not null)
        {
            if (IsInRestoreButton(hit.Bounds, world)) { Cursor = Cursors.Hand; return; }
            if (hit.Block.IsSelected && !hit.Block.IsLocked && !IsLinearShapeTool(hit.Block.ShapeType) && HitNoteCorner(hit.Bounds, world, _camera.Zoom) is var corner && corner != NoteResizeCorner.None) { Cursor = corner is NoteResizeCorner.TopLeft or NoteResizeCorner.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW; return; }
            if (hit.Block.IsSelected && !hit.Block.IsLocked && !IsLinearShapeTool(hit.Block.ShapeType) && IsInRightEdgeResize(hit.Bounds, world)) { Cursor = Cursors.SizeWE; return; }
            Cursor = Cursors.Arrow;
            return;
        }
        if (HitSwimLaneResize(world) is not null) { Cursor = Cursors.SizeNWSE; return; }
        Cursor = Cursors.Arrow;
    }

    internal static bool IsTextEditableBlock(RenderBlock block) =>
        block.Kind is BlockKind.Note or BlockKind.Text
        || (block.Kind == BlockKind.Shape && !IsLinearShapeTool(block.ShapeType) && !IsFreedrawTool(block.ShapeType))
        // Whole-page portals are editable in place — their outline body writes back to the
        // source page (see BeginNoteEdit / CommitNoteEdit → PagePortalEdited).
        || (block.Kind == BlockKind.Transclusion && !string.IsNullOrEmpty(block.RefPageName));

    internal static bool IsColorGroup(RenderBlock block) =>
        block.Kind == BlockKind.Container && string.Equals(block.ShapeType, "color-group", StringComparison.OrdinalIgnoreCase);
}
