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
 * Holding Ctrl constrains the cursor position to one of 8 cardinal directions
 * (multiples of 45°) measured from the previous vertex — useful for clean
 * orthogonal / diagonal elbow connectors.
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

        Point snapped = ResolveDraftPoint(world, modifiers, commit: true, out string? attachKey, out WpfPoint? relativeOffset);

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

        Point current = ResolveDraftPoint(world, modifiers, commit: false, out string? attachEnd, out WpfPoint? relativeEndOffset);
        Viewport._shapeDraftCurrentWorld = current;
        Viewport._shapeDraftAttachEndKey = attachEnd;
        Viewport._shapeDraftEndOffset = relativeEndOffset;
        Viewport.RenderNative();
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._activeShapeTool is null) return;
        if (Viewport._shapeDraftStartWorld is null) return;

        Point endWorld = ResolveDraftPoint(world, modifiers, commit: true, out string? attachEndKey, out WpfPoint? relativeEndOffset);

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
                FinalizeShape(shape, keepToolActive: modifiers.HasFlag(ModifierKeys.Shift));
                return;
            }
            CommitSingleShape(endWorld, keepToolActive: modifiers.HasFlag(ModifierKeys.Shift));
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

    private void CommitSingleShape(Point endWorld, bool keepToolActive)
    {
        var shape = Viewport.CreateShapeBlock(Viewport._activeShapeTool!, Viewport._shapeDraftStartWorld!.Value, endWorld);
        FinalizeShape(shape, keepToolActive);
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

    private void FinalizeShape(RenderBlock shape, bool keepToolActive = true)
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
        ClearDraftState(deactivateTool: !keepToolActive);
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
            Viewport.SyncActiveShapeToolDp();
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
    /// When Ctrl is held during linear-shape drawing, snaps <paramref name="world"/> onto
    /// the nearest of 8 rays (multiples of 45°) emanating from the previous vertex —
    /// the last polyline point or the drag start. Outside linear mode, or without Ctrl,
    /// or before any reference vertex exists, the point is returned unchanged.
    /// </summary>
    private Point ApplyAxisConstraint(Point world, ModifierKeys modifiers)
    {
        if (!IsLinear) return world;
        if (!modifiers.HasFlag(ModifierKeys.Control)) return world;

        Point reference;
        if (Viewport._shapeDraftPolyline is { Count: >= 1 } verts) reference = verts[^1];
        else if (Viewport._shapeDraftStartWorld is { } start) reference = start;
        else return world;

        double dx = world.X - reference.X;
        double dy = world.Y - reference.Y;
        if (dx == 0 && dy == 0) return world;

        double step = Math.PI / 4;
        double snapped = Math.Round(Math.Atan2(dy, dx) / step) * step;
        double cos = Math.Cos(snapped);
        double sin = Math.Sin(snapped);
        double proj = Math.Max(0, dx * cos + dy * sin);
        return new Point(reference.X + cos * proj, reference.Y + sin * proj);
    }

    /// <summary>
    /// When Shift or Ctrl is held during linear-shape drawing, looks for the closest point
    /// on any already-placed line/arrow/polyline. If the cursor is within a small screen
    /// threshold, returns that closest point — letting the user start (or end) a new line
    /// directly on an existing one (e.g. branching off it). Returns null otherwise.
    /// </summary>
    private Point? TrySnapToExistingLine(Point world, ModifierKeys modifiers)
    {
        if (!IsLinear) return null;
        if (!modifiers.HasFlag(ModifierKeys.Shift) && !modifiers.HasFlag(ModifierKeys.Control)) return null;

        double threshold = Viewport.InvStroke(10f);
        double bestDistSq = threshold * threshold;
        Point? bestPoint = null;

        var lookup = Viewport._snapshot.Blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var block in Viewport._snapshot.Blocks)
        {
            if (block.Block.Kind != BlockKind.Shape) continue;
            if (!CanvasViewport.IsLinearShapeTool(block.Block.ShapeType)) continue;

            var points = CanvasDrawingUtils.ResolveLinearShapePoints(block.Block, block.Bounds, lookup);
            if (points.Count < 2) continue;

            for (int i = 0; i + 1 < points.Count; i++)
            {
                Point proj = ProjectPointOntoSegment(world, points[i], points[i + 1]);
                double dx = proj.X - world.X;
                double dy = proj.Y - world.Y;
                double distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestPoint = proj;
                }
            }
        }
        return bestPoint;
    }

    private static Point ProjectPointOntoSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return a;
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        return new Point(a.X + t * dx, a.Y + t * dy);
    }

    /// <summary>
    /// Single entry point that resolves a candidate draft vertex. Snap-to-existing-line takes
    /// priority (and bypasses block attachment); otherwise we apply the axis constraint and
    /// the normal block-snap logic.
    /// </summary>
    private Point ResolveDraftPoint(Point world, ModifierKeys modifiers, bool commit,
        out string? attachKey, out WpfPoint? relativeOffset)
    {
        if (TrySnapToExistingLine(world, modifiers) is { } lineSnap)
        {
            attachKey = null;
            relativeOffset = null;
            return lineSnap;
        }
        Point constrained = ApplyAxisConstraint(world, modifiers);
        return SnapPoint(constrained, out attachKey, out relativeOffset, commit: commit, modifiers: modifiers);
    }

    /// <summary>
    /// Finds the first intersection point of the ray from <paramref name="origin"/> in the
    /// direction of <paramref name="through"/> with the axis-aligned rectangle
    /// <paramref name="bounds"/>. Returns null if the ray misses the rect or has zero length.
    /// When <paramref name="origin"/> is inside the rect, returns the exit point instead.
    /// </summary>
    private static Point? IntersectRayWithRect(Point origin, Point through, Rect bounds)
    {
        double dx = through.X - origin.X;
        double dy = through.Y - origin.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return null;

        double tMinX, tMaxX, tMinY, tMaxY;
        if (Math.Abs(dx) > 1e-9)
        {
            double t1 = (bounds.Left - origin.X) / dx;
            double t2 = (bounds.Right - origin.X) / dx;
            tMinX = Math.Min(t1, t2);
            tMaxX = Math.Max(t1, t2);
        }
        else
        {
            if (origin.X < bounds.Left || origin.X > bounds.Right) return null;
            tMinX = double.NegativeInfinity; tMaxX = double.PositiveInfinity;
        }
        if (Math.Abs(dy) > 1e-9)
        {
            double t1 = (bounds.Top - origin.Y) / dy;
            double t2 = (bounds.Bottom - origin.Y) / dy;
            tMinY = Math.Min(t1, t2);
            tMaxY = Math.Max(t1, t2);
        }
        else
        {
            if (origin.Y < bounds.Top || origin.Y > bounds.Bottom) return null;
            tMinY = double.NegativeInfinity; tMaxY = double.PositiveInfinity;
        }

        double tEntry = Math.Max(tMinX, tMinY);
        double tExit = Math.Min(tMaxX, tMaxY);
        if (tEntry > tExit || tExit < 0) return null;
        double t = tEntry >= 0 ? tEntry : tExit;
        return new Point(origin.X + t * dx, origin.Y + t * dy);
    }

    /// <summary>
    /// Resolves the world position for a draft vertex. While the user is still dragging
    /// (<paramref name="commit"/> = false) the raw mouse position is used so the line follows
    /// the cursor freely, even passing behind a target shape. On commit, if the cursor is
    /// inside a shape we project the segment (previous-vertex → cursor) onto that shape's
    /// outline and return the entry point nearest the previous vertex — guaranteeing the
    /// final line stops at the near edge instead of crossing the shape. Discrete connection
    /// anchors (outside the bounds) still snap immediately in both modes.
    /// </summary>
    private Point SnapPoint(Point world, out string? attachKey, out WpfPoint? relativeOffset, bool commit = false, ModifierKeys modifiers = ModifierKeys.None)
    {
        attachKey = null;
        relativeOffset = null;
        if (!IsLinear) return world;

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

            if (!commit)
            {
                // Drag preview: don't snap. Record the attach key (so the shape will commit
                // to that block when released) but leave the endpoint at the raw cursor.
                relativeOffset = new WpfPoint(
                    Math.Clamp((world.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1),
                    Math.Clamp((world.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1));
                return world;
            }

            // Commit: find the previous vertex (last polyline point, or segment start). For
            // the very first click of a fresh draft there is no prior reference, so fall
            // back to the mouse-based projection.
            Point? reference = null;
            if (Viewport._shapeDraftPolyline is { Count: >= 1 } verts)
                reference = verts[^1];
            else if (Viewport._shapeDraftStartWorld is { } start)
                reference = start;

            Point outlinePoint;
            // Ctrl axis-snap: keep the endpoint strictly on the axis ray from the previous
            // vertex by intersecting that ray with the block's bounds rect — avoids the
            // off-axis nudge that center-to-toward outline projection would otherwise cause.
            if (modifiers.HasFlag(ModifierKeys.Control)
                && reference is { } refPt
                && IntersectRayWithRect(refPt, world, bounds) is { } rayHit)
            {
                outlinePoint = rayHit;
            }
            else
            {
                Point toward = reference is { } r && !bounds.Contains(r) ? r : world;
                outlinePoint = CanvasDrawingUtils.GetBlockOutlinePoint(hit.Block, bounds, toward);
            }

            relativeOffset = new WpfPoint(
                Math.Clamp((outlinePoint.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1),
                Math.Clamp((outlinePoint.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1));
            return outlinePoint;
        }

        // Outside any block: snap to discrete connection anchors when close.
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
