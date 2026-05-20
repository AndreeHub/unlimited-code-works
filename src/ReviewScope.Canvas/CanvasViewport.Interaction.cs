using ReviewScope.Domain;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using IOPath = System.IO.Path;
using FactoryType = Vortice.DirectWrite.FactoryType;
using DWriteFontWeight = Vortice.DirectWrite.FontWeight;
using DWriteFontStyle = Vortice.DirectWrite.FontStyle;
using DWriteFontStretch = Vortice.DirectWrite.FontStretch;
using DWriteTextAlignment = Vortice.DirectWrite.TextAlignment;
using D2DBezierSegment = Vortice.Direct2D1.BezierSegment;
using WpfColor = System.Windows.Media.Color;
using RectangleF = System.Drawing.RectangleF;
using Color4 = Vortice.Mathematics.Color4;

namespace ReviewScope.Canvas;

public sealed partial class CanvasViewport
{
    private sealed record LinearShapeDraft(Point Start, Point End, string? StartKey, string? EndKey);
    private sealed record LinearShapeBody(IReadOnlyList<Point> RelativePoints, string? StartKey, string? EndKey);

    private static readonly string[] ShapeToolIds =
    {
        "rectangle",
        "square",
        "circle",
        "oval",
        "triangle",
        "diamond",
        "star",
        "hexagon",
        "line",
        "arrow",
        "polyline"
    };

    private string? HitShapeTool(Point screen)
    {
        var buttons = GetShapeToolButtonBounds(new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        foreach (var (tool, bounds) in buttons)
        {
            if (bounds.Contains((float)screen.X, (float)screen.Y))
                return tool;
        }

        return null;
    }

    private IReadOnlyList<(string Tool, RectangleF Bounds)> GetShapeToolButtonBounds(Size viewSize)
    {
        const int columns = 4;
        var buttons = new List<(string Tool, RectangleF Bounds)>(ShapeToolIds.Length);
        float rows = (float)Math.Ceiling(ShapeToolIds.Length / (double)columns);
        float paletteH = ShapeToolPalettePadding * 2 + rows * ShapeToolButtonSize + (rows - 1) * ShapeToolButtonGap;
        float x = ShapeToolPaletteMargin;
        float y = (float)viewSize.Height - paletteH - ShapeToolPaletteMargin;

        for (int i = 0; i < ShapeToolIds.Length; i++)
        {
            int col = i % columns;
            int row = i / columns;
            buttons.Add((
                ShapeToolIds[i],
                new RectangleF(
                    x + ShapeToolPalettePadding + col * (ShapeToolButtonSize + ShapeToolButtonGap),
                    y + ShapeToolPalettePadding + row * (ShapeToolButtonSize + ShapeToolButtonGap),
                    ShapeToolButtonSize,
                    ShapeToolButtonSize)));
        }

        return buttons;
    }

    private static string ShapeToolTitle(string shapeType) =>
        string.Join(" ", shapeType.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static Size ShapeToolDefaultSize(string shapeType) => shapeType switch
    {
        "square" or "circle" or "star" or "hexagon" => new Size(150, 150),
        "triangle" or "diamond" => new Size(170, 145),
        "line" or "arrow" or "polyline" => new Size(220, 80),
        "oval" or "rectangle" => new Size(220, 130),
        _ => new Size(180, 120)
    };

    private static BoardItemStyle ShapeToolStyle(string shapeType) =>
        IsLinearShapeTool(shapeType)
            ? new BoardItemStyle("#00FFFFFF", "#2E7DD7", "#172033", 2.2, CornerRadius: 0)
            : new BoardItemStyle("#FFFFFF", "#2E7DD7", "#172033", 1.6, CornerRadius: 3);

    private static bool ShapeKeepsSquareBounds(string shapeType) =>
        shapeType is "square" or "circle" or "star" or "hexagon";

    private static bool IsLinearShapeTool(string? shapeType) =>
        shapeType is "line" or "arrow" or "polyline";

    private static bool IsTextEditableBlock(RenderBlock block) =>
        block.Kind is BlockKind.Note or BlockKind.Text
        || (block.Kind == BlockKind.Shape && !IsLinearShapeTool(block.ShapeType));

    private static Rect BuildShapeDraftRect(string shapeType, Point start, Point current)
    {
        double dx = current.X - start.X;
        double dy = current.Y - start.Y;

        if (IsLinearShapeTool(shapeType))
        {
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
            {
                Size size = ShapeToolDefaultSize(shapeType);
                current = new Point(start.X + size.Width, start.Y);
            }

            return BuildLinearShapeBounds(start, current);
        }

        if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
        {
            Size size = ShapeToolDefaultSize(shapeType);
            return new Rect(start.X, start.Y, size.Width, size.Height);
        }

        if (ShapeKeepsSquareBounds(shapeType))
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            current = new Point(
                start.X + Math.Sign(dx == 0 ? 1 : dx) * side,
                start.Y + Math.Sign(dy == 0 ? 1 : dy) * side);
        }

        var rect = new Rect(start, current);
        return new Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height));
    }

    private RenderBlock CreateShapeBlock(string shapeType, Point start, Point current)
    {
        Rect bounds = BuildShapeDraftRect(shapeType, start, current);
        string? body = null;
        if (IsLinearShapeTool(shapeType))
        {
            var endpoints = GetLinearShapeDraft(start, current);
            bounds = BuildLinearShapeBounds(endpoints.Start, endpoints.End);
            body = BuildLinearShapeBody(shapeType, bounds, endpoints);
        }

        var id = Guid.NewGuid();
        string title = ShapeToolTitle(shapeType);
        int z = Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Max(b => b.ZIndex) + 1;
        return new RenderBlock(
            id,
            $"shape::{id:N}",
            BlockKind.Shape,
            title,
            string.Empty,
            bounds.X,
            bounds.Y,
            Math.Max(24, bounds.Width),
            Math.Max(24, bounds.Height),
            IsSelected: true,
            Body: body ?? title,
            ZIndex: z,
            LayerKey: "layer::architecture",
            ShapeType: shapeType,
            Style: ShapeToolStyle(shapeType));
    }

    private static Rect BuildLinearShapeBounds(Point start, Point end)
    {
        var bounds = new Rect(start, end);
        double x = bounds.X;
        double y = bounds.Y;
        double w = Math.Max(1, bounds.Width);
        double h = Math.Max(1, bounds.Height);
        const double min = 24;
        if (w < min)
        {
            x = (start.X + end.X) / 2 - min / 2;
            w = min;
        }
        if (h < min)
        {
            y = (start.Y + end.Y) / 2 - min / 2;
            h = min;
        }

        return new Rect(x, y, w, h);
    }

    private LinearShapeDraft GetLinearShapeDraft(Point start, Point current)
    {
        if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4)
        {
            Size size = ShapeToolDefaultSize(_activeShapeTool ?? "line");
            current = new Point(start.X + size.Width, start.Y);
        }

        Point snappedStart = start;
        Point snappedEnd = current;
        string? startKey = null;
        string? endKey = null;
        var startBlock = HitBlock(start);
        var endBlock = HitBlock(current);
        if (startBlock is not null && IsContourSnapBlock(startBlock.Block))
        {
            snappedStart = GetBlockOutlinePoint(startBlock.Block, startBlock.Bounds, current);
            startKey = startBlock.Block.Key;
        }
        if (endBlock is not null && IsContourSnapBlock(endBlock.Block))
        {
            snappedEnd = GetBlockOutlinePoint(endBlock.Block, endBlock.Bounds, start);
            endKey = endBlock.Block.Key;
        }

        return new LinearShapeDraft(snappedStart, snappedEnd, startKey, endKey);
    }

    private static bool IsContourSnapBlock(RenderBlock? block) =>
        block?.Kind == BlockKind.Shape && !IsLinearShapeTool(block.ShapeType);

    private static string BuildLinearShapeBody(string shapeType, Rect bounds, LinearShapeDraft draft)
    {
        var points = shapeType == "polyline"
            ? new[]
            {
                NormalizePoint(bounds, draft.Start),
                NormalizePoint(bounds, new Point(draft.End.X, draft.Start.Y)),
                NormalizePoint(bounds, draft.End)
            }
            : new[] { NormalizePoint(bounds, draft.Start), NormalizePoint(bounds, draft.End) };

        string body = "points:" + string.Join(";", points.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.X:0.####},{p.Y:0.####}")));
        if (draft.StartKey is not null || draft.EndKey is not null)
            body += $"|attach:{draft.StartKey ?? string.Empty},{draft.EndKey ?? string.Empty}";
        return body;
    }

    private static Point NormalizePoint(Rect bounds, Point point) =>
        new(
            (point.X - bounds.X) / Math.Max(1, bounds.Width),
            (point.Y - bounds.Y) / Math.Max(1, bounds.Height));

    private static LinearShapeBody ParseLinearShapeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new LinearShapeBody(Array.Empty<Point>(), null, null);

        string pointsText = body;
        string? startKey = null;
        string? endKey = null;
        foreach (string section in body.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (section.StartsWith("points:", StringComparison.OrdinalIgnoreCase))
                pointsText = section["points:".Length..];
            else if (section.StartsWith("attach:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = section["attach:".Length..].Split(',', 2);
                startKey = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : null;
                endKey = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
            }
        }

        if (pointsText.StartsWith("points:", StringComparison.OrdinalIgnoreCase))
            pointsText = pointsText["points:".Length..];

        var points = new List<Point>();
        foreach (string pair in pointsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rx)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ry))
                continue;

            points.Add(new Point(rx, ry));
        }

        return new LinearShapeBody(points, startKey, endKey);
    }

    private static IReadOnlyList<Point> ResolveLinearShapePoints(
        RenderBlock block,
        Rect outer,
        IReadOnlyDictionary<string, SceneBlockVisual>? blockLookup = null)
    {
        var body = ParseLinearShapeBody(block.Body);
        var points = body.RelativePoints.Count >= 2
            ? body.RelativePoints.Select(p => new Point(
                outer.X + Math.Clamp(p.X, 0, 1) * outer.Width,
                outer.Y + Math.Clamp(p.Y, 0, 1) * outer.Height)).ToList()
            : new List<Point> { new(outer.Left, outer.Top), new(outer.Right, outer.Bottom) };

        if (blockLookup is null || points.Count < 2)
            return points;

        SceneBlockVisual? startBlock = body.StartKey is null
            ? null
            : blockLookup.GetValueOrDefault(body.StartKey);
        SceneBlockVisual? endBlock = body.EndKey is null
            ? null
            : blockLookup.GetValueOrDefault(body.EndKey);

        if (startBlock is null && endBlock is null)
            return points;

        Point startToward = endBlock is not null ? CenterOf(endBlock.Bounds) : points[^1];
        Point endToward = startBlock is not null ? CenterOf(startBlock.Bounds) : points[0];
        if (startBlock is not null)
            points[0] = GetBlockOutlinePoint(startBlock.Block, startBlock.Bounds, startToward);
        if (endBlock is not null)
            points[^1] = GetBlockOutlinePoint(endBlock.Block, endBlock.Bounds, endToward);

        if (block.ShapeType == "polyline" && points.Count > 2)
        {
            if (startBlock is not null || endBlock is not null)
            {
                var start = points[0];
                var end = points[^1];
                points = new List<Point> { start, new(end.X, start.Y), end };
            }
        }

        return points;
    }

    private static Rect GetLinearShapeVisualBounds(RenderBlock block, Rect fallback, IReadOnlyDictionary<string, SceneBlockVisual> blockLookup)
    {
        var points = ResolveLinearShapePoints(block, fallback, blockLookup);
        if (points.Count == 0) return fallback;

        Rect bounds = new(points[0], points[0]);
        foreach (Point point in points.Skip(1))
            bounds.Union(point);

        if (bounds.Width < 24)
            bounds = new Rect(bounds.X - (24 - bounds.Width) / 2, bounds.Y, 24, bounds.Height);
        if (bounds.Height < 24)
            bounds = new Rect(bounds.X, bounds.Y - (24 - bounds.Height) / 2, bounds.Width, 24);
        return bounds;
    }

    private bool IsDoubleClick(string key, Point screen)
    {
        long now = Environment.TickCount64;
        bool sameKey = string.Equals(_lastClickKey, key, StringComparison.OrdinalIgnoreCase);
        bool sameTime = _lastClickTick >= 0 && (now - _lastClickTick) <= GetDoubleClickTime();
        bool samePlace = !double.IsNaN(_lastClickScreen.X)
            && Math.Abs(screen.X - _lastClickScreen.X) <= 5
            && Math.Abs(screen.Y - _lastClickScreen.Y) <= 5;
        TrackClick(key, screen);
        return sameKey && sameTime && samePlace;
    }

    private void TrackClick(string key, Point screen)
    {
        _lastClickKey = key;
        _lastClickScreen = screen;
        _lastClickTick = Environment.TickCount64;
    }

    // -----------------------------------------------------------------------
    // Interaction helpers
    // -----------------------------------------------------------------------
    private void ResetInteraction()
    {
        _panPoint = null;
        _dragWorldPoint = null;
        _dragStartScreen = null;
        _primaryDrag = null;
        _draggedKeys = new();
        _resizeKey = null;
        _resizeWorldPoint = null;
        _resizeWidthOnly = false;
        _resizeSwimLaneKey = null;
        _resizeSwimLaneWorldPoint = null;
        _dragArrowConnectionId = null;
        _dragConnectionControlId = null;
        _dragConnectionControlKind = ConnectionControlNodeKind.None;
        _marqueeStart = null;
        _marqueeEnd = null;
        _isMarquee = false;
        _appendMarquee = false;
        _didMove = false;
        _isMinimapDrag = false;
    }

    private void UpdateHoverCursor(Point screen)
    {
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

        Point world = ToWorld(screen);
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
        if (hit is not null && hit.Block.Focused is not null && IsInRestoreButton(hit.Bounds, world))
        { Cursor = Cursors.Hand; return; }
        if (hit is not null && hit.Block.Kind != BlockKind.Note && IsInResize(hit.Bounds, world))
        { Cursor = IsInRightEdgeResize(hit.Bounds, world) ? Cursors.SizeWE : Cursors.SizeNWSE; return; }
        if (HitSwimLaneResize(world) is not null) { Cursor = Cursors.SizeNWSE; return; }
        Cursor = Cursors.Arrow;
    }
}

