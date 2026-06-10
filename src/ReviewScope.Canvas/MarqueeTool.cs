using System.Windows;
using System.Windows.Input;

namespace ReviewScope.Canvas;

/*
 * File: MarqueeTool.cs
 * Purpose: Interactive tool for performing marquee (box) selection of multiple items on the canvas.
 * Functions:
 * - HandleLDown: Initiates the marquee operation.
 * - HandleMouseMove: Updates the marquee visual bounds.
 * - HandleLUp: Finalizes selection of items within the box.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal sealed class MarqueeTool : CanvasToolBase
{
    public override string Name => "Marquee";

    public MarqueeTool(CanvasViewport viewport) : base(viewport) { }

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        // Shift/Ctrl marquee appends to the existing selection (Excalidraw-style), so the
        // current selection must survive until the marquee completes.
        bool append = modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift);
        if (!append)
            Viewport.ApplySceneChange(CanvasViewport.ClearSelection(Viewport.Scene));
        Viewport._marqueeStart = screen;
        Viewport._marqueeEnd = screen;
        Viewport._isMarquee = true;
        Viewport._appendMarquee = append;
        Viewport._dragStartScreen = screen;
        Viewport._didMove = false;
        Viewport.Cursor = Cursors.Cross;
        CanvasViewport.SetCapture(Viewport._hwnd);
        Viewport.RenderNative();
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._isMarquee)
        {
            Viewport._marqueeEnd = screen;
            Viewport.RenderNative();
        }
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._isMarquee && Viewport._marqueeStart is Point start)
        {
            Viewport.CompleteMarqueeSelection(start, screen, Viewport._appendMarquee);
            Viewport.ResetInteraction();
            Viewport.UpdateHoverCursor(screen);
            CanvasViewport.ReleaseCapture();
            Viewport.RenderNative();
        }
    }
}
