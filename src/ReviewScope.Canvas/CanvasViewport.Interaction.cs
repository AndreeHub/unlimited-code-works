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
            Style: ShapeToolStyle(shapeType));
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
            Style: ShapeToolStyle(effectiveType));
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
        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (block.Bounds.Contains(world)) return block;
        }
        return null;
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
        // Keep a minimum of 20 screen-pixels so handles are always easy to hit at any zoom level.
        double hs = Math.Max(14.0, 20.0 / zoom);
        if (new Rect(bounds.Left, bounds.Top, hs, hs).Contains(world)) return NoteResizeCorner.TopLeft;
        if (new Rect(bounds.Right - hs, bounds.Top, hs, hs).Contains(world)) return NoteResizeCorner.TopRight;
        if (new Rect(bounds.Left, bounds.Bottom - hs, hs, hs).Contains(world)) return NoteResizeCorner.BottomLeft;
        if (new Rect(bounds.Right - hs, bounds.Bottom - hs, hs, hs).Contains(world)) return NoteResizeCorner.BottomRight;
        return NoteResizeCorner.None;
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
        || (block.Kind == BlockKind.Shape && !IsLinearShapeTool(block.ShapeType));

    internal static bool IsColorGroup(RenderBlock block) =>
        block.Kind == BlockKind.Container && string.Equals(block.ShapeType, "color-group", StringComparison.OrdinalIgnoreCase);
}
