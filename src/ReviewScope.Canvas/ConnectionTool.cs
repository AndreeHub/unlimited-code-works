using System.Windows;
using System.Windows.Input;
using ReviewScope.Domain;

namespace ReviewScope.Canvas;

internal sealed class ConnectionTool : CanvasToolBase
{
    public override string Name => "Connection";

    public ConnectionTool(CanvasViewport viewport) : base(viewport) { }

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        var anchorHit = Viewport.HitConnectionAnchor(world);
        if (anchorHit is not null)
        {
            if (Viewport.HitConnectionEndpoint(anchorHit) is { } endpointHit)
            {
                Viewport.BeginConnectionRewire(endpointHit);
                Viewport._dragStartScreen = screen;
                Viewport._didMove = false;
                CanvasViewport.SetCapture(Viewport._hwnd);
                Viewport.RenderNative();
                return;
            }

            Viewport._isDrawingConnection = true;
            Viewport._connectionSourceKey = anchorHit.Block.Block.Key;
            Viewport._connectionSourceAnchorIndex = anchorHit.AnchorIndex;
            Viewport._connectionSourceWorld = anchorHit.Point;
            Viewport._connectionCurrentWorld = anchorHit.Point;
            Viewport._connectionDraftMidPoint = null;
            Viewport._connectionDraftMidPointBends = false;
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.SetCurrentValue(CanvasViewport.SceneProperty, Viewport.ClearConnectionSelection(Viewport.Scene));
            Viewport.RebuildSnapshot();
            CanvasViewport.SetCapture(Viewport._hwnd);
            Viewport.RenderNative();
            return;
        }

        var controlHit = Viewport.HitConnectionControlNode(world);
        if (controlHit is not null)
        {
            Viewport.SelectConnection(controlHit.Connection.Connection.Id, controlHit.Kind);
            Viewport._dragConnectionControlId = controlHit.Connection.Connection.Id;
            Viewport._dragConnectionControlKind = controlHit.Kind;
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.Cursor = Cursors.SizeAll;
            CanvasViewport.SetCapture(Viewport._hwnd);
            return;
        }

        var arrowHit = Viewport.HitConnectionArrow(world);
        if (arrowHit is not null)
        {
            Viewport.SelectConnection(arrowHit.Connection.Id);
            Viewport._dragArrowConnectionId = arrowHit.Connection.Id;
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.Cursor = Cursors.Hand;
            CanvasViewport.SetCapture(Viewport._hwnd);
            return;
        }

        if (Viewport.HitConnectionCurve(world, out _) is { } curveHit)
        {
            Viewport.SelectConnection(curveHit.Connection.Id);
            return;
        }
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._dragConnectionControlId is Guid controlId)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }

            Viewport.MoveConnectionControl(controlId, Viewport._dragConnectionControlKind, world);
            return;
        }

        if (Viewport._dragArrowConnectionId is Guid arrowId)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }

            Viewport.MoveConnectionArrow(arrowId, world);
            return;
        }

        if (Viewport._isDrawingConnection)
        {
            Viewport._connectionCurrentWorld = world;
            Viewport.UpdateConnectionHoverTarget(world);
            Viewport.RenderNative();
        }
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._dragConnectionControlId is not null)
        {
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            Viewport._dragConnectionControlId = null;
            Viewport._dragConnectionControlKind = ConnectionControlNodeKind.None;
            return;
        }

        if (Viewport._dragArrowConnectionId is not null)
        {
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            Viewport._dragArrowConnectionId = null;
            return;
        }

        if (Viewport._isDrawingConnection)
        {
            var anchorHit = Viewport.HitConnectionAnchor(world);
            if (anchorHit is not null && Viewport.TryCompleteConnectionToAnchor(anchorHit))
            {
                Viewport.ClearConnectionDrawingState();
                return;
            }

            var targetBlock = Viewport.HitBlock(world);
            if (targetBlock is not null && Viewport.TryCompleteConnectionToBlock(targetBlock, world))
            {
                Viewport.ClearConnectionDrawingState();
                return;
            }

            Viewport.ClearConnectionDrawingState();
            Viewport.RenderNative();
        }
    }
}
