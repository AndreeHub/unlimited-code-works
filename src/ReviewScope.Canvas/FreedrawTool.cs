using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: FreedrawTool.cs
 * Purpose: Freehand ("pencil") drawing. Press-drag-release samples cursor positions into a point
 * list (throttled by a small min-distance so the list stays manageable), then commits a single
 * "freedraw" Shape block whose points are stored in the normalized "points:x,y;..." body format —
 * the same encoding linear shapes use, so a freehand stroke scales when its block is resized and
 * renders through BlockRenderer.DrawFreedraw.
 *
 * The pencil stays active after each stroke (Excalidraw-style) so several strokes can be drawn in a
 * row; press Esc or pick another tool to leave freehand mode. A pure tap (no drag) is ignored.
 */
internal sealed class FreedrawTool : CanvasToolBase
{
    public override string Name => "Freedraw";

    public FreedrawTool(CanvasViewport viewport) : base(viewport) { }

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        Viewport.CaptureSelectedShapeStyle();
        Viewport.ApplySceneChange(CanvasViewport.ClearSelection(Viewport.Scene));
        Viewport._freedrawPoints = new List<WpfPoint> { world };
        Viewport._dragStartScreen = screen;
        Viewport._didMove = false;
        Viewport.Cursor = Cursors.Cross;
        CanvasViewport.SetCapture(Viewport._hwnd);
        Viewport.RenderNative();
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._freedrawPoints is not { Count: > 0 } pts) return;

        // Throttle: only record a new sample once the cursor has moved a couple of pixels (in
        // world units), so dense mouse-move events don't bloat the point list.
        double minDist = Viewport.InvStroke(2f);
        var last = pts[^1];
        double dx = world.X - last.X, dy = world.Y - last.Y;
        if (dx * dx + dy * dy < minDist * minDist) return;

        pts.Add(world);
        Viewport._didMove = true;
        Viewport.RenderNative();
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._freedrawPoints is not { } pts)
            return;

        if (pts.Count == 0 || (pts[^1] - world).LengthSquared > 0.01)
            pts.Add(world);

        Viewport._freedrawPoints = null;
        CanvasViewport.ReleaseCapture();

        // Ignore a bare click (no real stroke); require at least two distinct points.
        if (pts.Count >= 2)
        {
            var shape = Viewport.CreateFreedrawBlock(pts);
            var blocks = Viewport.Scene.Blocks
                .Select(b => b with { IsSelected = false })
                .Append(shape with { IsSelected = true })
                .ToList();
            Viewport.ApplySceneChange(Viewport.Scene with { Blocks = blocks });
            Viewport.RebuildSnapshot();
        }

        // Pencil stays armed for the next stroke.
        Viewport.RenderNative();
    }

    public override void HandleKeyDown(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
        {
            Viewport._freedrawPoints = null;
            CanvasViewport.ReleaseCapture();
            Viewport.RenderNative();
        }
    }

    public override void Deactivate()
    {
        Viewport._freedrawPoints = null;
    }
}
