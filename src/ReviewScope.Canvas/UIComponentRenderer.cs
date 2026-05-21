using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

internal sealed class UIComponentRenderer
{
    private readonly DrawingContext _ctx;
    
    private const double MinimapW = 180;
    private const double MinimapH = 135;
    private const double MinimapMargin = 16;
    private const float ShapeToolPaletteMargin = 16;
    private const float ShapeToolPalettePadding = 12;
    private const float ShapeToolButtonSize = 32;
    private const float ShapeToolButtonGap = 6;

    public UIComponentRenderer(DrawingContext ctx)
    {
        _ctx = ctx;
    }

    public void DrawMinimap(Size viewSize, SceneSnapshot snapshot, Func<Point, Point> toWorld)
    {
        if (snapshot.WorldBounds.IsEmpty) return;
        float mx = (float)(viewSize.Width - MinimapW - MinimapMargin);
        float my = (float)(viewSize.Height - MinimapH - MinimapMargin);
        float mw = (float)MinimapW, mh = (float)MinimapH;

        _ctx.RenderTarget.FillRectangle(new RectangleF(mx, my, mw, mh), _ctx.GetBrush(WpfColor.FromArgb(235, 255, 255, 255)));
        _ctx.RenderTarget.DrawRectangle(new RectangleF(mx, my, mw, mh), _ctx.GetBrush(WpfColor.FromArgb(220, 226, 232, 240)), 1f);

        Rect wb = snapshot.WorldBounds;
        double scaleX = MinimapW / wb.Width, scaleY = MinimapH / wb.Height;
        double scale = Math.Min(scaleX, scaleY) * 0.9;
        double offX = mx + (MinimapW - wb.Width * scale) / 2 - wb.X * scale;
        double offY = my + (MinimapH - wb.Height * scale) / 2 - wb.Y * scale;

        foreach (var b in snapshot.Blocks)
        {
            float bx = (float)(b.Bounds.X * scale + offX);
            float by = (float)(b.Bounds.Y * scale + offY);
            float bw = (float)Math.Max(4, b.Bounds.Width * scale);
            float bh = (float)Math.Max(3, b.Bounds.Height * scale);
            WpfColor c = b.Block.Kind == BlockKind.Note ? WpfColor.FromRgb(226, 186, 76) : WpfColor.FromRgb(46, 125, 215);
            _ctx.RenderTarget.FillRectangle(new RectangleF(bx, by, bw, bh), _ctx.GetBrush(WpfColor.FromArgb(140, c.R, c.G, c.B)));
        }

        // Viewport indicator
        Point wvTL = toWorld(new Point(0, 0));
        Point wvBR = toWorld(new Point(viewSize.Width, viewSize.Height));
        float vx = (float)(wvTL.X * scale + offX);
        float vy = (float)(wvTL.Y * scale + offY);
        float vw = (float)((wvBR.X - wvTL.X) * scale);
        float vh = (float)((wvBR.Y - wvTL.Y) * scale);
        _ctx.RenderTarget.DrawRectangle(new RectangleF(vx, vy, Math.Max(2, vw), Math.Max(2, vh)),
            _ctx.GetBrush(WpfColor.FromArgb(210, 46, 125, 215)), 1f);
    }

    public void DrawMarquee(bool isMarquee, Point? marqueeStart, Point? marqueeEnd)
    {
        if (!isMarquee || marqueeStart is null || marqueeEnd is null) return;
        Rect r = new(marqueeStart.Value, marqueeEnd.Value);
        _ctx.RenderTarget.FillRectangle(CanvasDrawingUtils.ToRF(r), _ctx.GetBrush(WpfColor.FromArgb(36, 46, 125, 215)));
        _ctx.RenderTarget.DrawRectangle(CanvasDrawingUtils.ToRF(r), _ctx.GetBrush(WpfColor.FromArgb(150, 46, 125, 215)), 1f);
    }

    public void DrawShapeToolPalette(Size viewSize, string[] shapeToolIds, string? activeShapeTool, string? hoverShapeTool)
    {
        var tools = shapeToolIds;
        float columns = 4;
        float rows = (float)Math.Ceiling(tools.Length / columns);
        float w = ShapeToolPalettePadding * 2 + columns * ShapeToolButtonSize + (columns - 1) * ShapeToolButtonGap;
        float h = ShapeToolPalettePadding * 2 + rows * ShapeToolButtonSize + (rows - 1) * ShapeToolButtonGap;
        float x = ShapeToolPaletteMargin;
        float y = (float)viewSize.Height - h - ShapeToolPaletteMargin;
        var bounds = new RectangleF(x, y, w, h);

        _ctx.RenderTarget.FillRoundedRectangle(new RoundedRectangle(bounds, 6, 6), _ctx.GetBrush(WpfColor.FromArgb(242, 255, 255, 255)));
        _ctx.RenderTarget.DrawRoundedRectangle(new RoundedRectangle(bounds, 6, 6), _ctx.GetBrush(WpfColor.FromArgb(230, 211, 218, 228)), 1f);

        for (int i = 0; i < tools.Length; i++)
        {
            string tool = tools[i];
            int col = i % (int)columns;
            int row = i / (int)columns;
            var button = new RectangleF(
                x + ShapeToolPalettePadding + col * (ShapeToolButtonSize + ShapeToolButtonGap),
                y + ShapeToolPalettePadding + row * (ShapeToolButtonSize + ShapeToolButtonGap),
                ShapeToolButtonSize,
                ShapeToolButtonSize);

            bool active = string.Equals(activeShapeTool, tool, StringComparison.OrdinalIgnoreCase);
            bool hover = string.Equals(hoverShapeTool, tool, StringComparison.OrdinalIgnoreCase);
            WpfColor fill = active
                ? WpfColor.FromRgb(234, 243, 255)
                : hover ? WpfColor.FromRgb(244, 247, 251) : WpfColor.FromRgb(255, 255, 255);
            WpfColor stroke = active
                ? WpfColor.FromRgb(46, 125, 215)
                : WpfColor.FromRgb(211, 218, 228);

            _ctx.RenderTarget.FillRoundedRectangle(new RoundedRectangle(button, 4, 4), _ctx.GetBrush(fill));
            _ctx.RenderTarget.DrawRoundedRectangle(new RoundedRectangle(button, 4, 4), _ctx.GetBrush(stroke), active ? 1.4f : 1f);
            DrawShapeToolIcon(tool, button, active ? WpfColor.FromRgb(46, 125, 215) : WpfColor.FromRgb(96, 111, 130));
        }
    }

    private void DrawShapeToolIcon(string shape, RectangleF button, WpfColor color)
    {
        float pad = 8f;
        var r = new RectangleF(button.X + pad, button.Y + pad, button.Width - pad * 2, button.Height - pad * 2);
        var brush = _ctx.GetBrush(color);

        if (shape is "line" or "arrow")
        {
            var a = new Vector2(r.X, r.Bottom);
            var b = new Vector2(r.Right, r.Y);
            _ctx.RenderTarget.DrawLine(a, b, brush, 1.5f);
            if (shape == "arrow") DrawArrowhead(new Point(b.X, b.Y), new Point(a.X, a.Y), brush, 1.5f);
        }
        else if (shape is "polyline")
        {
            var pts = new[]
            {
                new Vector2(r.X, r.Bottom),
                new Vector2(r.X + r.Width * 0.55f, r.Bottom),
                new Vector2(r.X + r.Width * 0.55f, r.Y),
                new Vector2(r.Right, r.Y)
            };
            DrawPolyline(pts, brush, 1.5f, close: false);
        }
        else if (shape is "rectangle")
            _ctx.RenderTarget.DrawRectangle(new RectangleF(r.X, r.Y + r.Height * 0.18f, r.Width, r.Height * 0.64f), brush, 1.5f);
        else if (shape is "square")
            _ctx.RenderTarget.DrawRectangle(r, brush, 1.5f);
        else if (shape is "circle" or "oval")
        {
            var eRect = shape == "circle" ? r : new RectangleF(r.X, r.Y + r.Height * 0.18f, r.Width, r.Height * 0.64f);
            _ctx.RenderTarget.DrawEllipse(new Ellipse(new Vector2(eRect.X + eRect.Width / 2, eRect.Y + eRect.Height / 2), eRect.Width / 2, eRect.Height / 2), brush, 1.5f);
        }
        else
        {
            Rect iconBounds = new(r.X, r.Y, r.Width, r.Height);
            IReadOnlyList<Vector2> pts = shape switch
            {
                "triangle" => new[]
                {
                    new Vector2(r.X + r.Width / 2, r.Y),
                    new Vector2(r.Right, r.Bottom),
                    new Vector2(r.X, r.Bottom)
                },
                "diamond" => new[]
                {
                    new Vector2(r.X + r.Width / 2, r.Y),
                    new Vector2(r.Right, r.Y + r.Height / 2),
                    new Vector2(r.X + r.Width / 2, r.Bottom),
                    new Vector2(r.X, r.Y + r.Height / 2)
                },
                "star" => BuildStarPoints(iconBounds),
                "hexagon" => BuildRegularPolygonPoints(iconBounds, 6, -MathF.PI / 6),
                _ => BuildRegularPolygonPoints(iconBounds, 4, MathF.PI / 4)
            };
            DrawPolyline(pts, brush, 1.5f, close: true);
        }
    }

    private void DrawPolyline(IReadOnlyList<Vector2> points, Vortice.Direct2D1.ID2D1SolidColorBrush brush, float stroke, bool close)
    {
        if (points.Count < 2) return;
        for (int i = 1; i < points.Count; i++)
            _ctx.RenderTarget.DrawLine(points[i - 1], points[i], brush, stroke);
        if (close)
            _ctx.RenderTarget.DrawLine(points[^1], points[0], brush, stroke);
    }

    private void DrawArrowhead(Point tip, Point from, Vortice.Direct2D1.ID2D1SolidColorBrush brush, float stroke)
    {
        Vector2 dir = new((float)(tip.X - from.X), (float)(tip.Y - from.Y));
        float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.01f) return;
        dir /= len;
        Vector2 perp = new(-dir.Y, dir.X);
        float aLen = Math.Max(8f, stroke * 5);
        Vector2 tipV = new((float)tip.X, (float)tip.Y);
        Vector2 left = tipV - dir * aLen + perp * (aLen * 0.45f);
        Vector2 right = tipV - dir * aLen - perp * (aLen * 0.45f);
        using var path = _ctx.Factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(tipV, Vortice.Direct2D1.FigureBegin.Filled);
        sink.AddLine(left);
        sink.AddLine(right);
        sink.EndFigure(Vortice.Direct2D1.FigureEnd.Closed);
        sink.Close();
        _ctx.RenderTarget.FillGeometry(path, brush);
    }

    private static IReadOnlyList<Vector2> BuildRegularPolygonPoints(Rect bounds, int sides, float rotation)
    {
        var points = new Vector2[Math.Max(3, sides)];
        float cx = (float)(bounds.X + bounds.Width / 2);
        float cy = (float)(bounds.Y + bounds.Height / 2);
        float rx = (float)bounds.Width / 2;
        float ry = (float)bounds.Height / 2;
        for (int i = 0; i < points.Length; i++)
        {
            float angle = rotation + MathF.PI * 2 * i / points.Length;
            points[i] = new Vector2(cx + MathF.Cos(angle) * rx, cy + MathF.Sin(angle) * ry);
        }
        return points;
    }

    private static IReadOnlyList<Vector2> BuildStarPoints(Rect bounds)
    {
        const int pointCount = 10;
        var points = new Vector2[pointCount];
        float cx = (float)(bounds.X + bounds.Width / 2);
        float cy = (float)(bounds.Y + bounds.Height / 2);
        float outerRx = (float)bounds.Width / 2;
        float outerRy = (float)bounds.Height / 2;
        float innerRx = outerRx * 0.46f;
        float innerRy = outerRy * 0.46f;
        for (int i = 0; i < pointCount; i++)
        {
            bool outer = i % 2 == 0;
            float angle = -MathF.PI / 2 + MathF.PI * 2 * i / pointCount;
            float rx = outer ? outerRx : innerRx;
            float ry = outer ? outerRy : innerRy;
            points[i] = new Vector2(cx + MathF.Cos(angle) * rx, cy + MathF.Sin(angle) * ry);
        }
        return points;
    }
}
