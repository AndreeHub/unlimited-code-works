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

/*
 * File: CanvasViewport.Scene.cs
 * Purpose: Partial class for CanvasViewport handling scene state management, snapshot rebuilding, and visibility culling.
 * Functions:
 * - ApplySceneChangeInternal: Updates the scene and triggers external commands.
 * - RebuildSnapshotInternal: Orchestrates the transformation of the domain RenderScene into a flattened SceneSnapshot for rendering.
 * - EnsureVisible: Performs visibility culling to determine which blocks and connections should be drawn.
 * - ToWorld: Translates screen coordinates to world coordinates.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

public sealed partial class CanvasViewport
{
    private sealed record CollapsedGroupMember(string GroupKey, RenderBlock Member);

    private void ApplySceneChangeInternal(RenderScene scene)
    {
        RenderScene before = Scene;
        SetCurrentValue(SceneProperty, scene);
        if (!_isCoalescingSceneChanges)
            RaiseSceneChanged(before, scene);
    }

    internal void BeginCoalescedSceneChange()
    {
        if (_isCoalescingSceneChanges)
            return;

        _isCoalescingSceneChanges = true;
        _coalescedSceneBefore = Scene;
    }

    internal void CommitCoalescedSceneChange()
    {
        if (!_isCoalescingSceneChanges)
            return;

        RenderScene? before = _coalescedSceneBefore;
        RenderScene after = Scene;
        _isCoalescingSceneChanges = false;
        _coalescedSceneBefore = null;

        if (before is not null && !ReferenceEquals(before, after))
            RaiseSceneChanged(before, after);
    }

    internal void CancelCoalescedSceneChange()
    {
        _isCoalescingSceneChanges = false;
        _coalescedSceneBefore = null;
    }

    private void RaiseSceneChanged(RenderScene before, RenderScene after)
    {
        var args = new CanvasSceneChangedArgs(before, after, HasBoardContentChanged(before, after));
        if (BlockMovedCommand?.CanExecute(args) == true)
            BlockMovedCommand.Execute(args);
    }

    private static bool HasBoardContentChanged(RenderScene before, RenderScene after)
    {
        if (ReferenceEquals(before, after))
            return false;

        return !NormalizeTransientSceneState(before).Equals(NormalizeTransientSceneState(after));
    }

    private static RenderScene NormalizeTransientSceneState(RenderScene scene) =>
        scene with
        {
            Blocks = scene.Blocks
                .Select(b => b with { IsSelected = false, IsDimmed = false })
                .ToList(),
            Connections = scene.Connections
                .Select(c => c with { IsSelected = false, IsDimmed = false })
                .ToList(),
            SwimLanes = scene.SwimLanes
                .Select(l => l with { IsSelected = false })
                .ToList()
        };

    private void RebuildSnapshotInternal()
    {
        foreach (var geometry in _connectionGeoms.Values)
            geometry.Dispose();
        _connectionGeoms.Clear();

        var collapsedGroupMembers = GetCollapsedGroupMemberMap(Scene);
        var hiddenByCollapsedGroups = collapsedGroupMembers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocks = Scene.Blocks
            .Where(b => !hiddenByCollapsedGroups.Contains(b.Key))
            .OrderBy(b => b.ZIndex)
            .ThenBy(GetBlockStackRank)
            .Select(b => new SceneBlockVisual(b, new Rect(b.X, b.Y, b.Width, b.Height)))
            .ToList();
        var blockLookup = blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);
        blocks = blocks
            .Select(v => v.Block.Kind == BlockKind.Shape && IsLinearShapeTool(v.Block.ShapeType)
                ? v with { Bounds = GetLinearShapeVisualBounds(v.Block, v.Bounds, blockLookup) }
                : v)
            .ToList();
        blockLookup = blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);

        var connections = new List<SceneConnectionVisual>();
        foreach (var conn in Scene.Connections)
        {
            bool sourceIsCollapsedMember = collapsedGroupMembers.TryGetValue(conn.SourceKey, out var sourceGroupMember);
            bool targetIsCollapsedMember = collapsedGroupMembers.TryGetValue(conn.TargetKey, out var targetGroupMember);
            string sourceKey = sourceIsCollapsedMember ? sourceGroupMember!.GroupKey : conn.SourceKey;
            string targetKey = targetIsCollapsedMember ? targetGroupMember!.GroupKey : conn.TargetKey;
            if (sourceKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!blockLookup.TryGetValue(sourceKey, out var src) || !blockLookup.TryGetValue(targetKey, out var dst))
                continue;

            Point srcCenter = CanvasDrawingUtils.CenterOf(src.Bounds);
            Point dstCenter = CanvasDrawingUtils.CenterOf(dst.Bounds);
            int? sourceAnchorIndex = conn.SourceAnchorIndex;
            int? targetAnchorIndex = conn.TargetAnchorIndex;
            if (sourceIsCollapsedMember)
                sourceAnchorIndex = FindCollapsedProxyAnchor(src.Block, sourceGroupMember!.Member, conn.SourceAnchorIndex, dstCenter);
            if (targetIsCollapsedMember)
                targetAnchorIndex = FindCollapsedProxyAnchor(dst.Block, targetGroupMember!.Member, conn.TargetAnchorIndex, srcCenter);

            var visualConn = conn with
            {
                SourceKey = sourceKey,
                TargetKey = targetKey,
                SourceAnchorIndex = sourceAnchorIndex,
                TargetAnchorIndex = targetAnchorIndex
            };
            int sourceAnchor = visualConn.SourceAnchorIndex ?? FindNearestConnectionAnchor(src, dstCenter);
            int targetAnchor = visualConn.TargetAnchorIndex ?? FindNearestConnectionAnchor(dst, srcCenter);
            Point start = CanvasDrawingUtils.GetConnectionAnchorPoint(src, sourceAnchor);
            Point end = CanvasDrawingUtils.GetConnectionAnchorPoint(dst, targetAnchor);
            Rect bounds = new(start, end);
            var visual = new SceneConnectionVisual(visualConn, start, end, bounds);
            if (conn.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
            {
                foreach (var point in BuildConnectionPolyline(visual))
                    bounds.Union(point);
            }
            else
            {
                GetConnectionPathPoints(visual, out Point startLead, out Point middleControl, out Point endLead);
                bounds.Union(startLead);
                bounds.Union(middleControl);
                bounds.Union(endLead);
            }
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
        _visibleBlocks = _snapshot.QueryBlocks(world)
            .OrderBy(b => b.Block.ZIndex)
            .ThenBy(b => GetBlockStackRank(b.Block))
            .ToList();
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

    internal Point ToWorld(Point screen) =>
        new((screen.X - _camera.OffsetX) / _camera.Zoom,
            (screen.Y - _camera.OffsetY) / _camera.Zoom);

    private static int GetBlockStackRank(RenderBlock block) =>
        block.Kind switch
        {
            BlockKind.Container => 0,
            BlockKind.File or BlockKind.Extract => 10,
            BlockKind.MarkdownDoc or BlockKind.Shape or BlockKind.Text or BlockKind.Image => 20,
            BlockKind.Note => 30,
            _ => 10
        };

    private static Dictionary<string, CollapsedGroupMember> GetCollapsedGroupMemberMap(RenderScene scene)
    {
        var hidden = new Dictionary<string, CollapsedGroupMember>(StringComparer.OrdinalIgnoreCase);
        var collapsedGroups = scene.Blocks
            .Where(b => IsColorGroup(b) && b.IsCollapsed)
            .ToList();
        if (collapsedGroups.Count == 0) return hidden;

        foreach (var group in collapsedGroups)
        {
            Rect groupBounds = GetGroupExpandedBounds(group);
            foreach (var block in scene.Blocks)
            {
                if (block.Key.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsColorGroup(block)) continue;
                if (groupBounds.IntersectsWith(new Rect(block.X, block.Y, block.Width, block.Height)))
                    hidden[block.Key] = new CollapsedGroupMember(group.Key, block);
            }
        }

        return hidden;
    }

    private static int FindCollapsedProxyAnchor(RenderBlock group, RenderBlock member, int? memberAnchorIndex, Point otherPoint)
    {
        Rect memberBounds = new(member.X, member.Y, member.Width, member.Height);
        int originalAnchor = memberAnchorIndex ?? FindNearestConnectionAnchor(memberBounds, otherPoint);
        int side = GetAnchorSide(originalAnchor);
        Rect expandedGroup = GetGroupExpandedBounds(group);
        Point memberCenter = CanvasDrawingUtils.CenterOf(memberBounds);

        double ratio = side is 0 or 2
            ? (memberCenter.X - expandedGroup.Left) / Math.Max(1, expandedGroup.Width)
            : (memberCenter.Y - expandedGroup.Top) / Math.Max(1, expandedGroup.Height);
        int lane = Math.Clamp((int)Math.Floor(ratio * 4), 0, 3);
        return AnchorForSideLane(side, lane);
    }

    private static int GetAnchorSide(int anchorIndex)
    {
        int safe = Math.Clamp(anchorIndex, 0, 15);
        if (safe <= 3) return 0;
        if (safe <= 7) return 1;
        if (safe <= 11) return 2;
        return 3;
    }

    private static int AnchorForSideLane(int side, int lane)
    {
        lane = Math.Clamp(lane, 0, 3);
        return side switch
        {
            0 => lane,
            1 => 4 + lane,
            2 => 11 - lane,
            _ => 15 - lane
        };
    }

    private static Rect GetGroupExpandedBounds(RenderBlock group) =>
        group.GroupState is { } state
            ? new Rect(state.ExpandedX, state.ExpandedY, state.ExpandedWidth, state.ExpandedHeight)
            : new Rect(group.X, group.Y, group.Width, group.Height);
}
