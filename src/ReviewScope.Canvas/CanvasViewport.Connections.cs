using ReviewScope.Domain;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Vortice;
using Vortice.Direct2D1;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.Connections.cs
 * Purpose: Partial class for CanvasViewport handling connection-specific hit testing, state management, and updates.
 * Functions:
 * - Hit testing for connection anchors, arrows, curves, and control nodes.
 * - Connection rewire and move logic.
 * - Selection and deletion of connections.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal sealed record ConnectionAnchorHit(SceneBlockVisual Block, int AnchorIndex, Point Point);
internal sealed record ConnectionControlHit(SceneConnectionVisual Connection, ConnectionControlNodeKind Kind, Point Point);
internal sealed record ConnectionEndpointHit(SceneConnectionVisual Connection, ConnectionEndpointKind Kind);

public sealed partial class CanvasViewport
{
    private const double ConnectionArrowHitRadius = 17;
    private const double ConnectionAnchorHitRadius = 9;
    private const double ConnectionControlHitRadius = 9;
    private const int ConnectionHitSamples = 48;

    internal static int FindNearestConnectionAnchor(Rect bounds, Point world)
    {
        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            Point anchor = CanvasDrawingUtils.GetConnectionAnchorPoint(bounds, i);
            double distance = CanvasDrawingUtils.DistanceSquared(anchor, world);
            if (distance >= bestDistance) continue;
            bestIndex = i;
            bestDistance = distance;
        }
        return bestIndex;
    }

    internal static int FindNearestConnectionAnchor(SceneBlockVisual block, Point world)
    {
        int bestIndex = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            Point anchor = CanvasDrawingUtils.GetConnectionAnchorPoint(block, i);
            double distance = CanvasDrawingUtils.DistanceSquared(anchor, world);
            if (distance >= bestDistance) continue;
            bestIndex = i;
            bestDistance = distance;
        }
        return bestIndex;
    }

    internal ConnectionAnchorHit? HitConnectionAnchor(Point world)
    {
        if (!ConnectorsEnabled) return null;

        double radius = ConnectionAnchorHitRadius / Math.Max(0.12, _camera.Zoom);
        double best = radius * radius;
        ConnectionAnchorHit? bestHit = null;

        foreach (var block in _snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (block.Block.Kind is BlockKind.Note) continue;
            if (block.Block.Kind == BlockKind.Shape && IsLinearShapeTool(block.Block.ShapeType)) continue;
            for (int i = 0; i < 16; i++)
            {
                Point anchor = CanvasDrawingUtils.GetConnectionAnchorPoint(block, i);
                double distance = CanvasDrawingUtils.DistanceSquared(anchor, world);
                if (distance >= best) continue;
                best = distance;
                bestHit = new ConnectionAnchorHit(block, i, anchor);
            }
        }

        return bestHit;
    }

    internal static void GetConnectionPathPoints(SceneConnectionVisual connVis, out Point startLead, out Point middlePoint, out Point endLead) =>
        CanvasDrawingUtils.GetConnectionPathPoints(connVis, out startLead, out middlePoint, out endLead);

    internal static Point EvaluateConnectionPoint(SceneConnectionVisual connVis, double t) =>
        CanvasDrawingUtils.EvaluateConnectionPoint(connVis, t);

    internal static Vector2 EvaluateConnectionTangent(SceneConnectionVisual connVis, double t) =>
        CanvasDrawingUtils.EvaluateConnectionTangent(connVis, t);

    internal static Point Lerp(Point a, Point b, double t) => CanvasDrawingUtils.Lerp(a, b, t);

    internal static IReadOnlyList<Point> BuildConnectionPolyline(SceneConnectionVisual connVis) => CanvasDrawingUtils.BuildConnectionPolyline(connVis);

    internal static Point GetQuadraticControlThroughMid(Point start, Point middle, Point end) => CanvasDrawingUtils.GetQuadraticControlThroughMid(start, middle, end);

    internal static void GetAutoCubicControls(SceneConnectionVisual connVis, Point startLead, Point endLead, out Point c1, out Point c2) =>
        CanvasDrawingUtils.GetAutoCubicControls(connVis, startLead, endLead, out c1, out c2);

    internal void DrawInlineArrow(Point center, Vector2 tangent, ID2D1SolidColorBrush brush, float stroke)
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

    internal SceneConnectionVisual? HitConnectionArrow(Point world)
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

    internal ConnectionControlHit? HitConnectionControlNode(Point world)
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

    internal ConnectionEndpointHit? HitConnectionEndpoint(ConnectionAnchorHit anchor)
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

    internal SceneConnectionVisual? HitConnectionCurve(Point world, out double t)
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

    internal void MoveConnectionControl(Guid id, ConnectionControlNodeKind kind, Point world)
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

    internal void BeginConnectionRewire(ConnectionEndpointHit endpoint)
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

        _connectionDraftMidPoint = CanvasDrawingUtils.HasCustomConnectionMidPoint(endpoint.Connection.Connection)
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

    internal void CompleteConnectionRewire(ConnectionAnchorHit anchor)
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

    internal void SelectConnection(Guid id, ConnectionControlNodeKind controlKind = ConnectionControlNodeKind.None)
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

    internal RenderScene ClearConnectionSelection(RenderScene scene)
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

    internal void ClearConnectionDrawingState()
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

    internal void UpdateConnectionHoverTarget(Point world)
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
