using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vortice.Direct2D1;
using RectangleF = System.Drawing.RectangleF;

namespace ReviewScope.Canvas;

public static class SketchyDrawer
{
    private static void DrawSketchyLine(ID2D1RenderTarget rt, Random rand, Vector2 p1, Vector2 p2, ID2D1Brush brush, float strokeWidth, ID2D1StrokeStyle? strokeStyle)
    {
        float dx = p2.X - p1.X;
        float dy = p2.Y - p1.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;

        // Draw two overlapping slightly perturbed paths
        for (int pathIdx = 0; pathIdx < 2; pathIdx++)
        {
            // Random overshoot at both ends
            float overshootStart = (float)(rand.NextDouble() * 3.5 - 1.5);
            float overshootEnd = (float)(rand.NextDouble() * 3.5 - 1.5);

            float angle = MathF.Atan2(dy, dx);
            Vector2 start = p1 - new Vector2(MathF.Cos(angle) * overshootStart, MathF.Sin(angle) * overshootStart);
            Vector2 end = p2 + new Vector2(MathF.Cos(angle) * overshootEnd, MathF.Sin(angle) * overshootEnd);

            // Perpendicular vector for bow offset
            Vector2 perp = new Vector2(-MathF.Sin(angle), MathF.Cos(angle));

            // Intermediate midpoint with bow offset and small noise
            float bow = (float)((rand.NextDouble() - 0.5) * Math.Clamp(len * 0.02f, 1.0f, 3.5f));
            float midNoiseX = (float)((rand.NextDouble() - 0.5) * 1.2);
            float midNoiseY = (float)((rand.NextDouble() - 0.5) * 1.2);

            Vector2 mid = (start + end) * 0.5f + perp * bow + new Vector2(midNoiseX, midNoiseY);

            // Draw as two segments for that human imperfect stroke
            rt.DrawLine(start, mid, brush, strokeWidth, strokeStyle);
            rt.DrawLine(mid, end, brush, strokeWidth, strokeStyle);
        }
    }

    private static void DrawSketchyLineSingle(ID2D1RenderTarget rt, Random rand, Vector2 p1, Vector2 p2, ID2D1Brush brush, float strokeWidth)
    {
        float dx = p2.X - p1.X;
        float dy = p2.Y - p1.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;

        float angle = MathF.Atan2(dy, dx);
        Vector2 perp = new Vector2(-MathF.Sin(angle), MathF.Cos(angle));

        float bow = (float)((rand.NextDouble() - 0.5) * Math.Clamp(len * 0.015f, 0.8f, 2.8f));
        Vector2 mid = (p1 + p2) * 0.5f + perp * bow;

        rt.DrawLine(p1, mid, brush, strokeWidth);
        rt.DrawLine(mid, p2, brush, strokeWidth);
    }

    public static void DrawLine(ID2D1RenderTarget rt, Vector2 start, Vector2 end, ID2D1Brush brush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle = null)
    {
        var rand = new Random(seedKey.GetHashCode());
        DrawSketchyLine(rt, rand, start, end, brush, strokeWidth, strokeStyle);
    }

    public static void DrawRectangle(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle = null, string fillStyle = "hatch")
    {
        var rand = new Random(seedKey.GetHashCode());
        Vector2[] pts =
        {
            new(rect.X, rect.Y),
            new(rect.X + rect.Width, rect.Y),
            new(rect.X + rect.Width, rect.Y + rect.Height),
            new(rect.X, rect.Y + rect.Height)
        };

        if (fillBrush is not null)
        {
            rt.FillRectangle(rect, fillBrush);
            if (fillStyle != "solid")
                DrawSketchyHatching(rt, rand, pts, strokeBrush, strokeWidth);
        }

        // Draw boundaries
        for (int i = 0; i < 4; i++)
        {
            DrawSketchyLine(rt, rand, pts[i], pts[(i + 1) % 4], strokeBrush, strokeWidth, strokeStyle);
        }
    }

    public static void DrawPolygon(ID2D1RenderTarget rt, Vector2[] pts, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, bool close = true, ID2D1StrokeStyle? strokeStyle = null, string fillStyle = "hatch")
    {
        if (pts.Length < 2) return;
        var rand = new Random(seedKey.GetHashCode());

        if (fillBrush is not null && pts.Length >= 3)
        {
            // Direct2D solid polygon fill using a path geometry
            using var path = rt.Factory.CreatePathGeometry();
            using var sink = path.Open();
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            foreach (var p in pts.Skip(1)) sink.AddLine(p);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
            rt.FillGeometry(path, fillBrush);

            if (fillStyle != "solid")
                DrawSketchyHatching(rt, rand, pts, strokeBrush, strokeWidth);
        }

        int count = close ? pts.Length : pts.Length - 1;
        for (int i = 0; i < count; i++)
        {
            DrawSketchyLine(rt, rand, pts[i], pts[(i + 1) % pts.Length], strokeBrush, strokeWidth, strokeStyle);
        }
    }

    public static void DrawEllipse(ID2D1RenderTarget rt, RectangleF rect, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle = null, string fillStyle = "hatch")
    {
        var rand = new Random(seedKey.GetHashCode());
        var pts = BuildEllipsePointsArray(rect, 24);

        if (fillBrush is not null)
        {
            var ellipse = new Ellipse(
                new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f),
                rect.Width / 2f,
                rect.Height / 2f);
            rt.FillEllipse(ellipse, fillBrush);
            if (fillStyle != "solid")
                DrawSketchyHatching(rt, rand, pts, strokeBrush, strokeWidth);
        }

        // Approximate ellipse drawing using two full sketchy overlapping loops
        for (int loop = 0; loop < 2; loop++)
        {
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float rx = rect.Width / 2f;
            float ry = rect.Height / 2f;

            int segmentCount = 20;
            Vector2 lastPt = Vector2.Zero;

            for (int i = 0; i <= segmentCount; i++)
            {
                float angle = i * MathF.PI * 2f / segmentCount;
                
                // Add radial noise
                float radNoiseX = (float)((rand.NextDouble() - 0.5) * Math.Clamp(rect.Width * 0.02f, 1.0f, 3.5f));
                float radNoiseY = (float)((rand.NextDouble() - 0.5) * Math.Clamp(rect.Height * 0.02f, 1.0f, 3.5f));

                float px = cx + (rx + radNoiseX) * MathF.Cos(angle);
                float py = cy + (ry + radNoiseY) * MathF.Sin(angle);
                Vector2 currentPt = new(px, py);

                if (i > 0)
                {
                    // Draw continuous sketchy segments with single stroke for organic flow
                    DrawSketchyLineSingle(rt, rand, lastPt, currentPt, strokeBrush, strokeWidth);
                }
                lastPt = currentPt;
            }
        }
    }

    private static Vector2[] BuildEllipsePointsArray(RectangleF rect, int count)
    {
        Vector2[] pts = new Vector2[count];
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        float rx = rect.Width / 2f;
        float ry = rect.Height / 2f;

        for (int i = 0; i < count; i++)
        {
            float angle = i * MathF.PI * 2f / count;
            pts[i] = new Vector2(cx + rx * MathF.Cos(angle), cy + ry * MathF.Sin(angle));
        }
        return pts;
    }

    private static void DrawSketchyHatching(ID2D1RenderTarget rt, Random rand, Vector2[] polygonPoints, ID2D1Brush brush, float strokeWidth)
    {
        if (polygonPoints.Length < 3) return;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in polygonPoints)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        float w = maxX - minX;
        float h = maxY - minY;
        if (w < 6f || h < 6f) return;

        // Space out hatching lines (typically 12px, slightly randomized)
        float spacing = 12f;

        // Diagonal lines: x + y = c
        float startC = minX + minY;
        float endC = maxX + maxY;

        for (float c = startC + spacing; c < endC; c += spacing + (float)(rand.NextDouble() * 3.0 - 1.5))
        {
            List<Vector2> intersections = new();
            for (int i = 0; i < polygonPoints.Length; i++)
            {
                var p1 = polygonPoints[i];
                var p2 = polygonPoints[(i + 1) % polygonPoints.Length];

                float denom = (p2.X - p1.X) + (p2.Y - p1.Y);
                if (MathF.Abs(denom) > 1e-4f)
                {
                    float t = (c - (p1.X + p1.Y)) / denom;
                    if (t >= 0f && t <= 1f)
                    {
                        intersections.Add(p1 + t * (p2 - p1));
                    }
                }
            }

            if (intersections.Count >= 2)
            {
                var sorted = intersections.OrderBy(pt => pt.X).ToList();
                for (int k = 0; k < sorted.Count - 1; k += 2)
                {
                    // Lighter hachures using thinner single stroke
                    DrawSketchyLineSingle(rt, rand, sorted[k], sorted[k + 1], brush, strokeWidth * 0.75f);
                }
            }
        }
    }

    public static Vector2[] BuildRoundedRectPoints(RectangleF rect, float radius, int pointsPerCorner = 6)
    {
        if (radius <= 0)
        {
            return new Vector2[]
            {
                new(rect.X, rect.Y),
                new(rect.X + rect.Width, rect.Y),
                new(rect.X + rect.Width, rect.Y + rect.Height),
                new(rect.X, rect.Y + rect.Height)
            };
        }

        float w = rect.Width;
        float h = rect.Height;
        float x = rect.X;
        float y = rect.Y;

        // Make sure radius is not larger than half of width or height
        radius = Math.Min(radius, Math.Min(w / 2f, h / 2f));

        var pts = new List<Vector2>();

        // Top-Right corner (from angle -PI/2 to 0)
        for (int i = 0; i <= pointsPerCorner; i++)
        {
            float angle = -MathF.PI / 2f + (MathF.PI / 2f) * ((float)i / pointsPerCorner);
            pts.Add(new Vector2(x + w - radius + radius * MathF.Cos(angle), y + radius + radius * MathF.Sin(angle)));
        }

        // Bottom-Right corner (from angle 0 to PI/2)
        for (int i = 0; i <= pointsPerCorner; i++)
        {
            float angle = (MathF.PI / 2f) * ((float)i / pointsPerCorner);
            pts.Add(new Vector2(x + w - radius + radius * MathF.Cos(angle), y + h - radius + radius * MathF.Sin(angle)));
        }

        // Bottom-Left corner (from angle PI/2 to PI)
        for (int i = 0; i <= pointsPerCorner; i++)
        {
            float angle = MathF.PI / 2f + (MathF.PI / 2f) * ((float)i / pointsPerCorner);
            pts.Add(new Vector2(x + radius + radius * MathF.Cos(angle), y + h - radius + radius * MathF.Sin(angle)));
        }

        // Top-Left corner (from angle PI to 3*PI/2)
        for (int i = 0; i <= pointsPerCorner; i++)
        {
            float angle = MathF.PI + (MathF.PI / 2f) * ((float)i / pointsPerCorner);
            pts.Add(new Vector2(x + radius + radius * MathF.Cos(angle), y + radius + radius * MathF.Sin(angle)));
        }

        return pts.ToArray();
    }

    public static void DrawRoundedRectangle(ID2D1RenderTarget rt, RectangleF rect, float radius, ID2D1Brush? fillBrush, ID2D1Brush strokeBrush, float strokeWidth, string seedKey, ID2D1StrokeStyle? strokeStyle = null, string fillStyle = "hatch")
    {
        var pts = BuildRoundedRectPoints(rect, radius);
        DrawPolygon(rt, pts, fillBrush, strokeBrush, strokeWidth, seedKey, close: true, strokeStyle: strokeStyle, fillStyle: fillStyle);
    }
}

