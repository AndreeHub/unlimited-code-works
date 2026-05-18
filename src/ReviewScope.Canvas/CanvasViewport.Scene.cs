using ReviewScope.Domain;
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
    private void ApplySceneChange(RenderScene scene)
    {
        SetCurrentValue(SceneProperty, scene);
        if (BlockMovedCommand?.CanExecute(scene) == true)
            BlockMovedCommand.Execute(scene);
    }

    private void RebuildSnapshot()
    {
        _connectionGeoms.Clear();

        var blocks = Scene.Blocks
            .Select(b => new SceneBlockVisual(b, new Rect(b.X, b.Y, b.Width, b.Height)))
            .ToList();
        var blockLookup = blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);

        var connections = new List<SceneConnectionVisual>();
        foreach (var conn in Scene.Connections)
        {
            if (!blockLookup.TryGetValue(conn.SourceKey, out var src) || !blockLookup.TryGetValue(conn.TargetKey, out var dst))
                continue;
            Point srcCenter = CenterOf(src.Bounds);
            Point dstCenter = CenterOf(dst.Bounds);
            int sourceAnchor = conn.SourceAnchorIndex ?? FindNearestConnectionAnchor(src.Bounds, dstCenter);
            int targetAnchor = conn.TargetAnchorIndex ?? FindNearestConnectionAnchor(dst.Bounds, srcCenter);
            Point start = GetConnectionAnchorPoint(src.Bounds, sourceAnchor);
            Point end = GetConnectionAnchorPoint(dst.Bounds, targetAnchor);
            Rect bounds = new(start, end);
            var visual = new SceneConnectionVisual(conn, start, end, bounds);
            GetConnectionPathPoints(visual, out Point startLead, out Point middleControl, out Point endLead);
            bounds.Union(startLead);
            bounds.Union(middleControl);
            bounds.Union(endLead);
            bounds.Inflate(80, 80);
            connections.Add(visual with { Bounds = bounds });
        }

        var swimLanes = Scene.SwimLanes
            .Select(l => new SceneSwimLaneVisual(l, new Rect(l.X, l.Y, l.Width, l.Height)))
            .ToList();

        _snapshot = new SceneSnapshot(blocks, connections, swimLanes);
        _visDirty = true;
    }

    // -----------------------------------------------------------------------
    // Visibility culling
    // -----------------------------------------------------------------------
    private void EnsureVisible(Size viewportSize)
    {
        if (!_visDirty && _lastVisCamera.Equals(_camera) && _lastVisSize == viewportSize) return;
        Rect viewport = new(new Point(0, 0), viewportSize);
        Rect world = WorldViewport(viewport, CullPadding);
        _visibleBlocks = _snapshot.QueryBlocks(world);
        var visKeys = _visibleBlocks.Select(b => b.Block.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _visibleConnections = _snapshot.QueryConnections(world, visKeys);
        _lastVisCamera = _camera; _lastVisSize = viewportSize; _visDirty = false;
    }

    private Rect WorldViewport(Rect screen, double pad)
    {
        Point tl = ToWorld(new Point(screen.Left - pad, screen.Top - pad));
        Point br = ToWorld(new Point(screen.Right + pad, screen.Bottom + pad));
        return new Rect(tl, br);
    }

    private Point ToWorld(Point screen) =>
        new((screen.X - _camera.OffsetX) / _camera.Zoom,
            (screen.Y - _camera.OffsetY) / _camera.Zoom);
}

