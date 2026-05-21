using System.Windows;
using System.Windows.Input;
using ReviewScope.Domain;

namespace ReviewScope.Canvas;

/*
 * File: SelectionTool.cs
 * Purpose: Interactive tool for selecting, dragging, and resizing blocks and swim lanes on the canvas.
 * Functions:
 * - HandleLDown: Initiates selection, drag, or resize based on the hit object and modifiers.
 * - HandleMouseMove: Updates the position of dragged items or marquee selection bounds.
 * - HandleLUp: Completes a drag or selection operation.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal sealed class SelectionTool : CanvasToolBase
{
    public override string Name => "Selection";

    public SelectionTool(CanvasViewport viewport) : base(viewport) { }

    public override void HandleLDown(Point screen, Point world, ModifierKeys modifiers)
    {
        bool isCtrl = modifiers.HasFlag(ModifierKeys.Control);
        bool isAlt = modifiers.HasFlag(ModifierKeys.Alt);
        bool isShift = modifiers.HasFlag(ModifierKeys.Shift);

        var endpointHit = Viewport.HitLinearShapeEndpoint(world);
        if (endpointHit is not null)
        {
            if (!endpointHit.Block.Block.IsSelected)
            {
                Viewport.ApplySceneChange(CanvasViewport.SetSelection(Viewport.ClearConnectionSelection(Viewport.Scene), new[] { endpointHit.Block.Block.Key }));
                Viewport.RebuildSnapshot();
            }

            Viewport._linearShapeVertexDragKey = endpointHit.Block.Block.Key;
            Viewport._linearShapeVertexDragIndex = endpointHit.VertexIndex;
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.Cursor = Cursors.SizeAll;
            CanvasViewport.SetCapture(Viewport._hwnd);
            Viewport.RenderNative();
            return;
        }

        var hit = Viewport.HitBlock(world);
        if (hit is not null)
        {
            // Restore button on a focused block
            if (hit.Block.Focused is not null && CanvasDrawingUtils.IsInRestoreButton(hit.Bounds, world))
            {
                if (Viewport.RestoreRequestedCommand?.CanExecute(null) == true)
                    Viewport.RestoreRequestedCommand.Execute(new RestoreRequestedArgs(hit.Block));
                return;
            }

            if (isAlt
                && hit.Block.IsSelected
                && hit.Block.Kind is BlockKind.File or BlockKind.Extract
                && hit.Block.Body is not null
                && Viewport.TryHitSymbolToken(hit, world, out var symbolToken))
            {
                if (Viewport.ExtractRequestedCommand?.CanExecute(null) == true)
                    Viewport.ExtractRequestedCommand.Execute(new ExtractRequestedArgs(hit.Block, symbolToken.Line, symbolToken.Column));
                return;
            }

            // Ctrl+click on a code block -> focus that function inside this block
            if (isCtrl && hit.Block.Kind is BlockKind.File or BlockKind.Extract && hit.Block.Body is not null)
            {
                int codeLine = Viewport.WorldToCodeLine(hit, world);
                if (Viewport.FocusRequestedCommand?.CanExecute(null) == true)
                    Viewport.FocusRequestedCommand.Execute(new FocusRequestedArgs(hit.Block, codeLine, 1));
                return;
            }

            // Shift+click = toggle selection
            if (isShift)
            {
                Viewport.ApplySceneChange(CanvasViewport.ToggleSelection(Viewport.Scene, hit.Block.Key));
                return;
            }

            // Select the hit block if not already selected
            if (!hit.Block.IsSelected)
            {
                Viewport.ApplySceneChange(CanvasViewport.SetSelection(Viewport.Scene, new[] { hit.Block.Key }));
                Viewport.RebuildSnapshot();
                hit = Viewport.HitBlock(world);
                if (hit is null) return;
            }

            if (isAlt)
            {
                string duplicateKey = Viewport.DuplicateSelectedBlocksForDrag(hit.Block.Key);
                Viewport.RebuildSnapshot();
                hit = Viewport._snapshot.Blocks.FirstOrDefault(b => b.Block.Key.Equals(duplicateKey, StringComparison.OrdinalIgnoreCase));
                if (hit is null) return;
            }

            // Check corner resize handles (applicable to any selected block except linear shapes)
            if (hit.Block.IsSelected && !CanvasViewport.IsLinearShapeTool(hit.Block.ShapeType))
            {
                var corner = CanvasViewport.HitNoteCorner(hit.Bounds, world);
                if (corner != NoteResizeCorner.None)
                {
                    Viewport._noteResizeKey = hit.Block.Key;
                    Viewport._noteResizeCorner = corner;
                    Viewport._noteResizeWorldPoint = world;
                    Viewport._dragStartScreen = screen;
                    Viewport._didMove = false;
                    Viewport.Cursor = corner is NoteResizeCorner.TopLeft or NoteResizeCorner.BottomRight
                        ? Cursors.SizeNWSE : Cursors.SizeNESW;
                    CanvasViewport.SetCapture(Viewport._hwnd);
                    return;
                }

                if (Viewport.IsInResize(hit.Bounds, world))
                {
                    Viewport._resizeKey = hit.Block.Key;
                    Viewport._resizeWorldPoint = world;
                    Viewport._resizeWidthOnly = Viewport.IsInRightEdgeResize(hit.Bounds, world);
                    Viewport._dragStartScreen = screen;
                    Viewport._didMove = false;
                    Viewport.Cursor = Viewport._resizeWidthOnly ? Cursors.SizeWE : Cursors.SizeNWSE;
                    CanvasViewport.SetCapture(Viewport._hwnd);
                    return;
                }
            }

            // Check swim-lane resize
            var laneHit = Viewport.HitSwimLaneResize(world);
            if (laneHit is not null)
            {
                Viewport._resizeSwimLaneKey = laneHit.Lane.Key;
                Viewport._resizeSwimLaneWorldPoint = world;
                Viewport._dragStartScreen = screen;
                Viewport._didMove = false;
                Viewport.Cursor = Cursors.SizeNWSE;
                CanvasViewport.SetCapture(Viewport._hwnd);
                return;
            }

            // Start drag
            Viewport._primaryDrag = hit.Block.Key;
            Viewport._draggedKeys = CanvasViewport.IsColorGroup(hit.Block)
                ? Viewport.GetGroupDragKeys(hit.Block)
                : Viewport.Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key)
                    .DefaultIfEmpty(hit.Block.Key).ToList();
            Viewport._dragWorldPoint = world;
            Viewport._dragAnchorOffset = new Point(world.X - hit.Block.X, world.Y - hit.Block.Y);
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.Cursor = Cursors.SizeAll;
            CanvasViewport.SetCapture(Viewport._hwnd);
            return;
        }

        // Hit swim-lane?
        var laneBodyHit = Viewport.HitSwimLane(world);
        if (laneBodyHit is not null)
        {
            Viewport.ApplySceneChange(CanvasViewport.SelectSwimLane(Viewport.Scene, laneBodyHit.Lane.Key));
            Viewport.RebuildSnapshot();

            var laneResizeHit = Viewport.HitSwimLaneResize(world);
            if (laneResizeHit is not null)
            {
                Viewport._resizeSwimLaneKey = laneResizeHit.Lane.Key;
                Viewport._resizeSwimLaneWorldPoint = world;
                Viewport._dragStartScreen = screen;
                Viewport._didMove = false;
                Viewport.Cursor = Cursors.SizeNWSE;
                CanvasViewport.SetCapture(Viewport._hwnd);
                return;
            }

            Viewport._primaryDrag = $"lane::{laneBodyHit.Lane.Key}";
            Viewport._dragWorldPoint = world;
            Viewport._dragAnchorOffset = new Point(world.X - laneBodyHit.Lane.X, world.Y - laneBodyHit.Lane.Y);
            Viewport._dragStartScreen = screen;
            Viewport._didMove = false;
            Viewport.Cursor = Cursors.SizeAll;
            CanvasViewport.SetCapture(Viewport._hwnd);
            return;
        }

        // Default: Marquee
        Viewport.ApplySceneChange(CanvasViewport.ClearSelection(Viewport.Scene));
        Viewport._marqueeStart = screen;
        Viewport._marqueeEnd = screen;
        Viewport._isMarquee = true;
        Viewport._appendMarquee = isCtrl;
        Viewport._dragStartScreen = screen;
        Viewport._didMove = false;
        Viewport.Cursor = Cursors.Cross;
        CanvasViewport.SetCapture(Viewport._hwnd);
        Viewport.RenderNative();
    }

    public override void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._linearShapeVertexDragKey is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }
            Viewport.MoveLinearShapeVertex(Viewport._linearShapeVertexDragKey, Viewport._linearShapeVertexDragIndex, world);
            return;
        }

        if (Viewport._noteResizeKey is not null && Viewport._noteResizeWorldPoint is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }
            var delta = world - Viewport._noteResizeWorldPoint.Value;
            Viewport._noteResizeWorldPoint = world;
            Viewport.ResizeNoteCorner(Viewport._noteResizeKey, Viewport._noteResizeCorner, delta.X, delta.Y);
            return;
        }

        if (Viewport._resizeKey is not null && Viewport._resizeWorldPoint is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }
            var delta = world - Viewport._resizeWorldPoint.Value;
            Viewport._resizeWorldPoint = world;
            Viewport.ResizeBlock(Viewport._resizeKey, delta.X, Viewport._resizeWidthOnly ? 0 : delta.Y);
            return;
        }

        if (Viewport._resizeSwimLaneKey is not null && Viewport._resizeSwimLaneWorldPoint is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }
            var delta = world - Viewport._resizeSwimLaneWorldPoint.Value;
            Viewport._resizeSwimLaneWorldPoint = world;
            Viewport.ResizeSwimLane(Viewport._resizeSwimLaneKey, delta.X, delta.Y);
            return;
        }

        if (Viewport._isMarquee && Viewport._marqueeStart is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) >= 4 || Math.Abs(d.Y) >= 4) Viewport._didMove = true;
            }
            Viewport._marqueeEnd = screen;
            Viewport.RenderNative();
            return;
        }

        if (Viewport._primaryDrag is not null && Viewport._dragAnchorOffset is not null)
        {
            if (!Viewport._didMove && Viewport._dragStartScreen is not null)
            {
                var d = screen - Viewport._dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                Viewport._didMove = true;
                Viewport.BeginCoalescedSceneChange();
            }

            double targetX = world.X - Viewport._dragAnchorOffset.Value.X;
            double targetY = world.Y - Viewport._dragAnchorOffset.Value.Y;
            if (Viewport.SnapToGrid)
            {
                double grid = Math.Clamp(Viewport.GridSize, 4, 240);
                targetX = Math.Round(targetX / grid) * grid;
                targetY = Math.Round(targetY / grid) * grid;
            }

            if (Viewport._primaryDrag.StartsWith("lane::", StringComparison.Ordinal))
            {
                string laneKey = Viewport._primaryDrag[6..];
                var lane = Viewport.Scene.SwimLanes.FirstOrDefault(l => l.Key.Equals(laneKey, StringComparison.OrdinalIgnoreCase));
                if (lane is null) return;
                double dx = targetX - lane.X;
                double dy = targetY - lane.Y;
                if (dx != 0 || dy != 0) Viewport.MoveSwimLane(laneKey, dx, dy);
            }
            else
            {
                var anchor = Viewport.Scene.Blocks.FirstOrDefault(b => b.Key.Equals(Viewport._primaryDrag, StringComparison.OrdinalIgnoreCase));
                if (anchor is null) return;
                double dx = targetX - anchor.X;
                double dy = targetY - anchor.Y;
                if (dx != 0 || dy != 0) Viewport.MoveBlocks(Viewport._draggedKeys, dx, dy, applySnap: false);
            }
            return;
        }
    }

    public override void HandleLUp(Point screen, Point world, ModifierKeys modifiers)
    {
        if (Viewport._linearShapeVertexDragKey is not null)
        {
            Viewport._linearShapeVertexDragKey = null;
            Viewport._linearShapeVertexDragIndex = -1;
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            return;
        }

        if (Viewport._noteResizeKey is not null)
        {
            Viewport._noteResizeKey = null;
            Viewport._noteResizeCorner = NoteResizeCorner.None;
            Viewport._noteResizeWorldPoint = null;
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            return;
        }
        if (Viewport._resizeKey is not null)
        {
            Viewport._resizeKey = null;
            Viewport._resizeWorldPoint = null;
            Viewport._resizeWidthOnly = false;
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            return;
        }
        if (Viewport._resizeSwimLaneKey is not null)
        {
            Viewport._resizeSwimLaneKey = null;
            Viewport._resizeSwimLaneWorldPoint = null;
            if (Viewport._didMove) Viewport.CommitCoalescedSceneChange();
            return;
        }
        if (Viewport._isMarquee) { Viewport.CompleteMarquee(); return; }

        if (Viewport._primaryDrag is not null && Viewport._didMove)
        {
            Viewport.GlueNearbyNotes(Viewport._primaryDrag);
            Viewport.CommitCoalescedSceneChange();
        }

        if (Viewport._primaryDrag is not null && !Viewport._didMove)
        {
            var visual = Viewport._snapshot.Blocks.FirstOrDefault(b => b.Block.Key == Viewport._primaryDrag);
            if (visual is not null)
            {
                if (CanvasViewport.IsTextEditableBlock(visual.Block))
                {
                    if (Viewport.IsDoubleClick(visual.Block.Key, screen))
                    {
                        Viewport.BeginNoteEdit(visual.Block, world);
                        return;
                    }
                }
                else if (!CanvasViewport.IsColorGroup(visual.Block) && Viewport.IsDoubleClick(visual.Block.Key, screen) && Viewport.BlockActivatedCommand?.CanExecute(null) == true)
                    Viewport.BlockActivatedCommand.Execute(new BlockActivatedArgs(visual.Block));
                else
                    Viewport.TrackClick(visual.Block.Key, screen);
            }
        }
    }
}
