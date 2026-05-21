using System.Windows;
using System.Windows.Input;
using ReviewScope.Domain;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: ShapeTool.cs
 * Purpose: Interactive tool for placing shapes on the canvas.
 * Two interaction modes for linear shapes (line/arrow/polyline):
 * - Drag: hold + drag = single segment from drag-start to drag-end.
 * - Click (no drag): each click drops a polyline vertex. Shift+click marks the
 *   vertex as curved (spline). Click on a block or double-click to commit.
 * Hovering near a block snaps endpoints to the block's connection anchor and stores
 * the block key so the shape attaches and follows the block at render time.
 */

internal sealed class ShapeTool : CanvasToolBase
{
    public override string Name => "Shape";

    public ShapeTool(CanvasViewport viewport) : base(viewport) { }

    private bool IsLinear => CanvasViewport.IsLinearShapeTool(Viewport._activeShapeTool);

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        Viewport.ApplySceneChange(CanvasViewport.ClearSelection(Viewport.Scene));

        Point snapped = SnapPoint(world, out string? attachKey, out WpfPoint? relativeOffset);

        // Already in polyline mode → add a vertex, attach to block, or commit on double-click
        if (IsLinear && Viewport._shapeDraftPolyline is { Count: >= 1 } verts)
        {
            if (Viewport.IsDoubleClick("shape::draft", screen))
            {
                CommitPolyline(snapped, attachKey, relativeOffset);
                return;
            }
            // Click on a block → commit the polyline ending attached to that block.
            if (attachKey is not null)
            {
                CommitPolyline(snapped, attachKey, relativeOffset);
                return;
            }
            // Plain click outside any shape → add a polyline vertex. Shift makes it curved (spline).
            verts.Add(snapped);
            if (Viewport._shapeDraftCurvedFlags is null)
            {
                Viewport._shapeDraftCurvedFlags = new List<bool> { false }; // start is not curved
            }
            Viewport._shapeDraftCurvedFlags.Add(modifiers.HasFlag(ModifierKeys.Shift));
            Viewport._shapeDraftCurrentWorld = snapped;
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.RenderNative();
            return;
        }

        // Fresh draft
        Viewport._shapeDraftStartWorld = snapped;
        Viewport._shapeDraftCurrentWorld = snapped;
        Viewport._shapeDraftAttachStartKey = attachKey;
        Viewport._shapeDraftStartOffset = relativeOffset;
        Viewport._shapeDraftPolyline = null;
        Viewport._shapeDraftCurvedFlags = null;
        Viewport._dragStartScreen = screen;
        Viewport._didMove = false;
        Viewport.Cursor = Cursors.Cross;
        CanvasViewport.SetCapture(Viewport._hwnd);
        Viewport.RenderNative();
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._activeShapeTool is null) return;
        if (Viewport._shapeDraftStartWorld is null && Viewport._shapeDraftPolyline is null) return;

        if (!Viewport._didMove && Viewport._dragStartScreen is not null)
        {
            var d = screen - Viewport._dragStartScreen.Value;
            if (Math.Abs(d.X) >= 4 || Math.Abs(d.Y) >= 4) Viewport._didMove = true;
        }

        Point current = SnapPoint(world, out string? attachEnd, out WpfPoint? relativeEndOffset);
        Viewport._shapeDraftCurrentWorld = current;
        Viewport._shapeDraftAttachEndKey = attachEnd;
        Viewport._shapeDraftEndOffset = relativeEndOffset;
        Viewport.RenderNative();
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._activeShapeTool is null) return;
        if (Viewport._shapeDraftStartWorld is null) return;

        Point endWorld = SnapPoint(world, out string? attachEndKey, out WpfPoint? relativeEndOffset);

        // Non-linear: always commit as single shape on mouse-up
        if (!IsLinear)
        {
            double dx = endWorld.X - Viewport._shapeDraftStartWorld.Value.X;
            double dy = endWorld.Y - Viewport._shapeDraftStartWorld.Value.Y;
            if (dx * dx + dy * dy < 9)
            {
                var sz = CanvasViewport.ShapeToolDefaultSize(Viewport._activeShapeTool!);
                var center = Viewport._shapeDraftStartWorld.Value;
                var start = new Point(center.X - sz.Width / 2, center.Y - sz.Height / 2);
                var end = new Point(center.X + sz.Width / 2, center.Y + sz.Height / 2);
                var shape = Viewport.CreateShapeBlock(Viewport._activeShapeTool!, start, end);
                FinalizeShape(shape);
                return;
            }
            CommitSingleShape(endWorld);
            return;
        }

        // Linear: if the user dragged, commit the drag-line immediately.
        if (Viewport._didMove)
        {
            // If we're in polyline mode mid-stream, drag = commit current polyline including this drag endpoint
            if (Viewport._shapeDraftPolyline is { Count: >= 1 })
            {
                CommitPolyline(endWorld, attachEndKey, relativeEndOffset);
                return;
            }
            CommitLinearDrag(endWorld, attachEndKey, relativeEndOffset, modifiers);
            return;
        }

        // No drag → either the user just clicked on a fresh start, or they double-clicked to commit
        if (Viewport._shapeDraftPolyline is null && Viewport.IsDoubleClick("shape::draft", screen))
        {
            // Double-click without any polyline → cancel (zero-length shape)
            Cancel(screen);
            return;
        }

        // No drag → enter (or continue) polyline mode. First click seeded the start vertex
        // in HandleLDown; subsequent clicks are added by HandleLDown's polyline branch.
        if (Viewport._shapeDraftPolyline is null)
        {
            Viewport._shapeDraftPolyline = new List<Point> { Viewport._shapeDraftStartWorld.Value };
            Viewport._shapeDraftCurvedFlags = new List<bool> { false }; // start vertex is not curved
        }
        Viewport.Cursor = Cursors.Cross;
        CanvasViewport.ReleaseCapture();
        Viewport.RenderNative();
    }

    public override void HandleKeyDown(Key key, ModifierKeys modifiers)
    {
        if (Viewport._activeShapeTool is null) return;
        if (key == Key.Escape)
        {
            Cancel(Viewport._lastMouseScreenPoint);
            return;
        }
        if ((key == Key.Enter || key == Key.Return) && Viewport._shapeDraftPolyline is { Count: >= 2 } verts)
        {
            CommitVertices(verts, Viewport._shapeDraftAttachStartKey, null, Viewport._shapeDraftStartOffset, null, Viewport._shapeDraftCurvedFlags);
        }
    }

    private void CommitSingleShape(Point endWorld)
    {
        var shape = Viewport.CreateShapeBlock(Viewport._activeShapeTool!, Viewport._shapeDraftStartWorld!.Value, endWorld);
        FinalizeShape(shape);
    }

    private void CommitLinearDrag(Point endWorld, string? attachEndKey, Point? endOffset, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            var start = Viewport._shapeDraftStartWorld!.Value;
            double dx = endWorld.X - start.X;
            double dy = endWorld.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double mx = (start.X + endWorld.X) / 2;
            double my = (start.Y + endWorld.Y) / 2;
            if (len > 1)
            {
                double px = -dy / len;
                double py = dx / len;
                double offset = len * 0.2;
                mx += px * offset;
                my += py * offset;
            }
            var verts = new List<Point> { start, new Point(mx, my), endWorld };
            CommitVertices(verts, Viewport._shapeDraftAttachStartKey, attachEndKey, Viewport._shapeDraftStartOffset, endOffset, new[] { false, true, false });
        }
        else
        {
            var verts = new List<Point> { Viewport._shapeDraftStartWorld!.Value, endWorld };
            CommitVertices(verts, Viewport._shapeDraftAttachStartKey, attachEndKey, Viewport._shapeDraftStartOffset, endOffset, null);
        }
    }

    private void CommitPolyline(Point lastClick, string? attachEndKey, Point? endOffset)
    {
        var verts = new List<Point>(Viewport._shapeDraftPolyline!);
        var curvedFlags = Viewport._shapeDraftCurvedFlags != null ? new List<bool>(Viewport._shapeDraftCurvedFlags) : new List<bool>();

        // Add the final click if it's not co-located with the previous vertex
        if (verts.Count == 0 || (verts[^1] - lastClick).LengthSquared > 1)
        {
            verts.Add(lastClick);
            curvedFlags.Add(false);
        }
        if (verts.Count < 2) { Cancel(Viewport._lastMouseScreenPoint); return; }
        CommitVertices(verts, Viewport._shapeDraftAttachStartKey, attachEndKey, Viewport._shapeDraftStartOffset, endOffset, curvedFlags);
    }

    private void CommitVertices(
        IReadOnlyList<Point> verts,
        string? attachStart,
        string? attachEnd,
        Point? startOffset,
        Point? endOffset,
        IReadOnlyList<bool>? curvedFlags)
    {
        var shape = Viewport.CreateLinearShapeFromVertices(Viewport._activeShapeTool!, verts, attachStart, attachEnd, startOffset, endOffset, curvedFlags);
        FinalizeShape(shape);
    }

    private void FinalizeShape(RenderBlock shape)
    {
        var blocks = Viewport.Scene.Blocks
            .Select(b => b with { IsSelected = false })
            .Append(shape)
            .ToList();
        Viewport.ApplySceneChange(Viewport.Scene with
        {
            Blocks = blocks,
            SwimLanes = Viewport.Scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList()
        });
        ClearDraftState(deactivateTool: false);
        Viewport.UpdateHoverCursor(Viewport._lastMouseScreenPoint);
        CanvasViewport.ReleaseCapture();
        Viewport.RebuildSnapshot();
        Viewport.RenderNative();
    }

    private void Cancel(Point screen)
    {
        ClearDraftState(deactivateTool: true);
        Viewport.SetTool("Selection");
        Viewport.UpdateHoverCursor(screen);
        CanvasViewport.ReleaseCapture();
        Viewport.RenderNative();
    }

    private void ClearDraftState(bool deactivateTool = false)
    {
        if (deactivateTool)
        {
            Viewport._activeShapeTool = null;
        }
        Viewport._shapeDraftStartWorld = null;
        Viewport._shapeDraftCurrentWorld = null;
        Viewport._shapeDraftPolyline = null;
        Viewport._shapeDraftCurvedFlags = null;
        Viewport._shapeDraftAttachStartKey = null;
        Viewport._shapeDraftAttachEndKey = null;
        Viewport._shapeDraftStartOffset = null;
        Viewport._shapeDraftEndOffset = null;
        Viewport.ResetInteraction();
    }

    /// <summary>
    /// Snaps a world point to the nearest connection anchor if hovering one, or to the block's
    /// center-toward-outline point if hovering anywhere over a block (for linear shapes).
    /// Returns the attach key if the point was snapped to a block.
    /// </summary>
    private Point SnapPoint(Point world, out string? attachKey, out WpfPoint? relativeOffset)
    {
        attachKey = null;
        relativeOffset = null;
        if (!IsLinear) return world;

        // 1. If mouse is inside any block's bounds, immediately attach and project to outline (bypass discrete anchors)
        SceneBlockVisual? hit = null;
        foreach (var block in Viewport._snapshot.Blocks.Reverse<SceneBlockVisual>())
        {
            if (block.Bounds.Contains(world))
            {
                if (block.Block.Kind is BlockKind.Note) continue;
                if (block.Block.Kind == BlockKind.Shape && CanvasViewport.IsLinearShapeTool(block.Block.ShapeType)) continue;
                hit = block;
                break;
            }
        }
        if (hit is not null)
        {
            attachKey = hit.Block.Key;
            var bounds = hit.Bounds;
            
            // Project the exact world position onto the shape's outline
            Point outlinePoint = CanvasDrawingUtils.GetBlockOutlinePoint(hit.Block, bounds, world);
            
            relativeOffset = new WpfPoint(
                Math.Clamp((outlinePoint.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1),
                Math.Clamp((outlinePoint.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1));
            return outlinePoint;
        }

        // 2. Otherwise (mouse is outside), check for tight connection anchor snaps (discrete anchors)
        var anchor = Viewport.HitConnectionAnchor(world);
        if (anchor is not null)
        {
            attachKey = anchor.Block.Block.Key;
            var bounds = anchor.Block.Bounds;
            relativeOffset = new WpfPoint(
                Math.Clamp((anchor.Point.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1),
                Math.Clamp((anchor.Point.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1));
            return anchor.Point;
        }

        return world;
    }
}
