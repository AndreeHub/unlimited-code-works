using System.Windows;
using System.Windows.Input;

namespace ReviewScope.Canvas;

internal sealed class PanTool : CanvasToolBase
{
    public override string Name => "Pan";

    public PanTool(CanvasViewport viewport) : base(viewport) { }

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        Viewport._panPoint = screen;
        Viewport.Cursor = Cursors.Hand;
        CanvasViewport.SetCapture(Viewport._hwnd);
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._panPoint is Point last)
        {
            double dx = screen.X - last.X;
            double dy = screen.Y - last.Y;
            Viewport.Camera = new CameraState(Viewport.Camera.Zoom, Viewport.Camera.OffsetX + dx, Viewport.Camera.OffsetY + dy);
            Viewport._panPoint = screen;
            Viewport.RenderNative();
        }
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        Viewport._panPoint = null;
        Viewport.UpdateHoverCursor(screen);
        CanvasViewport.ReleaseCapture();
    }
}
