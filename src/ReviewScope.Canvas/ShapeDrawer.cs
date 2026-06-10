using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using ReviewScope.Domain;
using RectangleF = System.Drawing.RectangleF;

namespace ReviewScope.Canvas;

/// <summary>
/// Abstraction over the two shape-rendering looks so <see cref="BlockRenderer"/> can draw a shape
/// without caring which style is active. <see cref="ShapeDrawers.Sketch"/> is the hand-drawn,
/// perturbed look (delegates to <see cref="SketchyDrawer"/>); <see cref="ShapeDrawers.Vector"/> is a
/// crisp, precise look built from native Direct2D primitives.
/// </summary>
internal interface IShapeDrawer
{
    void DrawRectangle(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity);
    void DrawRoundedRectangle(ID2D1RenderTarget rt, RectangleF rect, float radius, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity);
    void DrawEllipse(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity);
    void DrawPolygon(ID2D1RenderTarget rt, Vector2[] pts, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, bool close, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity);
}

/// <summary>Hand-drawn look — thin adapter over the existing <see cref="SketchyDrawer"/>.</summary>
internal sealed class SketchyShapeDrawer : IShapeDrawer
{
    public void DrawRectangle(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
        => SketchyDrawer.DrawRectangle(rt, rect, fillBrush, strokeBrush, strokeWidth, seedKey, strokeStyle, fillStyle, hatchOpacity);

    public void DrawRoundedRectangle(ID2D1RenderTarget rt, RectangleF rect, float radius, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
        => SketchyDrawer.DrawRoundedRectangle(rt, rect, radius, fillBrush, strokeBrush, strokeWidth, seedKey, strokeStyle, fillStyle, hatchOpacity);

    public void DrawEllipse(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
        => SketchyDrawer.DrawEllipse(rt, rect, fillBrush, strokeBrush, strokeWidth, seedKey, strokeStyle, fillStyle, hatchOpacity);

    public void DrawPolygon(ID2D1RenderTarget rt, Vector2[] pts, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, bool close, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
        => SketchyDrawer.DrawPolygon(rt, pts, fillBrush, strokeBrush, strokeWidth, seedKey, close, strokeStyle, fillStyle, hatchOpacity);
}

/// <summary>
/// Crisp "vector" look — solid fills and precise outlines drawn with native Direct2D primitives.
/// Hatched fills become evenly-spaced straight diagonal hachures (no hand-drawn jitter). The
/// <c>seedKey</c> argument is ignored: this style is deterministic by construction.
/// </summary>
internal sealed class VectorShapeDrawer : IShapeDrawer
{
    public void DrawRectangle(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
    {
        if (fillBrush is not null)
        {
            rt.FillRectangle(rect, fillBrush);
            if (fillStyle != "solid")
                DrawCleanHatching(rt, RectPoints(rect), strokeBrush, strokeWidth, hatchOpacity, fillStyle);
        }
        rt.DrawRectangle(rect, strokeBrush, strokeWidth, strokeStyle);
    }

    public void DrawRoundedRectangle(ID2D1RenderTarget rt, RectangleF rect, float radius, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
    {
        float r = Math.Min(radius, Math.Min(rect.Width / 2f, rect.Height / 2f));
        var rr = new RoundedRectangle(rect, r, r);
        if (fillBrush is not null)
        {
            rt.FillRoundedRectangle(rr, fillBrush);
            if (fillStyle != "solid")
                DrawCleanHatching(rt, RectPoints(rect), strokeBrush, strokeWidth, hatchOpacity, fillStyle);
        }
        rt.DrawRoundedRectangle(rr, strokeBrush, strokeWidth, strokeStyle);
    }

    public void DrawEllipse(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
    {
        var ellipse = new Ellipse(
            new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f),
            rect.Width / 2f,
            rect.Height / 2f);
        if (fillBrush is not null)
        {
            rt.FillEllipse(ellipse, fillBrush);
            if (fillStyle != "solid")
                DrawCleanHatching(rt, EllipsePoints(rect, 36), strokeBrush, strokeWidth, hatchOpacity, fillStyle);
        }
        rt.DrawEllipse(ellipse, strokeBrush, strokeWidth, strokeStyle);
    }

    public void DrawPolygon(ID2D1RenderTarget rt, Vector2[] pts, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, bool close, ID2D1StrokeStyle? strokeStyle, string fillStyle, float hatchOpacity)
    {
        if (pts.Length < 2) return;

        if (fillBrush is not null && pts.Length >= 3)
        {
            using var fillPath = rt.Factory.CreatePathGeometry();
            using (var sink = fillPath.Open())
            {
                sink.BeginFigure(pts[0], FigureBegin.Filled);
                foreach (var p in pts.Skip(1)) sink.AddLine(p);
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            rt.FillGeometry(fillPath, fillBrush);
            if (fillStyle != "solid")
                DrawCleanHatching(rt, pts, strokeBrush, strokeWidth, hatchOpacity, fillStyle);
        }

        // Crisp outline: stroke the polygon as a single open/closed geometry so corners join cleanly.
        using var outline = rt.Factory.CreatePathGeometry();
        using (var sink = outline.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Hollow);
            foreach (var p in pts.Skip(1)) sink.AddLine(p);
            sink.EndFigure(close ? FigureEnd.Closed : FigureEnd.Open);
            sink.Close();
        }
        rt.DrawGeometry(outline, strokeBrush, strokeWidth, strokeStyle);
    }

    private static Vector2[] RectPoints(RectangleF rect) => new[]
    {
        new Vector2(rect.X, rect.Y),
        new Vector2(rect.X + rect.Width, rect.Y),
        new Vector2(rect.X + rect.Width, rect.Y + rect.Height),
        new Vector2(rect.X, rect.Y + rect.Height)
    };

    private static Vector2[] EllipsePoints(RectangleF rect, int count)
    {
        var pts = new Vector2[count];
        float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;
        float rx = rect.Width / 2f, ry = rect.Height / 2f;
        for (int i = 0; i < count; i++)
        {
            float a = i * MathF.PI * 2f / count;
            pts[i] = new Vector2(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
        }
        return pts;
    }

    /// <summary>Evenly-spaced fill patterns clipped to the polygon (no jitter): hachure,
    /// cross-hatch, zigzag, or dots depending on <paramref name="fillStyle"/>.</summary>
    private static void DrawCleanHatching(ID2D1RenderTarget rt, Vector2[] polygon, ID2D1Brush brush, float strokeWidth, float hatchOpacity, string fillStyle = "hatch")
    {
        if (polygon.Length < 3) return;

        float prevOpacity = brush.Opacity;
        float effective = Math.Clamp(hatchOpacity, 0f, 1f);
        if (Math.Abs(prevOpacity - effective) > 0.001f) brush.Opacity = effective;
        try
        {
            switch (fillStyle.ToLowerInvariant())
            {
                case "cross-hatch":
                    DrawCleanHatchingPass(rt, polygon, brush, strokeWidth, antiDiagonal: false);
                    DrawCleanHatchingPass(rt, polygon, brush, strokeWidth, antiDiagonal: true);
                    break;
                case "zigzag":
                    DrawCleanZigzag(rt, polygon, brush, strokeWidth);
                    break;
                case "dots":
                    DrawCleanDots(rt, polygon, brush, strokeWidth);
                    break;
                default:
                    DrawCleanHatchingPass(rt, polygon, brush, strokeWidth, antiDiagonal: false);
                    break;
            }
        }
        finally
        {
            if (Math.Abs(prevOpacity - effective) > 0.001f) brush.Opacity = prevOpacity;
        }
    }

    private static void DrawCleanZigzag(ID2D1RenderTarget rt, Vector2[] polygon, ID2D1Brush brush, float strokeWidth)
    {
        float minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
        float minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
        if (maxX - minX < 6f || maxY - minY < 6f) return;

        const float spacing = 13f;
        float hatch = strokeWidth * 0.75f;
        Vector2? previousEnd = null;
        bool flip = false;
        for (float c = minX + minY + spacing; c < maxX + maxY; c += spacing)
        {
            var hits = new List<Vector2>();
            for (int i = 0; i < polygon.Length; i++)
            {
                var p1 = polygon[i];
                var p2 = polygon[(i + 1) % polygon.Length];
                float denom = (p2.X - p1.X) + (p2.Y - p1.Y);
                if (MathF.Abs(denom) <= 1e-4f) continue;
                float t = (c - (p1.X + p1.Y)) / denom;
                if (t >= 0f && t <= 1f) hits.Add(p1 + t * (p2 - p1));
            }
            if (hits.Count < 2) continue;
            hits.Sort((a, b) => a.X.CompareTo(b.X));
            Vector2 start = flip ? hits[1] : hits[0];
            Vector2 end = flip ? hits[0] : hits[1];
            if (previousEnd is Vector2 prev)
                rt.DrawLine(prev, start, brush, hatch);
            rt.DrawLine(start, end, brush, hatch);
            previousEnd = end;
            flip = !flip;
        }
    }

    private static void DrawCleanDots(ID2D1RenderTarget rt, Vector2[] polygon, ID2D1Brush brush, float strokeWidth)
    {
        float minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
        float minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
        if (maxX - minX < 6f || maxY - minY < 6f) return;

        const float spacing = 12f;
        float radius = Math.Max(0.8f, strokeWidth * 0.55f);
        for (float y = minY + spacing / 2; y < maxY; y += spacing)
            for (float x = minX + spacing / 2; x < maxX; x += spacing)
            {
                var p = new Vector2(x, y);
                if (SketchyDrawer.PointInPolygon(p, polygon))
                    rt.FillEllipse(new Ellipse(p, radius, radius), brush);
            }
    }

    private static void DrawCleanHatchingPass(ID2D1RenderTarget rt, Vector2[] polygon, ID2D1Brush brush, float strokeWidth, bool antiDiagonal)
    {
        float minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
        float minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
        if (maxX - minX < 6f || maxY - minY < 6f) return;

        const float spacing = 12f;
        float hatch = strokeWidth * 0.75f;
        // One diagonal family per pass: x + y = c, or x - y = c for the cross-hatch second pass.
        float startC = antiDiagonal ? minX - maxY : minX + minY;
        float endC = antiDiagonal ? maxX - minY : maxX + maxY;
        for (float c = startC + spacing; c < endC; c += spacing)
        {
            var hits = new List<Vector2>();
            for (int i = 0; i < polygon.Length; i++)
            {
                var p1 = polygon[i];
                var p2 = polygon[(i + 1) % polygon.Length];
                float denom = antiDiagonal
                    ? (p2.X - p1.X) - (p2.Y - p1.Y)
                    : (p2.X - p1.X) + (p2.Y - p1.Y);
                if (MathF.Abs(denom) <= 1e-4f) continue;
                float value = antiDiagonal ? p1.X - p1.Y : p1.X + p1.Y;
                float t = (c - value) / denom;
                if (t >= 0f && t <= 1f) hits.Add(p1 + t * (p2 - p1));
            }
            if (hits.Count < 2) continue;
            hits.Sort((a, b) => a.X.CompareTo(b.X));
            for (int k = 0; k + 1 < hits.Count; k += 2)
                rt.DrawLine(hits[k], hits[k + 1], brush, hatch);
        }
    }
}

/// <summary>Shared singletons for the two shape styles.</summary>
internal static class ShapeDrawers
{
    public static readonly IShapeDrawer Sketch = new SketchyShapeDrawer();
    public static readonly IShapeDrawer Vector = new VectorShapeDrawer();

    public static IShapeDrawer For(ShapeRenderStyle style) =>
        style == ShapeRenderStyle.Vector ? Vector : Sketch;
}
