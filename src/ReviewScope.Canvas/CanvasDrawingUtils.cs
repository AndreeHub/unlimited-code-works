using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: CanvasDrawingUtils.cs
 * Purpose: Provides shared geometric, color, and layout utilities for the ReviewScope canvas.
 * Functions:
 * - Geometric calculations (Distance, CenterOf, Outline points)
 * - Connection path and interpolation logic (Bézier, Polyline)
 * - UI element bounds calculation (Restore button, code gutters)
 * - Color parsing
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal static class CanvasDrawingUtils
{
    public const double HeaderH = 56;
    public const double FooterH = 28;
    public const double CodeGutterW = 54;
    public const double CodeTextPadX = 12;
    public const double CodeScrollbarReserveW = 34;
    public const double CodeLineH = 18;
    public const double CodeCharW = 7.2;
    public const int FocusedCodeTopPaddingLines = 2;
    public const double ConnectionLeadDistance = 32;
    public const double MinAutoTangent = 45;
    public const double MaxAutoTangent = 280;

    public static RectangleF ToRF(Rect r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    public static WpfColor ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith('#')) hex = hex[1..];
            uint val = Convert.ToUInt32(hex, 16);
            return hex.Length == 6
                ? WpfColor.FromRgb((byte)(val >> 16), (byte)(val >> 8), (byte)val)
                : WpfColor.FromArgb((byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val);
        }
        catch { return WpfColor.FromRgb(100, 140, 200); }
    }

    public static Rect CenteredSquare(Rect bounds)
    {
        double side = Math.Min(bounds.Width, bounds.Height);
        return new Rect(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2, side, side);
    }

    public static WpfPoint CenterOf(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public static Rect GetRestoreButtonBounds(Rect blockBounds)
    {
        const double sz = 22;
        const double margin = 12;
        return new Rect(blockBounds.Right - sz - margin, blockBounds.Y + 12, sz, sz);
    }

    public static bool IsInRestoreButton(Rect blockBounds, WpfPoint world) =>
        GetRestoreButtonBounds(blockBounds).Contains(world);

    public static Rect GetBodyRect(Rect outer) =>
        new(outer.X + 1, outer.Y + HeaderH, outer.Width - 2, Math.Max(0, outer.Height - HeaderH - FooterH));

    public static Vector2 GetConnectionAnchorNormal(int anchorIndex) =>
        Math.Clamp(anchorIndex, 0, 15) switch
        {
            <= 3 => new Vector2(0, -1),
            <= 7 => new Vector2(1, 0),
            <= 11 => new Vector2(0, 1),
            _ => new Vector2(-1, 0)
        };

    public static WpfPoint GetConnectionAnchorPoint(Rect bounds, int anchorIndex)
    {
        int index = Math.Clamp(anchorIndex, 0, 15);
        int side = index / 4; // 0: top, 1: right, 2: bottom, 3: left
        int pos = index % 4;
        double frac = (pos + 1) / 5.0;

        return side switch
        {
            0 => new WpfPoint(bounds.Left + bounds.Width * frac, bounds.Top),
            1 => new WpfPoint(bounds.Right, bounds.Top + bounds.Height * frac),
            2 => new WpfPoint(bounds.Right - bounds.Width * frac, bounds.Bottom),
            _ => new WpfPoint(bounds.Left, bounds.Bottom - bounds.Height * frac)
        };
    }

    public static WpfPoint GetConnectionAnchorPoint(SceneBlockVisual block, int anchorIndex) =>
        GetBlockOutlinePoint(block.Block, block.Bounds, GetConnectionAnchorPoint(block.Bounds, anchorIndex));

    public static WpfPoint GetBlockOutlinePoint(RenderBlock block, Rect bounds, WpfPoint toward)
    {
        string? shape = block.ShapeType;
        WpfPoint center = CenterOf(bounds);
        double dx = toward.X - center.X;
        double dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
            return toward;

        if (shape is "square")
        {
            bounds = CenteredSquare(bounds);
            center = CenterOf(bounds);
            dx = toward.X - center.X;
            dy = toward.Y - center.Y;
        }

        if (shape is "circle" or "oval")
        {
            Rect ellipse = shape == "circle" ? CenteredSquare(bounds) : bounds;
            WpfPoint ellipseCenter = CenterOf(ellipse);
            dx = toward.X - ellipseCenter.X;
            dy = toward.Y - ellipseCenter.Y;
            double rx = Math.Max(1, ellipse.Width / 2);
            double ry = Math.Max(1, ellipse.Height / 2);
            double scale = 1 / Math.Sqrt((dx * dx) / (rx * rx) + (dy * dy) / (ry * ry));
            return new WpfPoint(ellipseCenter.X + dx * scale, ellipseCenter.Y + dy * scale);
        }

        if (shape is "risk" or "decision" or "diamond")
        {
            double halfW = Math.Max(1, bounds.Width / 2);
            double halfH = Math.Max(1, bounds.Height / 2);
            double scale = 1 / ((Math.Abs(dx) / halfW) + (Math.Abs(dy) / halfH));
            return new WpfPoint(center.X + dx * scale, center.Y + dy * scale);
        }

        if (shape is "triangle")
        {
            WpfPoint v1 = new(bounds.X + bounds.Width / 2f, bounds.Y);
            WpfPoint v2 = new(bounds.Right, bounds.Bottom);
            WpfPoint v3 = new(bounds.Left, bounds.Bottom);

            if (RaySegmentIntersection(center, toward, v1, v2, out var inter) ||
                RaySegmentIntersection(center, toward, v2, v3, out inter) ||
                RaySegmentIntersection(center, toward, v3, v1, out inter))
            {
                return inter;
            }
        }

        return GetRectOutlinePoint(bounds, toward);
    }

    private static bool RaySegmentIntersection(WpfPoint rayStart, WpfPoint rayEnd, WpfPoint segStart, WpfPoint segEnd, out WpfPoint intersection)
    {
        intersection = new WpfPoint();
        double r_px = rayStart.X;
        double r_py = rayStart.Y;
        double r_dx = rayEnd.X - rayStart.X;
        double r_dy = rayEnd.Y - rayStart.Y;

        double s_px = segStart.X;
        double s_py = segStart.Y;
        double s_dx = segEnd.X - segStart.X;
        double s_dy = segEnd.Y - segStart.Y;

        double denom = r_dx * s_dy - r_dy * s_dx;
        if (Math.Abs(denom) < 0.0001) return false;

        double t = ((s_px - r_px) * s_dy - (s_py - r_py) * s_dx) / denom;
        double u = ((s_px - r_px) * r_dy - (s_py - r_py) * r_dx) / denom;

        if (t >= 0 && u >= 0 && u <= 1)
        {
            intersection = new WpfPoint(r_px + t * r_dx, r_py + t * r_dy);
            return true;
        }
        return false;
    }

    private static WpfPoint GetRectOutlinePoint(Rect bounds, WpfPoint toward)
    {
        WpfPoint center = CenterOf(bounds);
        double dx = toward.X - center.X;
        double dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
            return center;

        double sx = Math.Abs(dx) < 0.001 ? double.PositiveInfinity : (bounds.Width / 2) / Math.Abs(dx);
        double sy = Math.Abs(dy) < 0.001 ? double.PositiveInfinity : (bounds.Height / 2) / Math.Abs(dy);
        double scale = Math.Min(sx, sy);
        return new WpfPoint(center.X + dx * scale, center.Y + dy * scale);
    }

    public static void GetConnectionPathPoints(SceneConnectionVisual connVis, out WpfPoint startLead, out WpfPoint middlePoint, out WpfPoint endLead)
    {
        int sourceAnchor = connVis.Connection.SourceAnchorIndex ?? 4;
        int targetAnchor = connVis.Connection.TargetAnchorIndex ?? 10;
        Vector2 sourceNormal = GetConnectionAnchorNormal(sourceAnchor);
        Vector2 targetNormal = GetConnectionAnchorNormal(targetAnchor);

        startLead = new WpfPoint(
            connVis.Start.X + sourceNormal.X * ConnectionLeadDistance,
            connVis.Start.Y + sourceNormal.Y * ConnectionLeadDistance);
        endLead = new WpfPoint(
            connVis.End.X + targetNormal.X * ConnectionLeadDistance,
            connVis.End.Y + targetNormal.Y * ConnectionLeadDistance);

        if (connVis.Connection.MidControlX is double mx && connVis.Connection.MidControlY is double my)
        {
            middlePoint = new WpfPoint(mx, my);
            return;
        }

        if (connVis.Connection.SourceControlX is double sx && connVis.Connection.SourceControlY is double sy)
        {
            middlePoint = new WpfPoint(sx, sy);
            return;
        }

        if (connVis.Connection.TargetControlX is double tx && connVis.Connection.TargetControlY is double ty)
        {
            middlePoint = new WpfPoint(tx, ty);
            return;
        }

        middlePoint = new WpfPoint((startLead.X + endLead.X) / 2, (startLead.Y + endLead.Y) / 2);
    }

    public static WpfPoint EvaluateConnectionPoint(SceneConnectionVisual connVis, double t)
    {
        if (connVis.Connection.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
            return EvaluatePolylinePoint(BuildConnectionPolyline(connVis), t);

        GetConnectionPathPoints(connVis, out WpfPoint startLead, out WpfPoint mid, out WpfPoint endLead);
        t = Math.Clamp(t, 0, 1);

        if (t <= 0.12)
        {
            double lt = t / 0.12;
            return Lerp(connVis.Start, startLead, lt);
        }

        if (t >= 0.88)
        {
            double lt = (t - 0.88) / 0.12;
            return Lerp(endLead, connVis.End, lt);
        }

        double bt = (t - 0.12) / 0.76;
        double u = 1 - bt;
        if (HasCustomConnectionMidPoint(connVis.Connection) && connVis.Connection.MidControlBends)
        {
            WpfPoint control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            double qx = u * u * startLead.X + 2 * u * bt * control.X + bt * bt * endLead.X;
            double qy = u * u * startLead.Y + 2 * u * bt * control.Y + bt * bt * endLead.Y;
            return new WpfPoint(qx, qy);
        }

        GetAutoCubicControls(connVis, startLead, endLead, out WpfPoint c1, out WpfPoint c2);
        double x = u * u * u * startLead.X
                 + 3 * u * u * bt * c1.X
                 + 3 * u * bt * bt * c2.X
                 + bt * bt * bt * endLead.X;
        double y = u * u * u * startLead.Y
                 + 3 * u * u * bt * c1.Y
                 + 3 * u * bt * bt * c2.Y
                 + bt * bt * bt * endLead.Y;
        return new WpfPoint(x, y);
    }

    public static Vector2 EvaluateConnectionTangent(SceneConnectionVisual connVis, double t)
    {
        if (connVis.Connection.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
            return EvaluatePolylineTangent(BuildConnectionPolyline(connVis), t);

        GetConnectionPathPoints(connVis, out WpfPoint startLead, out WpfPoint mid, out WpfPoint endLead);
        t = Math.Clamp(t, 0, 1);

        if (t <= 0.12)
            return Normalize(connVis.Start, startLead);
        if (t >= 0.88)
            return Normalize(endLead, connVis.End);

        double bt = (t - 0.12) / 0.76;
        double x;
        double y;
        if (HasCustomConnectionMidPoint(connVis.Connection) && connVis.Connection.MidControlBends)
        {
            WpfPoint control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            x = 2 * (1 - bt) * (control.X - startLead.X) + 2 * bt * (endLead.X - control.X);
            y = 2 * (1 - bt) * (control.Y - startLead.Y) + 2 * bt * (endLead.Y - control.Y);
        }
        else
        {
            GetAutoCubicControls(connVis, startLead, endLead, out WpfPoint c1, out WpfPoint c2);
            x = 3 * (1 - bt) * (1 - bt) * (c1.X - startLead.X)
              + 6 * (1 - bt) * bt * (c2.X - c1.X)
              + 3 * bt * bt * (endLead.X - c2.X);
            y = 3 * (1 - bt) * (1 - bt) * (c1.Y - startLead.Y)
              + 6 * (1 - bt) * bt * (c2.Y - c1.Y)
              + 3 * bt * bt * (endLead.Y - c2.Y);
        }
        var tangent = new Vector2((float)x, (float)y);
        float len = tangent.Length();
        return len > 0.001f ? tangent / len : Vector2.UnitX;
    }

    public static WpfPoint Lerp(WpfPoint a, WpfPoint b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static IReadOnlyList<WpfPoint> BuildConnectionPolyline(SceneConnectionVisual connVis)
    {
        if (connVis.Connection.RouteKind == ConnectorRouteKind.Straight)
            return new[] { connVis.Start, connVis.End };

        GetConnectionPathPoints(connVis, out WpfPoint startLead, out WpfPoint middle, out WpfPoint endLead);
        var points = new List<WpfPoint> { connVis.Start, startLead };

        if (HasCustomConnectionMidPoint(connVis.Connection))
        {
            points.Add(new WpfPoint(middle.X, startLead.Y));
            points.Add(middle);
            points.Add(new WpfPoint(middle.X, endLead.Y));
        }
        else
        {
            bool horizontalFirst = Math.Abs(endLead.X - startLead.X) >= Math.Abs(endLead.Y - startLead.Y);
            if (horizontalFirst)
            {
                double midX = (startLead.X + endLead.X) / 2;
                points.Add(new WpfPoint(midX, startLead.Y));
                points.Add(new WpfPoint(midX, endLead.Y));
            }
            else
            {
                double midY = (startLead.Y + endLead.Y) / 2;
                points.Add(new WpfPoint(startLead.X, midY));
                points.Add(new WpfPoint(endLead.X, midY));
            }
        }

        points.Add(endLead);
        points.Add(connVis.End);
        return RemoveDuplicatePoints(points);
    }

    private static IReadOnlyList<WpfPoint> RemoveDuplicatePoints(IReadOnlyList<WpfPoint> points)
    {
        var compact = new List<WpfPoint>(points.Count);
        foreach (var point in points)
        {
            if (compact.Count == 0 || DistanceSquared(compact[^1], point) > 0.01)
                compact.Add(point);
        }
        return compact;
    }

    private static WpfPoint EvaluatePolylinePoint(IReadOnlyList<WpfPoint> points, double t)
    {
        if (points.Count == 0) return new WpfPoint();
        if (points.Count == 1) return points[0];
        double total = PolylineLength(points);
        if (total <= 0.001) return points[^1];
        double target = Math.Clamp(t, 0, 1) * total;
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double segment = Distance(points[i - 1], points[i]);
            if (walked + segment >= target)
                return Lerp(points[i - 1], points[i], segment <= 0.001 ? 1 : (target - walked) / segment);
            walked += segment;
        }
        return points[^1];
    }

    private static Vector2 EvaluatePolylineTangent(IReadOnlyList<WpfPoint> points, double t)
    {
        if (points.Count < 2) return Vector2.UnitX;
        double total = PolylineLength(points);
        double target = Math.Clamp(t, 0, 1) * Math.Max(0.001, total);
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double segment = Distance(points[i - 1], points[i]);
            if (walked + segment >= target)
                return Normalize(points[i - 1], points[i]);
            walked += segment;
        }
        return Normalize(points[^2], points[^1]);
    }

    private static double PolylineLength(IReadOnlyList<WpfPoint> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
            total += Distance(points[i - 1], points[i]);
        return total;
    }

    public static double Distance(WpfPoint a, WpfPoint b) => Math.Sqrt(DistanceSquared(a, b));

    public static double DistanceSquared(WpfPoint a, WpfPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static WpfPoint GetQuadraticControlThroughMid(WpfPoint start, WpfPoint middle, WpfPoint end) =>
        new(2 * middle.X - (start.X + end.X) / 2, 2 * middle.Y - (start.Y + end.Y) / 2);

    public static void GetAutoCubicControls(SceneConnectionVisual connVis, WpfPoint startLead, WpfPoint endLead, out WpfPoint c1, out WpfPoint c2)
    {
        int sourceAnchor = connVis.Connection.SourceAnchorIndex ?? 4;
        int targetAnchor = connVis.Connection.TargetAnchorIndex ?? 10;
        Vector2 sourceNormal = GetConnectionAnchorNormal(sourceAnchor);
        Vector2 targetNormal = GetConnectionAnchorNormal(targetAnchor);

        double dx = endLead.X - startLead.X;
        double dy = endLead.Y - startLead.Y;
        double tangent = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) * 0.42, MinAutoTangent, MaxAutoTangent);

        c1 = new WpfPoint(startLead.X + sourceNormal.X * tangent, startLead.Y + sourceNormal.Y * tangent);
        c2 = new WpfPoint(endLead.X + targetNormal.X * tangent, endLead.Y + targetNormal.Y * tangent);
    }

    public static bool HasCustomConnectionMidPoint(RenderConnection conn) =>
        conn.MidControlX is not null || conn.SourceControlX is not null || conn.TargetControlX is not null;

    public static Vector2 Normalize(WpfPoint from, WpfPoint to)
    {
        var v = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
        float len = v.Length();
        return len > 0.001f ? v / len : Vector2.UnitX;
    }

    public static WpfColor TokenColor(SemanticTokenKind kind) => kind switch
    {
        SemanticTokenKind.Keyword => WpfColor.FromRgb(124, 58, 237),
        SemanticTokenKind.Type => WpfColor.FromRgb(14, 116, 144),
        SemanticTokenKind.Function => WpfColor.FromRgb(37, 99, 235),
        SemanticTokenKind.Property => WpfColor.FromRgb(3, 105, 161),
        SemanticTokenKind.Field => WpfColor.FromRgb(79, 70, 229),
        SemanticTokenKind.String => WpfColor.FromRgb(194, 65, 12),
        SemanticTokenKind.Comment => WpfColor.FromRgb(100, 116, 139),
        SemanticTokenKind.Number => WpfColor.FromRgb(180, 83, 9),
        SemanticTokenKind.Preprocessor => WpfColor.FromRgb(107, 114, 128),
        SemanticTokenKind.Operator => WpfColor.FromRgb(75, 85, 99),
        _ => WpfColor.FromRgb(17, 24, 39)
    };

    public readonly record struct LinearShapeBody(
        IReadOnlyList<WpfPoint> RelativePoints,
        string? StartKey,
        string? EndKey,
        WpfPoint? StartOffset = null,
        WpfPoint? EndOffset = null,
        IReadOnlyList<bool>? CurvedFlags = null);

    /// <summary>
    /// Encodes a linear shape body as "points:x1,y1;x2,y2;..." with each coordinate in [0,1]
    /// (normalized to the given bounds). Optionally appends "|attach:srcKey,dstKey".
    /// </summary>
    public static string BuildLinearShapeBody(
        Rect bounds,
        IReadOnlyList<WpfPoint> vertices,
        string? attachStartKey = null,
        string? attachEndKey = null,
        WpfPoint? startOffset = null,
        WpfPoint? endOffset = null,
        IReadOnlyList<bool>? curvedFlags = null)
    {
        if (vertices.Count < 2) return string.Empty;
        double w = Math.Max(1, bounds.Width);
        double h = Math.Max(1, bounds.Height);
        var pairs = new List<string>();
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            double rx = Math.Clamp((v.X - bounds.X) / w, 0, 1);
            double ry = Math.Clamp((v.Y - bounds.Y) / h, 0, 1);
            bool isCurved = curvedFlags is not null && i < curvedFlags.Count && curvedFlags[i];
            string pair = isCurved
                ? string.Create(CultureInfo.InvariantCulture, $"{rx:F4},{ry:F4},c")
                : string.Create(CultureInfo.InvariantCulture, $"{rx:F4},{ry:F4}");
            pairs.Add(pair);
        }
        string body = "points:" + string.Join(";", pairs);
        if (!string.IsNullOrEmpty(attachStartKey) || !string.IsNullOrEmpty(attachEndKey) || startOffset is not null || endOffset is not null)
        {
            string sVal = attachStartKey ?? string.Empty;
            if (startOffset is not null)
                sVal += string.Create(CultureInfo.InvariantCulture, $":{startOffset.Value.X:F4}:{startOffset.Value.Y:F4}");
            string eVal = attachEndKey ?? string.Empty;
            if (endOffset is not null)
                eVal += string.Create(CultureInfo.InvariantCulture, $":{endOffset.Value.X:F4}:{endOffset.Value.Y:F4}");
            body += $"|attach:{sVal},{eVal}";
        }
        return body;
    }

    public static LinearShapeBody ParseLinearShapeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new LinearShapeBody(Array.Empty<WpfPoint>(), null, null);

        string pointsText = body;
        string? startKey = null;
        string? endKey = null;
        WpfPoint? startOffset = null;
        WpfPoint? endOffset = null;

        foreach (string section in body.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (section.StartsWith("points:", StringComparison.OrdinalIgnoreCase))
                pointsText = section["points:".Length..];
            else if (section.StartsWith("attach:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = section["attach:".Length..].Split(',', 2);
                string? sPart = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : null;
                string? ePart = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;

                if (sPart is not null)
                {
                    int lastColon = sPart.LastIndexOf(':');
                    int prevColon = lastColon > 0 ? sPart.LastIndexOf(':', lastColon - 1) : -1;
                    if (lastColon > 0 && prevColon > 0
                        && double.TryParse(sPart[(prevColon + 1)..lastColon], NumberStyles.Float, CultureInfo.InvariantCulture, out double rx)
                        && double.TryParse(sPart[(lastColon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double ry))
                    {
                        startKey = sPart[..prevColon];
                        startOffset = new WpfPoint(rx, ry);
                    }
                    else
                    {
                        startKey = sPart;
                    }
                }
                if (ePart is not null)
                {
                    int lastColon = ePart.LastIndexOf(':');
                    int prevColon = lastColon > 0 ? ePart.LastIndexOf(':', lastColon - 1) : -1;
                    if (lastColon > 0 && prevColon > 0
                        && double.TryParse(ePart[(prevColon + 1)..lastColon], NumberStyles.Float, CultureInfo.InvariantCulture, out double rx)
                        && double.TryParse(ePart[(lastColon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double ry))
                    {
                        endKey = ePart[..prevColon];
                        endOffset = new WpfPoint(rx, ry);
                    }
                    else
                    {
                        endKey = ePart;
                    }
                }
            }
        }

        if (pointsText.StartsWith("points:", StringComparison.OrdinalIgnoreCase))
            pointsText = pointsText["points:".Length..];

        var points = new List<WpfPoint>();
        var curvedFlags = new List<bool>();
        foreach (string pair in pointsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rx)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ry))
                continue;
            points.Add(new WpfPoint(rx, ry));
            curvedFlags.Add(parts.Length > 2 && parts[2] == "c");
        }

        return new LinearShapeBody(points, startKey, endKey, startOffset, endOffset, curvedFlags);
    }

    /// <summary>
    /// Resolves the world-space vertices of a linear shape. Normalized points are mapped against
    /// the block's STORED bounds (X/Y/W/H), not the visual bounds, so interior vertices (elbows)
    /// stay world-static when an attached endpoint's outline moves. Endpoints attached to other
    /// blocks are replaced with the outline point of the attached block aimed at the adjacent
    /// vertex — yielding "center-line aim, clip at outline" behavior.
    /// </summary>
    public static IReadOnlyList<WpfPoint> ResolveLinearShapePoints(
        RenderBlock block,
        Rect outer,
        IReadOnlyDictionary<string, SceneBlockVisual>? blockLookup = null)
    {
        // Always resolve normalized points against the stored block bounds (not visual bounds).
        // outer is accepted for backwards-compatibility but ignored when stored bounds are usable.
        Rect storedBounds = block.Width > 0 && block.Height > 0
            ? new Rect(block.X, block.Y, block.Width, block.Height)
            : outer;

        var body = ParseLinearShapeBody(block.Body);
        var points = body.RelativePoints.Count >= 2
            ? body.RelativePoints.Select(p => new WpfPoint(
                storedBounds.X + Math.Clamp(p.X, 0, 1) * storedBounds.Width,
                storedBounds.Y + Math.Clamp(p.Y, 0, 1) * storedBounds.Height)).ToList()
            : new List<WpfPoint> { new(storedBounds.Left, storedBounds.Top), new(storedBounds.Right, storedBounds.Bottom) };

        if (blockLookup is null || points.Count < 2)
            return points;

        SceneBlockVisual? startBlock = body.StartKey is null ? null : blockLookup.GetValueOrDefault(body.StartKey);
        SceneBlockVisual? endBlock = body.EndKey is null ? null : blockLookup.GetValueOrDefault(body.EndKey);
        if (startBlock is null && endBlock is null) return points;

        WpfPoint? T_start = null;
        if (startBlock is not null && body.StartOffset is WpfPoint sOff)
        {
            T_start = new WpfPoint(
                startBlock.Bounds.X + sOff.X * startBlock.Bounds.Width,
                startBlock.Bounds.Y + sOff.Y * startBlock.Bounds.Height);
        }

        WpfPoint? T_end = null;
        if (endBlock is not null && body.EndOffset is WpfPoint eOff)
        {
            T_end = new WpfPoint(
                endBlock.Bounds.X + eOff.X * endBlock.Bounds.Width,
                endBlock.Bounds.Y + eOff.Y * endBlock.Bounds.Height);
        }

        startBlock = IsLinearShape(startBlock) ? null : startBlock;
        endBlock = IsLinearShape(endBlock) ? null : endBlock;
        if (startBlock is null) T_start = null;
        if (endBlock is null) T_end = null;
        if (startBlock is null && endBlock is null) return points;

        // Aim toward the NEXT/PREVIOUS interior vertex, or the opposite offset point.
        WpfPoint startToward;
        WpfPoint endToward;

        if (points.Count == 2)
        {
            startToward = T_end ?? (endBlock is not null ? CenterOf(endBlock.Bounds) : points[1]);
            endToward = T_start ?? (startBlock is not null ? CenterOf(startBlock.Bounds) : points[0]);
        }
        else
        {
            startToward = points[1];
            endToward = points[^2];
        }

        if (startBlock is not null)
        {
            points[0] = T_start ?? GetBlockOutlinePoint(startBlock.Block, startBlock.Bounds, startToward);
        }
        if (endBlock is not null)
        {
            points[^1] = T_end ?? GetBlockOutlinePoint(endBlock.Block, endBlock.Bounds, endToward);
        }

        return points;
    }

    private static bool IsLinearShape(SceneBlockVisual? block) =>
        block?.Block.Kind == BlockKind.Shape
        && block.Block.ShapeType is "line" or "arrow" or "polyline";

}
