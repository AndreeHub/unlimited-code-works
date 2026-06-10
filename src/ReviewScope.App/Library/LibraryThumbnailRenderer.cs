using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReviewScope.Domain;

namespace ReviewScope.App.Library;

/// <summary>
/// Renders a small static preview of a <see cref="LibraryItemModel"/> using WPF drawing primitives
/// (not the Direct2D canvas — avoids offscreen-RT plumbing and airspace issues). Shapes are scaled
/// to fit a square thumbnail with padding. The look is intentionally "vector/clean" — thumbnails
/// favor legibility over the hand-drawn jitter of the live canvas.
/// </summary>
public static class LibraryThumbnailRenderer
{
    public static ImageSource Render(LibraryItemModel item, int px = 96)
    {
        const double pad = 6;
        double inner = px - 2 * pad;
        double scale = inner / Math.Max(item.Width, item.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;

        // Center the scaled item within the thumbnail.
        double drawW = item.Width * scale, drawH = item.Height * scale;
        double ox = (px - drawW) / 2, oy = (px - drawH) / 2;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var shape in item.Shapes)
                DrawShape(dc, shape, scale, ox, oy);
        }

        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawShape(DrawingContext dc, LibraryShape s, double scale, double ox, double oy)
    {
        double x = ox + s.X * scale, y = oy + s.Y * scale;
        double w = s.Width * scale, h = s.Height * scale;
        var style = s.Style ?? new BoardItemStyle();

        bool rotated = Math.Abs(style.Rotation % 360) > 0.01;
        if (rotated)
        {
            dc.PushTransform(new RotateTransform(style.Rotation, x + w / 2, y + h / 2));
            try { DrawShapeCore(dc, s, style, x, y, w, h, scale); }
            finally { dc.Pop(); }
            return;
        }
        DrawShapeCore(dc, s, style, x, y, w, h, scale);
    }

    private static void DrawShapeCore(DrawingContext dc, LibraryShape s, BoardItemStyle style, double x, double y, double w, double h, double scale)
    {
        // Image entries: draw the decoded bitmap stretched over the shape rect.
        if (!string.IsNullOrEmpty(s.AssetPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 128;
                bmp.UriSource = new Uri(s.AssetPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                dc.DrawImage(bmp, new Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
            }
            catch
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.LightGray, 1), new Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
            }
            return;
        }


        Brush? fill = ToBrush(style.Fill);
        Color strokeColor = ToColor(style.Stroke, Colors.Black);
        double thickness = Math.Clamp(style.StrokeWidth, 1, 3);
        var pen = new Pen(new SolidColorBrush(strokeColor), thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        switch (s.ShapeType)
        {
            case "oval":
            case "circle":
            case "database":
            case "cache":
            case "queue":
                dc.DrawEllipse(fill, pen, new Point(x + w / 2, y + h / 2), w / 2, h / 2);
                break;
            case "diamond":
            case "risk":
            case "decision":
                dc.DrawGeometry(fill, pen, Poly(new[]
                {
                    new Point(x + w / 2, y), new Point(x + w, y + h / 2),
                    new Point(x + w / 2, y + h), new Point(x, y + h / 2)
                }, true));
                break;
            case "triangle":
                dc.DrawGeometry(fill, pen, Poly(new[]
                {
                    new Point(x + w / 2, y), new Point(x + w, y + h), new Point(x, y + h)
                }, true));
                break;
            case "hexagon":
                dc.DrawGeometry(fill, pen, Poly(RegularPolygon(x, y, w, h, 6, -Math.PI / 6), true));
                break;
            case "star":
                dc.DrawGeometry(fill, pen, Poly(StarPoints(x, y, w, h), true));
                break;
            case "line":
            case "arrow":
            case "polyline":
            case "freedraw":
            {
                var pts = ParsePoints(s.Body, x, y, w, h);
                if (pts.Count >= 2) dc.DrawGeometry(null, pen, Poly(pts, false));
                break;
            }
            default: // rectangle / square / unknown
            {
                double r = Math.Min(style.CornerRadius * scale, Math.Min(w, h) / 2);
                dc.DrawRoundedRectangle(fill, pen, new Rect(x, y, Math.Max(1, w), Math.Max(1, h)), r, r);
                break;
            }
        }

        DrawLabel(dc, s, style, x, y, w, h, scale);
    }

    /// <summary>Draws a shape's text label (or a standalone text element's body) centered in its rect.</summary>
    private static void DrawLabel(DrawingContext dc, LibraryShape s, BoardItemStyle style, double x, double y, double w, double h, double scale)
    {
        string? body = s.Body;
        if (string.IsNullOrWhiteSpace(body)) return;
        if (body.StartsWith("points:", StringComparison.OrdinalIgnoreCase)) return; // linear-shape geometry, not text

        double fontSize = Math.Clamp((style.FontSize <= 0 ? 14 : style.FontSize) * scale, 3, 24);
        var text = new FormattedText(
            body, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), fontSize,
            new SolidColorBrush(ToColor(style.Text, Colors.Black)), 1.25)
        {
            MaxTextWidth = Math.Max(4, w),
            MaxTextHeight = Math.Max(4, h),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(x, y + Math.Max(0, (h - text.Height) / 2)));
    }

    private static List<Point> RegularPolygon(double x, double y, double w, double h, int sides, double rotation)
    {
        var pts = new List<Point>(sides);
        double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
        for (int i = 0; i < sides; i++)
        {
            double a = rotation + Math.PI * 2 * i / sides;
            pts.Add(new Point(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
        }
        return pts;
    }

    private static List<Point> StarPoints(double x, double y, double w, double h)
    {
        const int count = 10;
        var pts = new List<Point>(count);
        double cx = x + w / 2, cy = y + h / 2;
        for (int i = 0; i < count; i++)
        {
            bool outer = i % 2 == 0;
            double a = -Math.PI / 2 + Math.PI * 2 * i / count;
            double rx = (outer ? 1 : 0.46) * w / 2;
            double ry = (outer ? 1 : 0.46) * h / 2;
            pts.Add(new Point(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
        }
        return pts;
    }

    private static StreamGeometry Poly(IReadOnlyList<Point> pts, bool closed)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: closed, isClosed: closed);
            ctx.PolyLineTo(pts.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Parses the shared "points:rx,ry;..." body (rx,ry normalized to the shape's bounds).</summary>
    private static List<Point> ParsePoints(string? body, double x, double y, double w, double h)
    {
        var result = new List<Point>();
        if (string.IsNullOrEmpty(body)) return result;
        string text = body;
        int bar = text.IndexOf('|');
        if (bar >= 0) text = text[..bar];
        const string prefix = "points:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rx) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ry))
                result.Add(new Point(x + Math.Clamp(rx, 0, 1) * w, y + Math.Clamp(ry, 0, 1) * h));
        }
        return result;
    }

    private static Brush? ToBrush(string? hex)
    {
        var c = ToColor(hex, Colors.Transparent);
        if (c.A == 0) return null;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color ToColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            if (hex.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Colors.Transparent;
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { return fallback; }
    }
}
