using ReviewScope.Domain;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Vortice;
using Vortice.Direct2D1;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

internal sealed record ConnectionAnchorHit(SceneBlockVisual Block, int AnchorIndex, Point Point);
internal sealed record ConnectionControlHit(SceneConnectionVisual Connection, ConnectionControlNodeKind Kind, Point Point);
internal sealed record ConnectionEndpointHit(SceneConnectionVisual Connection, ConnectionEndpointKind Kind);

public sealed partial class CanvasViewport
{
    private const double ConnectionArrowHitRadius = 14;
    private const double ConnectionAnchorHitRadius = 6;
    private const double ConnectionControlHitRadius = 6;
    private const double ConnectionLeadDistance = 8;
    private const double MinAutoTangent = 72;
    private const double MaxAutoTangent = 260;
    private const int ConnectionHitSamples = 48;

    private static Point CenterOf(Rect bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static Point GetConnectionAnchorPoint(Rect bounds, int anchorIndex)
    {
        double p1x = bounds.X + bounds.Width * 0.20;
        double p2x = bounds.X + bounds.Width * 0.40;
        double p3x = bounds.X + bounds.Width * 0.60;
        double p4x = bounds.X + bounds.Width * 0.80;
        double p1y = bounds.Y + bounds.Height * 0.20;
        double p2y = bounds.Y + bounds.Height * 0.40;
        double p3y = bounds.Y + bounds.Height * 0.60;
        double p4y = bounds.Y + bounds.Height * 0.80;

        return Math.Clamp(anchorIndex, 0, 15) switch
        {
            0 => new Point(p1x, bounds.Top),
            1 => new Point(p2x, bounds.Top),
            2 => new Point(p3x, bounds.Top),
            3 => new Point(p4x, bounds.Top),
            4 => new Point(bounds.Right, p1y),
            5 => new Point(bounds.Right, p2y),
            6 => new Point(bounds.Right, p3y),
            7 => new Point(bounds.Right, p4y),
            8 => new Point(p4x, bounds.Bottom),
            9 => new Point(p3x, bounds.Bottom),
            10 => new Point(p2x, bounds.Bottom),
            11 => new Point(p1x, bounds.Bottom),
            12 => new Point(bounds.Left, p4y),
            13 => new Point(bounds.Left, p3y),
            14 => new Point(bounds.Left, p2y),
            _ => new Point(bounds.Left, p1y)
        };
    }

    private static Point GetConnectionAnchorPoint(SceneBlockVisual block, int anchorIndex) =>
        GetBlockOutlinePoint(block.Block, block.Bounds, GetConnectionAnchorPoint(block.Bounds, anchorIndex));

    private static Point GetBlockOutlinePoint(RenderBlock block, Rect bounds, Point toward)
    {
        string? shape = block.ShapeType;
        Point center = CenterOf(bounds);
        double dx = toward.X - center.X;
        double dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
            return toward;

        if (shape is "circle" or "oval")
        {
            Rect ellipse = shape == "circle" ? CenteredSquare(bounds) : bounds;
            Point ellipseCenter = CenterOf(ellipse);
            dx = toward.X - ellipseCenter.X;
            dy = toward.Y - ellipseCenter.Y;
            double rx = Math.Max(1, ellipse.Width / 2);
            double ry = Math.Max(1, ellipse.Height / 2);
            double scale = 1 / Math.Sqrt((dx * dx) / (rx * rx) + (dy * dy) / (ry * ry));
            return new Point(ellipseCenter.X + dx * scale, ellipseCenter.Y + dy * scale);
        }

        if (shape is "risk" or "decision" or "diamond")
        {
            double halfW = Math.Max(1, bounds.Width / 2);
            double halfH = Math.Max(1, bounds.Height / 2);
            double scale = 1 / ((Math.Abs(dx) / halfW) + (Math.Abs(dy) / halfH));
            return new Point(center.X + dx * scale, center.Y + dy * scale);
        }

        return GetRectOutlinePoint(bounds, toward);
    }

    private static Point GetRectOutlinePoint(Rect bounds, Point toward)
    {
        Point center = CenterOf(bounds);
        double dx = toward.X - center.X;
        double dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
            return center;

        double sx = Math.Abs(dx) < 0.001 ? double.PositiveInfinity : (bounds.Width / 2) / Math.Abs(dx);
        double sy = Math.Abs(dy) < 0.001 ? double.PositiveInfinity : (bounds.Height / 2) / Math.Abs(dy);
        double scale = Math.Min(sx, sy);
        return new Point(center.X + dx * scale, center.Y + dy * scale);
    }

    private static Vector2 GetConnectionAnchorNormal(int anchorIndex) =>
        Math.Clamp(anchorIndex, 0, 15) switch
        {
            <= 3 => new Vector2(0, -1),
            <= 7 => new Vector2(1, 0),
            <= 11 => new Vector2(0, 1),
            _ => new Vector2(-1, 0)
        };

    private static int FindNearestConnectionAnchor(Rect bounds, Point world)
    {
        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            Point anchor = GetConnectionAnchorPoint(bounds, i);
            double dx = anchor.X - world.X;
            double dy = anchor.Y - world.Y;
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestIndex = i;
            bestDistance = distance;
        }
        return bestIndex;
    }

    private static int FindNearestConnectionAnchor(SceneBlockVisual block, Point world)
    {
        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            Point anchor = GetConnectionAnchorPoint(block, i);
            double dx = anchor.X - world.X;
            double dy = anchor.Y - world.Y;
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestIndex = i;
            bestDistance = distance;
        }
        return bestIndex;
    }

    private ConnectionAnchorHit? HitConnectionAnchor(Point world)
    {
        if (!ConnectorsEnabled) return null;

        double radius = ConnectionAnchorHitRadius / Math.Max(0.12, _camera.Zoom);
        double best = radius * radius;
        ConnectionAnchorHit? bestHit = null;

        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (block.Block.Kind is BlockKind.Note or BlockKind.Shape) continue;
            for (int i = 0; i < 16; i++)
            {
                Point anchor = GetConnectionAnchorPoint(block, i);
                double dx = anchor.X - world.X;
                double dy = anchor.Y - world.Y;
                double distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                bestHit = new ConnectionAnchorHit(block, i, anchor);
            }
        }

        return bestHit;
    }

    private static void GetConnectionPathPoints(SceneConnectionVisual connVis, out Point startLead, out Point middlePoint, out Point endLead)
    {
        int sourceAnchor = connVis.Connection.SourceAnchorIndex ?? 4;
        int targetAnchor = connVis.Connection.TargetAnchorIndex ?? 10;
        Vector2 sourceNormal = GetConnectionAnchorNormal(sourceAnchor);
        Vector2 targetNormal = GetConnectionAnchorNormal(targetAnchor);

        startLead = new Point(
            connVis.Start.X + sourceNormal.X * ConnectionLeadDistance,
            connVis.Start.Y + sourceNormal.Y * ConnectionLeadDistance);
        endLead = new Point(
            connVis.End.X + targetNormal.X * ConnectionLeadDistance,
            connVis.End.Y + targetNormal.Y * ConnectionLeadDistance);

        if (connVis.Connection.MidControlX is double mx && connVis.Connection.MidControlY is double my)
        {
            middlePoint = new Point(mx, my);
            return;
        }

        if (connVis.Connection.SourceControlX is double sx && connVis.Connection.SourceControlY is double sy)
        {
            middlePoint = new Point(sx, sy);
            return;
        }

        if (connVis.Connection.TargetControlX is double tx && connVis.Connection.TargetControlY is double ty)
        {
            middlePoint = new Point(tx, ty);
            return;
        }

        middlePoint = new Point((startLead.X + endLead.X) / 2, (startLead.Y + endLead.Y) / 2);
    }

    private static Point EvaluateConnectionPoint(SceneConnectionVisual connVis, double t)
    {
        if (connVis.Connection.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
            return EvaluatePolylinePoint(BuildConnectionPolyline(connVis), t);

        GetConnectionPathPoints(connVis, out Point startLead, out Point mid, out Point endLead);
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
            Point control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            double qx = u * u * startLead.X + 2 * u * bt * control.X + bt * bt * endLead.X;
            double qy = u * u * startLead.Y + 2 * u * bt * control.Y + bt * bt * endLead.Y;
            return new Point(qx, qy);
        }

        GetAutoCubicControls(connVis, startLead, endLead, out Point c1, out Point c2);
        double x = u * u * u * startLead.X
                 + 3 * u * u * bt * c1.X
                 + 3 * u * bt * bt * c2.X
                 + bt * bt * bt * endLead.X;
        double y = u * u * u * startLead.Y
                 + 3 * u * u * bt * c1.Y
                 + 3 * u * bt * bt * c2.Y
                 + bt * bt * bt * endLead.Y;
        return new Point(x, y);
    }

    private static Vector2 EvaluateConnectionTangent(SceneConnectionVisual connVis, double t)
    {
        if (connVis.Connection.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
            return EvaluatePolylineTangent(BuildConnectionPolyline(connVis), t);

        GetConnectionPathPoints(connVis, out Point startLead, out Point mid, out Point endLead);
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
            Point control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            x = 2 * (1 - bt) * (control.X - startLead.X) + 2 * bt * (endLead.X - control.X);
            y = 2 * (1 - bt) * (control.Y - startLead.Y) + 2 * bt * (endLead.Y - control.Y);
        }
        else
        {
            GetAutoCubicControls(connVis, startLead, endLead, out Point c1, out Point c2);
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

    private static Point Lerp(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static IReadOnlyList<Point> BuildConnectionPolyline(SceneConnectionVisual connVis)
    {
        if (connVis.Connection.RouteKind == ConnectorRouteKind.Straight)
            return new[] { connVis.Start, connVis.End };

        GetConnectionPathPoints(connVis, out Point startLead, out Point middle, out Point endLead);
        var points = new List<Point> { connVis.Start, startLead };

        if (HasCustomConnectionMidPoint(connVis.Connection))
        {
            points.Add(new Point(middle.X, startLead.Y));
            points.Add(middle);
            points.Add(new Point(middle.X, endLead.Y));
        }
        else
        {
            bool horizontalFirst = Math.Abs(endLead.X - startLead.X) >= Math.Abs(endLead.Y - startLead.Y);
            if (horizontalFirst)
            {
                double midX = (startLead.X + endLead.X) / 2;
                points.Add(new Point(midX, startLead.Y));
                points.Add(new Point(midX, endLead.Y));
            }
            else
            {
                double midY = (startLead.Y + endLead.Y) / 2;
                points.Add(new Point(startLead.X, midY));
                points.Add(new Point(endLead.X, midY));
            }
        }

        points.Add(endLead);
        points.Add(connVis.End);
        return RemoveDuplicatePoints(points);
    }

    private static IReadOnlyList<Point> RemoveDuplicatePoints(IReadOnlyList<Point> points)
    {
        var compact = new List<Point>(points.Count);
        foreach (var point in points)
        {
            if (compact.Count == 0 || DistanceSquared(compact[^1], point) > 0.01)
                compact.Add(point);
        }
        return compact;
    }

    private static Point EvaluatePolylinePoint(IReadOnlyList<Point> points, double t)
    {
        if (points.Count == 0) return new Point();
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

    private static Vector2 EvaluatePolylineTangent(IReadOnlyList<Point> points, double t)
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

    private static double PolylineLength(IReadOnlyList<Point> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
            total += Distance(points[i - 1], points[i]);
        return total;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(DistanceSquared(a, b));

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static Point GetQuadraticControlThroughMid(Point start, Point middle, Point end) =>
        new(2 * middle.X - (start.X + end.X) / 2, 2 * middle.Y - (start.Y + end.Y) / 2);

    private static void GetAutoCubicControls(SceneConnectionVisual connVis, Point startLead, Point endLead, out Point c1, out Point c2)
    {
        int sourceAnchor = connVis.Connection.SourceAnchorIndex ?? 4;
        int targetAnchor = connVis.Connection.TargetAnchorIndex ?? 10;
        Vector2 sourceNormal = GetConnectionAnchorNormal(sourceAnchor);
        Vector2 targetNormal = GetConnectionAnchorNormal(targetAnchor);
        double dx = endLead.X - startLead.X;
        double dy = endLead.Y - startLead.Y;
        double tangent = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) * 0.42, MinAutoTangent, MaxAutoTangent);
        c1 = new Point(startLead.X + sourceNormal.X * tangent, startLead.Y + sourceNormal.Y * tangent);
        c2 = new Point(endLead.X + targetNormal.X * tangent, endLead.Y + targetNormal.Y * tangent);
    }

    private static Vector2 Normalize(Point from, Point to)
    {
        var v = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
        float len = v.Length();
        return len > 0.001f ? v / len : Vector2.UnitX;
    }

    private void DrawInlineArrow(Point center, Vector2 tangent, ID2D1SolidColorBrush brush, float stroke)
    {
        if (_rt is null || _factory is null) return;
        float len = Math.Max(10f, stroke * 6f);
        float width = len * 0.58f;
        Vector2 dir = tangent.LengthSquared() > 0.001f ? Vector2.Normalize(tangent) : Vector2.UnitX;
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 origin = new((float)center.X, (float)center.Y);
        Vector2 tip = origin + dir * (len * 0.55f);
        Vector2 tail = origin - dir * (len * 0.55f);
        Vector2 left = tail + perp * width;
        Vector2 right = tail - perp * width;

        using var path = _factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(tip, FigureBegin.Filled);
        sink.AddLine(left);
        sink.AddLine(right);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();

        _rt.FillGeometry(path, brush);
        _rt.DrawEllipse(new Ellipse(origin, len * 0.9f, len * 0.9f),
            GetBrush(WpfColor.FromArgb(45, 69, 132, 203)), InvStroke(1.0f));
    }

    private SceneConnectionVisual? HitConnectionArrow(Point world)
    {
        double radius = ConnectionArrowHitRadius / Math.Max(0.08, _camera.Zoom);
        double best = radius * radius;
        SceneConnectionVisual? bestHit = null;
        foreach (var conn in _visibleConnections)
        {
            if (conn.Connection.Label == "__note" || conn.Connection.ArrowPosition is not double t) continue;
            Point p = EvaluateConnectionPoint(conn, Math.Clamp(t, 0.04, 0.96));
            double dx = p.X - world.X;
            double dy = p.Y - world.Y;
            double d = dx * dx + dy * dy;
            if (d >= best) continue;
            best = d;
            bestHit = conn;
        }
        return bestHit;
    }

    private ConnectionControlHit? HitConnectionControlNode(Point world)
    {
        double radius = ConnectionControlHitRadius / Math.Max(0.08, _camera.Zoom);
        double best = radius * radius;
        ConnectionControlHit? bestHit = null;

        foreach (var conn in _visibleConnections)
        {
            if (conn.Connection.Label == "__note" || !conn.Connection.IsSelected) continue;
            GetConnectionPathPoints(conn, out _, out Point middleControl, out _);
            TryControl(ConnectionControlNodeKind.Middle, middleControl);

            void TryControl(ConnectionControlNodeKind kind, Point point)
            {
                double dx = point.X - world.X;
                double dy = point.Y - world.Y;
                double d = dx * dx + dy * dy;
                if (d >= best) return;
                best = d;
                bestHit = new ConnectionControlHit(conn, kind, point);
            }
        }

        return bestHit;
    }

    private ConnectionEndpointHit? HitConnectionEndpoint(ConnectionAnchorHit anchor)
    {
        foreach (var conn in _snapshot.Connections.Reverse<SceneConnectionVisual>())
        {
            if (conn.Connection.Label == "__note") continue;
            if (conn.Connection.TargetKey.Equals(anchor.Block.Block.Key, StringComparison.OrdinalIgnoreCase)
                && (conn.Connection.TargetAnchorIndex ?? FindNearestConnectionAnchor(anchor.Block, conn.Start)) == anchor.AnchorIndex)
                return new ConnectionEndpointHit(conn, ConnectionEndpointKind.Target);

            if (conn.Connection.SourceKey.Equals(anchor.Block.Block.Key, StringComparison.OrdinalIgnoreCase)
                && (conn.Connection.SourceAnchorIndex ?? FindNearestConnectionAnchor(anchor.Block, conn.End)) == anchor.AnchorIndex)
                return new ConnectionEndpointHit(conn, ConnectionEndpointKind.Source);
        }

        return null;
    }

    private SceneConnectionVisual? HitConnectionCurve(Point world, out double t)
    {
        t = 0.5;
        double radius = 14 / Math.Max(0.08, _camera.Zoom);
        double best = radius * radius;
        SceneConnectionVisual? bestHit = null;
        foreach (var conn in _visibleConnections)
        {
            if (conn.Connection.Label == "__note") continue;
            for (int i = 0; i <= ConnectionHitSamples; i++)
            {
                double sampleT = i / (double)ConnectionHitSamples;
                Point p = EvaluateConnectionPoint(conn, sampleT);
                double dx = p.X - world.X;
                double dy = p.Y - world.Y;
                double d = dx * dx + dy * dy;
                if (d >= best) continue;
                best = d;
                bestHit = conn;
                t = sampleT;
            }
        }
        return bestHit;
    }

    private double FindNearestConnectionT(SceneConnectionVisual conn, Point world)
    {
        double bestT = conn.Connection.ArrowPosition ?? 0.5;
        double best = double.MaxValue;
        for (int i = 0; i <= ConnectionHitSamples; i++)
        {
            double t = i / (double)ConnectionHitSamples;
            Point p = EvaluateConnectionPoint(conn, t);
            double dx = p.X - world.X;
            double dy = p.Y - world.Y;
            double d = dx * dx + dy * dy;
            if (d >= best) continue;
            best = d;
            bestT = t;
        }
        return Math.Clamp(bestT, 0.04, 0.96);
    }

    private void AddOrMoveConnectionArrow(Point world)
    {
        var hit = HitConnectionCurve(world, out double t);
        if (hit is null) return;
        UpdateConnection(hit.Connection.Id, c => c with { ArrowPosition = Math.Clamp(t, 0.04, 0.96) });
    }

    private void ToggleConnectionArrow(Guid id) =>
        UpdateConnection(id, c => c with { ArrowForward = !c.ArrowForward });

    private void MoveConnectionArrow(Guid id, Point world)
    {
        var visual = _snapshot.Connections.FirstOrDefault(c => c.Connection.Id == id);
        if (visual is null) return;
        double t = FindNearestConnectionT(visual, world);
        UpdateConnection(id, c => c with { ArrowPosition = t });
    }

    private void MoveConnectionControl(Guid id, ConnectionControlNodeKind kind, Point world)
    {
        UpdateConnection(id, c => kind switch
        {
            ConnectionControlNodeKind.Middle => c with
            {
                MidControlX = world.X,
                MidControlY = world.Y,
                MidControlBends = true,
                SourceControlX = null,
                SourceControlY = null,
                TargetControlX = null,
                TargetControlY = null
            },
            _ => c
        });
    }

    private void BeginConnectionRewire(ConnectionEndpointHit endpoint)
    {
        _rewireConnectionId = endpoint.Connection.Connection.Id;
        _rewireEndpointKind = endpoint.Kind;
        SelectConnection(endpoint.Connection.Connection.Id);

        if (endpoint.Kind == ConnectionEndpointKind.Target)
        {
            _connectionSourceKey = endpoint.Connection.Connection.SourceKey;
            _connectionSourceAnchorIndex = endpoint.Connection.Connection.SourceAnchorIndex;
            _connectionSourceWorld = endpoint.Connection.Start;
            _connectionCurrentWorld = endpoint.Connection.End;
            _rewireFixedWorld = endpoint.Connection.Start;
            _rewireFixedAnchorIndex = endpoint.Connection.Connection.SourceAnchorIndex;
        }
        else
        {
            _connectionSourceKey = endpoint.Connection.Connection.TargetKey;
            _connectionSourceAnchorIndex = endpoint.Connection.Connection.TargetAnchorIndex;
            _connectionSourceWorld = endpoint.Connection.End;
            _connectionCurrentWorld = endpoint.Connection.Start;
            _rewireFixedWorld = endpoint.Connection.End;
            _rewireFixedAnchorIndex = endpoint.Connection.Connection.TargetAnchorIndex;
        }

        _connectionDraftMidPoint = HasCustomConnectionMidPoint(endpoint.Connection.Connection)
            ? GetConnectionMidPoint(endpoint.Connection)
            : null;
        _connectionDraftMidPointBends = endpoint.Connection.Connection.MidControlBends;
        _isDrawingConnection = true;
        _connectionHoverTargetKey = null;
        _connectionHoverTargetAnchorIndex = null;
        _connectionHoverTargetWorld = null;
        _dragStartScreen = null;
        _didMove = false;
    }

    private static Point GetConnectionMidPoint(SceneConnectionVisual conn)
    {
        GetConnectionPathPoints(conn, out Point startLead, out Point mid, out Point endLead);
        if (conn.Connection.MidControlX is double || conn.Connection.SourceControlX is double || conn.Connection.TargetControlX is double)
            return mid;
        return new Point((startLead.X + endLead.X) / 2, (startLead.Y + endLead.Y) / 2);
    }

    private void CompleteConnectionRewire(ConnectionAnchorHit anchor)
    {
        if (_rewireConnectionId is not Guid id || _rewireEndpointKind == ConnectionEndpointKind.None)
            return;

        UpdateConnection(id, c => _rewireEndpointKind switch
        {
            ConnectionEndpointKind.Target => c with
            {
                TargetKey = anchor.Block.Block.Key,
                TargetAnchorIndex = anchor.AnchorIndex,
                MidControlX = _connectionDraftMidPoint?.X ?? c.MidControlX,
                MidControlY = _connectionDraftMidPoint?.Y ?? c.MidControlY,
                MidControlBends = _connectionDraftMidPoint is not null ? _connectionDraftMidPointBends : c.MidControlBends
            },
            ConnectionEndpointKind.Source => c with
            {
                SourceKey = anchor.Block.Block.Key,
                SourceAnchorIndex = anchor.AnchorIndex,
                MidControlX = _connectionDraftMidPoint?.X ?? c.MidControlX,
                MidControlY = _connectionDraftMidPoint?.Y ?? c.MidControlY,
                MidControlBends = _connectionDraftMidPoint is not null ? _connectionDraftMidPointBends : c.MidControlBends
            },
            _ => c
        });
    }

    private static bool HasCustomConnectionMidPoint(RenderConnection connection) =>
        connection.MidControlX is not null
        || connection.SourceControlX is not null
        || connection.TargetControlX is not null;

    private void ResetSelectedConnectionControl()
    {
        if (_selectedConnectionId is not Guid id || _selectedConnectionControlKind == ConnectionControlNodeKind.None)
            return;

        UpdateConnection(id, c => _selectedConnectionControlKind switch
        {
            ConnectionControlNodeKind.Middle => c with
            {
                MidControlX = null,
                MidControlY = null,
                SourceControlX = null,
                SourceControlY = null,
                TargetControlX = null,
                TargetControlY = null
            },
            _ => c
        });
        _selectedConnectionControlKind = ConnectionControlNodeKind.None;
    }

    private void SelectConnection(Guid id, ConnectionControlNodeKind controlKind = ConnectionControlNodeKind.None)
    {
        _selectedConnectionId = id;
        _selectedConnectionControlKind = controlKind;
        var connections = Scene.Connections.Select(c => c with { IsSelected = c.Id == id }).ToList();
        SetCurrentValue(SceneProperty, Scene with
        {
            Blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).ToList(),
            SwimLanes = Scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList(),
            Connections = connections
        });
        RebuildSnapshot();
        RenderNative();
    }

    private RenderScene ClearConnectionSelection(RenderScene scene)
    {
        _selectedConnectionId = null;
        _selectedConnectionControlKind = ConnectionControlNodeKind.None;
        return scene with { Connections = scene.Connections.Select(c => c with { IsSelected = false }).ToList() };
    }

    private void DeleteSelectedConnections()
    {
        if (_selectedConnectionControlKind != ConnectionControlNodeKind.None)
        {
            ResetSelectedConnectionControl();
            return;
        }

        if (_selectedConnectionId is not Guid id) return;
        var connections = Scene.Connections.Where(c => c.Id != id).ToList();
        _selectedConnectionId = null;
        ApplySceneChange(Scene with { Connections = connections });
        RebuildSnapshot();
        RenderNative();
    }

    private void UpdateConnection(Guid id, Func<RenderConnection, RenderConnection> update)
    {
        var connections = Scene.Connections
            .Select(c => c.Id == id ? update(c) : c)
            .ToList();
        ApplySceneChange(Scene with { Connections = connections });
        RebuildSnapshot();
        RenderNative();
    }

    private void ClearConnectionDrawingState()
    {
        _isDrawingConnection = false;
        _connectionSourceKey = null;
        _connectionSourceAnchorIndex = null;
        _connectionHoverTargetKey = null;
        _connectionHoverTargetAnchorIndex = null;
        _connectionHoverTargetWorld = null;
        _connectionDraftMidPoint = null;
        _connectionDraftMidPointBends = false;
        _rewireConnectionId = null;
        _rewireEndpointKind = ConnectionEndpointKind.None;
        _rewireFixedAnchorIndex = null;
    }

    private void UpdateConnectionHoverTarget(Point world)
    {
        var anchor = HitConnectionAnchor(world);
        if (anchor is not null && anchor.Block.Block.Key != _connectionSourceKey)
        {
            _connectionHoverTargetKey = anchor.Block.Block.Key;
            _connectionHoverTargetAnchorIndex = anchor.AnchorIndex;
            _connectionHoverTargetWorld = anchor.Point;
            return;
        }

        _connectionHoverTargetKey = null;
        _connectionHoverTargetAnchorIndex = null;
        _connectionHoverTargetWorld = null;
    }
}
