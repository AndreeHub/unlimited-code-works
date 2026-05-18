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
    private void HandleKeyDown(IntPtr wParam)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64());
        if (_editingNoteKey is not null) { HandleEditModeKey(key); return; }
        if (key == Key.Delete) { DeleteSelected(); return; }
        if (key == Key.D) { DetachSelectedNotes(); return; }
        if (key == Key.F) { FrameAll(); return; }
        if (key == Key.B) { ToggleBackground(); return; }
        if (key == Key.E) { AddOrMoveConnectionArrow(ToWorld(_lastMouseScreenPoint)); return; }
        if ((key == Key.LeftShift || key == Key.RightShift) && _isDrawingConnection)
        {
            _connectionDraftMidPoint = ToWorld(_lastMouseScreenPoint);
            _connectionDraftMidPointBends = true;
            RenderNative();
            return;
        }
        if (key == Key.W)
        {
            Point world = ToWorld(_lastMouseScreenPoint);
            var hit = HitBlock(world);
            if (hit is null)
            {
                if (AnnotationRequestedCommand?.CanExecute(null) == true)
                    AnnotationRequestedCommand.Execute(new AnnotationRequestedArgs(null, world.X, world.Y));
                return;
            }
        }
        if (key == Key.Q && _isDrawingConnection) return;
        if (key == Key.Q)
        {
            Point world = ToWorld(_lastMouseScreenPoint);
            var hit = HitBlock(world);
            if (hit?.Block.Kind == BlockKind.Note)
            {
                ToggleNoteGlue(hit.Block.Key);
                return;
            }
        }
        if (key == Key.Return)
        {
            var selected = Scene.Blocks.FirstOrDefault(b => b.IsSelected && b.Kind == BlockKind.Note);
            if (selected is not null)
            {
                var vis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == selected.Key);
                if (vis is not null) BeginNoteEdit(vis.Block);
            }
            return;
        }
        if (key == Key.LeftCtrl || key == Key.RightCtrl) { _isFocusMode = true; RenderNative(); }
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = true; RenderNative(); }
    }

    private void HandleKeyUp(IntPtr wParam)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64());
        if (key == Key.LeftCtrl || key == Key.RightCtrl) { _isFocusMode = false; _extractHoverBlock = null; RenderNative(); }
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = false; _extractHoverBlock = null; RenderNative(); }
        if (key == Key.Escape) { ClearConnectionDrawingState(); RenderNative(); }
    }

    private void HandleLDown(Point screen)
    {
        Focus(); SetFocus(_hwnd);

        if (_editingNoteKey is not null)
        {
            Point worldEd = ToWorld(screen);
            var editVis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _editingNoteKey);
            if (editVis is null || !editVis.Bounds.Contains(worldEd))
            {
                CommitNoteEdit(save: true);
                // fall through to handle the click normally
            }
            else
            {
                _editingTitle = false;
                int newPos = HitTestCursorPos(
                    _editBody, 11f,
                    (float)editVis.Bounds.X + 10,
                    (float)editVis.Bounds.Y + 8,
                    (float)editVis.Bounds.Width - 20,
                    worldEd,
                    wrap: true);
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (shift)
                {
                    if (_editSelectionAnchor < 0) _editSelectionAnchor = _editCursorPos;
                }
                else
                {
                    _editSelectionAnchor = newPos;
                }
                _editCursorPos = newPos;
                _editMouseSelecting = true;
                SetCapture(_hwnd);
                _editCursorVisible = true;
                RenderNative();
                return;
            }
        }

        if (TryHandleMinimapClick(screen)) return;

        Point world = ToWorld(screen);

        var anchorHit = HitConnectionAnchor(world);
        if (anchorHit is not null)
        {
            if (_isDrawingConnection)
            {
                if (anchorHit.Block.Block.Key != _connectionSourceKey)
                {
                    if (_rewireConnectionId is not null)
                    {
                        CompleteConnectionRewire(anchorHit);
                    }
                    else if (ConnectionDrawnCommand?.CanExecute(null) == true)
                    {
                        ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(
                            _connectionSourceKey!,
                            anchorHit.Block.Block.Key,
                            _connectionSourceAnchorIndex,
                            anchorHit.AnchorIndex,
                            _connectionDraftMidPoint?.X,
                            _connectionDraftMidPoint?.Y,
                            _connectionDraftMidPointBends));
                    }
                }

                ClearConnectionDrawingState();
                RenderNative();
                return;
            }

            if (HitConnectionEndpoint(anchorHit) is { } endpointHit)
            {
                BeginConnectionRewire(endpointHit);
                _dragStartScreen = screen;
                _didMove = false;
                SetCapture(_hwnd);
                RenderNative();
                return;
            }

            _isDrawingConnection = true;
            _connectionSourceKey = anchorHit.Block.Block.Key;
            _connectionSourceAnchorIndex = anchorHit.AnchorIndex;
            _connectionSourceWorld = anchorHit.Point;
            _connectionCurrentWorld = anchorHit.Point;
            _connectionDraftMidPoint = null;
            _connectionDraftMidPointBends = false;
            _dragStartScreen = screen;
            _didMove = false;
            SetCurrentValue(SceneProperty, ClearConnectionSelection(Scene));
            RebuildSnapshot();
            SetCapture(_hwnd);
            RenderNative();
            return;
        }

        var controlHit = HitConnectionControlNode(world);
        if (controlHit is not null)
        {
            SelectConnection(controlHit.Connection.Connection.Id, controlHit.Kind);
            _dragConnectionControlId = controlHit.Connection.Connection.Id;
            _dragConnectionControlKind = controlHit.Kind;
            _dragStartScreen = screen;
            _didMove = false;
            Cursor = Cursors.SizeAll;
            SetCapture(_hwnd);
            return;
        }

        var arrowHit = HitConnectionArrow(world);
        if (arrowHit is not null)
        {
            SelectConnection(arrowHit.Connection.Id);
            _dragArrowConnectionId = arrowHit.Connection.Id;
            _dragStartScreen = screen;
            _didMove = false;
            Cursor = Cursors.Hand;
            SetCapture(_hwnd);
            return;
        }

        // Draw-connection mode: click to end connection
        if (_isDrawingConnection)
        {
            ClearConnectionDrawingState();
            RenderNative();
            return;
        }

        if (HitConnectionCurve(world, out _) is { } curveHit)
        {
            SelectConnection(curveHit.Connection.Id);
            return;
        }

        var hit = HitBlock(world);
        bool isCtrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool isAlt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool isShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (hit is not null)
        {
            // Restore button on a focused block
            if (hit.Block.Focused is not null && IsInRestoreButton(hit.Bounds, world))
            {
                if (RestoreRequestedCommand?.CanExecute(null) == true)
                    RestoreRequestedCommand.Execute(new RestoreRequestedArgs(hit.Block));
                return;
            }

            // Ctrl+click on a code block â†’ focus that function inside this block
            if (isCtrl && hit.Block.Kind is BlockKind.File or BlockKind.Extract && hit.Block.Body is not null)
            {
                int codeLine = WorldToCodeLine(hit, world);
                if (FocusRequestedCommand?.CanExecute(null) == true)
                    FocusRequestedCommand.Execute(new FocusRequestedArgs(hit.Block, codeLine, 1));
                return;
            }

            // Alt+click on a code block â†’ extract function to new block
            if (isAlt && hit.Block.Kind is BlockKind.File or BlockKind.Extract && hit.Block.Body is not null)
            {
                int codeLine = WorldToCodeLine(hit, world);
                if (ExtractRequestedCommand?.CanExecute(null) == true)
                    ExtractRequestedCommand.Execute(new ExtractRequestedArgs(hit.Block, codeLine, 1));
                return;
            }

            // Shift+click = toggle selection (was Ctrl previously, now Ctrl is reserved for focus)
            if (isShift)
            {
                ApplySceneChange(ClearConnectionSelection(ToggleSelection(Scene, hit.Block.Key)));
                return;
            }

            bool wasAlreadySelected = hit.Block.IsSelected;

            // Select the hit block if not already selected
            if (!hit.Block.IsSelected)
            {
                ApplySceneChange(ClearConnectionSelection(SetSelection(Scene, new[] { hit.Block.Key })));
                RebuildSnapshot();
                hit = HitBlock(world);
                if (hit is null) return;
            }

            // Check note corner resize handles
            if (hit.Block.Kind == BlockKind.Note)
            {
                var corner = HitNoteCorner(hit.Bounds, world);
                if (corner != NoteResizeCorner.None)
                {
                    _noteResizeKey = hit.Block.Key;
                    _noteResizeCorner = corner;
                    _noteResizeWorldPoint = world;
                    _dragStartScreen = screen;
                    _didMove = false;
                    Cursor = corner is NoteResizeCorner.TopLeft or NoteResizeCorner.BottomRight
                        ? Cursors.SizeNWSE : Cursors.SizeNESW;
                    SetCapture(_hwnd);
                    return;
                }
                // If note was already selected, clicking inside enters edit mode
                if (wasAlreadySelected)
                {
                    BeginNoteEdit(hit.Block, world);
                    return;
                }
            }

            // Check resize handle
            if (hit.Block.Kind is BlockKind.File or BlockKind.Extract && IsInResize(hit.Bounds, world))
            {
                _resizeKey = hit.Block.Key;
                _resizeWorldPoint = world;
                _resizeWidthOnly = IsInRightEdgeResize(hit.Bounds, world);
                _dragStartScreen = screen;
                _didMove = false;
                Cursor = _resizeWidthOnly ? Cursors.SizeWE : Cursors.SizeNWSE;
                SetCapture(_hwnd);
                return;
            }

            // Check swim-lane resize
            var laneHit = HitSwimLaneResize(world);
            if (laneHit is not null)
            {
                _resizeSwimLaneKey = laneHit.Lane.Key;
                _resizeSwimLaneWorldPoint = world;
                _dragStartScreen = screen;
                _didMove = false;
                Cursor = Cursors.SizeNWSE;
                SetCapture(_hwnd);
                return;
            }

            // Start drag
            _primaryDrag = hit.Block.Key;
            _draggedKeys = Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key)
                .DefaultIfEmpty(hit.Block.Key).ToList();
            _dragWorldPoint = world;
            _dragStartScreen = screen;
            _didMove = false;
            Cursor = Cursors.SizeAll;
            SetCapture(_hwnd);
            return;
        }

        // Hit swim-lane?
        var laneBodyHit = HitSwimLane(world);
        if (laneBodyHit is not null)
        {
            // Select the swim-lane
            ApplySceneChange(ClearConnectionSelection(SelectSwimLane(Scene, laneBodyHit.Lane.Key)));
            RebuildSnapshot();

            // Check resize on swim lane
            var laneResizeHit = HitSwimLaneResize(world);
            if (laneResizeHit is not null)
            {
                _resizeSwimLaneKey = laneResizeHit.Lane.Key;
                _resizeSwimLaneWorldPoint = world;
                Cursor = Cursors.SizeNWSE;
                SetCapture(_hwnd);
                return;
            }

            // Start drag of swim-lane
            _primaryDrag = $"lane::{laneBodyHit.Lane.Key}";
            _dragWorldPoint = world;
            _dragStartScreen = screen;
            _didMove = false;
            Cursor = Cursors.SizeAll;
            SetCapture(_hwnd);
            return;
        }

        // Marquee or pan
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _panPoint = screen;
            Cursor = Cursors.Hand;
            SetCapture(_hwnd);
            return;
        }

        ApplySceneChange(ClearConnectionSelection(ClearSelection(Scene)));
        _marqueeStart = screen;
        _marqueeEnd = screen;
        _isMarquee = true;
        _appendMarquee = isCtrl;
        _dragStartScreen = screen;
        _didMove = false;
        Cursor = Cursors.Cross;
        SetCapture(_hwnd);
        RenderNative();
    }

    private void HandleLUp(Point screen)
    {
        if (_editMouseSelecting)
        {
            _editMouseSelecting = false;
            if (_editSelectionAnchor == _editCursorPos) _editSelectionAnchor = -1;
            ReleaseCapture();
            RenderNative();
            return;
        }
        if (_isDrawingConnection)
        {
            Point world = ToWorld(screen);
            var anchorHit = HitConnectionAnchor(world);
            if (anchorHit is not null && anchorHit.Block.Block.Key != _connectionSourceKey)
            {
                if (_rewireConnectionId is not null)
                {
                    CompleteConnectionRewire(anchorHit);
                }
                else if (ConnectionDrawnCommand?.CanExecute(null) == true)
                {
                    ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(
                        _connectionSourceKey!,
                        anchorHit.Block.Block.Key,
                        _connectionSourceAnchorIndex,
                        anchorHit.AnchorIndex,
                        _connectionDraftMidPoint?.X,
                        _connectionDraftMidPoint?.Y,
                        _connectionDraftMidPointBends));
                }
                ClearConnectionDrawingState();
                ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
            }

            if (!_didMove)
            {
                ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
            }

            ClearConnectionDrawingState();
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_dragConnectionControlId is Guid controlId)
        {
            _dragConnectionControlId = null;
            _dragConnectionControlKind = ConnectionControlNodeKind.None;
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_dragArrowConnectionId is Guid arrowId)
        {
            if (!_didMove)
                ToggleConnectionArrow(arrowId);
            _dragArrowConnectionId = null;
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_noteResizeKey is not null)
        {
            _noteResizeKey = null;
            _noteResizeCorner = NoteResizeCorner.None;
            _noteResizeWorldPoint = null;
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_resizeKey is not null)
        {
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_resizeSwimLaneKey is not null)
        {
            ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return;
        }
        if (_isMarquee) { CompleteMarquee(); ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture(); return; }

        if (_primaryDrag is not null && _didMove)
            GlueNearbyNotes(_primaryDrag);

        if (_primaryDrag is not null && !_didMove)
        {
            var visual = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _primaryDrag);
            if (visual is not null)
            {
                if (visual.Block.Kind == BlockKind.Note)
                {
                    if (IsDoubleClick(visual.Block.Key, screen))
                    {
                        BeginNoteEdit(visual.Block, ToWorld(screen));
                        ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture();
                        return;
                    }
                    // Single click: IsDoubleClick already tracked the click
                }
                else if (IsDoubleClick(visual.Block.Key, screen) && BlockActivatedCommand?.CanExecute(null) == true)
                    BlockActivatedCommand.Execute(new BlockActivatedArgs(visual.Block));
                else
                    TrackClick(visual.Block.Key, screen);
            }
        }

        ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture();
    }

    private void HandleRDown(Point screen)
    {
        Focus(); SetFocus(_hwnd);
        Point world = ToWorld(screen);
        var anchorHit = HitConnectionAnchor(world);
        if (anchorHit is not null && !_isDrawingConnection)
        {
            if (HitConnectionEndpoint(anchorHit) is { } endpointHit)
            {
                BeginConnectionRewire(endpointHit);
                _dragStartScreen = screen;
                _didMove = false;
                SetCapture(_hwnd);
                RenderNative();
                return;
            }

            _isDrawingConnection = true;
            _connectionSourceKey = anchorHit.Block.Block.Key;
            _connectionSourceAnchorIndex = anchorHit.AnchorIndex;
            _connectionSourceWorld = anchorHit.Point;
            _connectionCurrentWorld = anchorHit.Point;
            _connectionDraftMidPoint = null;
            _connectionDraftMidPointBends = false;
            _dragStartScreen = screen;
            _didMove = false;
            SetCapture(_hwnd);
            RenderNative();
        }
    }

    private void HandleRUp(Point screen)
    {
        if (_isDrawingConnection)
        {
            Point world = ToWorld(screen);
            var anchorHit = HitConnectionAnchor(world);
            if (anchorHit is not null && anchorHit.Block.Block.Key != _connectionSourceKey)
            {
                if (_rewireConnectionId is not null)
                {
                    CompleteConnectionRewire(anchorHit);
                }
                else if (ConnectionDrawnCommand?.CanExecute(null) == true)
                {
                    ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(
                        _connectionSourceKey!,
                        anchorHit.Block.Block.Key,
                        _connectionSourceAnchorIndex,
                        anchorHit.AnchorIndex,
                        _connectionDraftMidPoint?.X,
                        _connectionDraftMidPoint?.Y,
                        _connectionDraftMidPointBends));
                }
            }
            ClearConnectionDrawingState();
            ReleaseCapture();
            RenderNative();
        }
    }

    private void HandleMove(Point screen)
    {
        _lastMouseScreenPoint = screen;
        if (_isMinimapDrag) { UpdateCameraFromMinimap(screen); return; }

        if (_editMouseSelecting && _editingNoteKey is not null)
        {
            Point wp = ToWorld(screen);
            var ev = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _editingNoteKey);
            if (ev is not null)
            {
                _editCursorPos = HitTestCursorPos(
                    _editBody, 11f,
                    (float)ev.Bounds.X + 10, (float)ev.Bounds.Y + 8,
                    (float)ev.Bounds.Width - 20, wp,
                    wrap: true);
                _editCursorVisible = true;
                RenderNative();
            }
            return;
        }

        Point world = ToWorld(screen);

        if (_isDrawingConnection)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) >= 4 || Math.Abs(d.Y) >= 4) _didMove = true;
            }
            _connectionCurrentWorld = world;
            UpdateConnectionHoverTarget(world);
            RenderNative();
            return;
        }

        if (_dragConnectionControlId is Guid controlId)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            MoveConnectionControl(controlId, _dragConnectionControlKind, world);
            return;
        }

        if (_dragArrowConnectionId is Guid arrowId)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            MoveConnectionArrow(arrowId, world);
            return;
        }

        if (_noteResizeKey is not null && _noteResizeWorldPoint is not null)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            var delta = world - _noteResizeWorldPoint.Value;
            _noteResizeWorldPoint = world;
            ResizeNoteCorner(_noteResizeKey, _noteResizeCorner, delta.X, delta.Y);
            return;
        }

        if (_resizeKey is not null && _resizeWorldPoint is not null)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            var delta = world - _resizeWorldPoint.Value;
            _resizeWorldPoint = world;
            ResizeBlock(_resizeKey, delta.X, _resizeWidthOnly ? 0 : delta.Y);
            return;
        }

        if (_resizeSwimLaneKey is not null && _resizeSwimLaneWorldPoint is not null)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            var delta = world - _resizeSwimLaneWorldPoint.Value;
            _resizeSwimLaneWorldPoint = world;
            ResizeSwimLane(_resizeSwimLaneKey, delta.X, delta.Y);
            return;
        }

        if (_isMarquee && _marqueeStart is not null)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) >= 4 || Math.Abs(d.Y) >= 4) _didMove = true;
            }
            _marqueeEnd = screen;
            RenderNative();
            return;
        }

        if (_panPoint is not null)
        {
            var delta = screen - _panPoint.Value;
            _panPoint = screen;
            _camera = _camera with { OffsetX = _camera.OffsetX + delta.X, OffsetY = _camera.OffsetY + delta.Y };
            SetCurrentValue(CameraProperty, _camera);
            _visDirty = true;
            RenderNative();
            return;
        }

        if (_primaryDrag is not null && _dragWorldPoint is not null)
        {
            if (!_didMove && _dragStartScreen is not null)
            {
                var d = screen - _dragStartScreen.Value;
                if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
                _didMove = true;
            }
            var delta = world - _dragWorldPoint.Value;
            _dragWorldPoint = world;

            if (_primaryDrag.StartsWith("lane::", StringComparison.Ordinal))
            {
                string laneKey = _primaryDrag[6..];
                MoveSwimLane(laneKey, delta.X, delta.Y);
            }
            else
            {
                MoveBlocks(_draggedKeys, delta.X, delta.Y);
            }
            return;
        }

        // Focus/Extract-mode hover: highlight function scope under cursor
        if (_isFocusMode || _isExtractMode)
        {
            var hoverBlock = HitBlock(world);
            if (hoverBlock?.Block.Kind is BlockKind.File or BlockKind.Extract && hoverBlock.Block.Body is not null)
            {
                int codeLine = WorldToCodeLine(hoverBlock, world);
                if (_extractHoverBlock?.Block.Key != hoverBlock.Block.Key || _extractHoverLine != codeLine)
                {
                    _extractHoverBlock = hoverBlock;
                    _extractHoverLine = codeLine;
                    _extractHoverStartLine = codeLine;
                    _extractHoverEndLine = codeLine;
                    RenderNative();
                }
            }
            else
            {
                if (_extractHoverBlock is not null) { _extractHoverBlock = null; RenderNative(); }
            }
        }

        UpdateHoverCursor(screen);
    }

    private void HandleWheel(Point screen, int delta)
    {
        if (TryScrollCodeBlock(screen, delta))
            return;

        Point anchor = ToWorld(screen);
        double factor = delta > 0 ? 1.08 : 0.92;
        double nextZoom = Math.Clamp(_camera.Zoom * factor, 0.02, 8.0);
        _camera = new CameraState(nextZoom,
            screen.X - anchor.X * nextZoom,
            screen.Y - anchor.Y * nextZoom);
        SetCurrentValue(CameraProperty, _camera);
        _visDirty = true;
        RenderNative();
    }

    private bool TryScrollCodeBlock(Point screen, int delta)
    {
        Point world = ToWorld(screen);
        var hit = HitBlock(world);
        if (hit is null || hit.Block.Body is null || hit.Block.Kind is not (BlockKind.File or BlockKind.Extract))
            return false;

        Rect bodyRect = GetBodyRect(hit.Bounds);
        if (!bodyRect.Contains(world))
            return false;

        int maxScroll = GetMaxCodeScrollLines(hit.Block, bodyRect);
        if (maxScroll <= 0)
            return false;

        _codeScrollLines.TryGetValue(hit.Block.Key, out int current);
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 12 : 4;
        int next = Math.Clamp(current + (delta < 0 ? step : -step), 0, maxScroll);
        if (next == current)
            return true;

        _codeScrollLines[hit.Block.Key] = next;
        RenderNative();
        return true;
    }

    // -----------------------------------------------------------------------
    // Scene mutation helpers
    // -----------------------------------------------------------------------
    private void MoveBlocks(IReadOnlyList<string> keys, double dx, double dy)
    {
        if (keys.Count == 0) return;
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string attachedNoteKey in GetAttachedNoteKeys(set))
            set.Add(attachedNoteKey);
        var blocks = Scene.Blocks
            .Select(b => set.Contains(b.Key) ? b with { X = b.X + dx, Y = b.Y + dy } : b)
            .ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    private void ResizeBlock(string key, double dw, double dh)
    {
        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return b;
            double width = Math.Max(MinBlockW, b.Width + dw);
            double height = Math.Max(MinBlockH, b.Height + dh);
            return b with
            {
                Width = width,
                Height = height
            };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    private void MoveSwimLane(string key, double dx, double dy)
    {
        var lanes = Scene.SwimLanes
            .Select(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? l with { X = l.X + dx, Y = l.Y + dy } : l)
            .ToList();
        ApplySceneChange(Scene with { SwimLanes = lanes });
        RebuildSnapshot(); RenderNative();
    }

    private void ResizeSwimLane(string key, double dw, double dh)
    {
        var lanes = Scene.SwimLanes.Select(l =>
        {
            if (!l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return l;
            return l with { Width = Math.Max(200, l.Width + dw), Height = Math.Max(120, l.Height + dh) };
        }).ToList();
        ApplySceneChange(Scene with { SwimLanes = lanes });
        RebuildSnapshot(); RenderNative();
    }

    private void DeleteSelected()
    {
        if (_selectedConnectionId is not null)
        {
            DeleteSelectedConnections();
            return;
        }

        var selectedKeys = Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedLaneKeys = Scene.SwimLanes.Where(l => l.IsSelected).Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0 && selectedLaneKeys.Count == 0) return;
        var blocks = Scene.Blocks.Where(b => !selectedKeys.Contains(b.Key)).ToList();
        var connections = Scene.Connections
            .Where(c => !selectedKeys.Contains(c.SourceKey) && !selectedKeys.Contains(c.TargetKey))
            .ToList();
        var lanes = Scene.SwimLanes.Where(l => !selectedLaneKeys.Contains(l.Key)).ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks, Connections = connections, SwimLanes = lanes }));
        RebuildSnapshot(); RenderNative();
    }

    private IEnumerable<string> GetAttachedNoteKeys(ISet<string> movingKeys)
    {
        foreach (var connection in Scene.Connections)
        {
            if (connection.Label != "__note" || !movingKeys.Contains(connection.SourceKey)) continue;
            var note = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(connection.TargetKey, StringComparison.OrdinalIgnoreCase));
            if (note?.Kind == BlockKind.Note)
                yield return note.Key;
        }
    }

    private void GlueNearbyNotes(string movedKey)
    {
        var moved = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(movedKey, StringComparison.OrdinalIgnoreCase));
        if (moved is null) return;

        if (moved.Kind == BlockKind.Note)
        {
            var nearest = Scene.Blocks
                .Where(b => b.Kind != BlockKind.Note)
                .Select(b => new { Block = b, Distance = DistanceBetween(moved, b) })
                .Where(x => x.Distance <= 42)
                .OrderBy(x => x.Distance)
                .FirstOrDefault();
            if (nearest is not null)
                AttachNote(nearest.Block.Key, moved.Key);
            return;
        }

        foreach (var note in Scene.Blocks.Where(b => b.Kind == BlockKind.Note && DistanceBetween(moved, b) <= 42))
            AttachNote(moved.Key, note.Key);
    }

    private void AttachNote(string sourceKey, string noteKey)
    {
        bool exists = Scene.Connections.Any(c => c.Label == "__note"
            && c.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
            && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase));
        if (exists) return;

        var connections = Scene.Connections
            .Where(c => !(c.Label == "__note" && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase)))
            .Append(new RenderConnection(Guid.NewGuid(), sourceKey, noteKey, "__note"))
            .ToList();
        ApplySceneChange(Scene with { Connections = connections });
    }

    private void DetachSelectedNotes()
    {
        var selectedNotes = Scene.Blocks.Where(b => b.IsSelected && b.Kind == BlockKind.Note).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedNotes.Count == 0) return;
        var connections = Scene.Connections.Where(c => c.Label != "__note" || !selectedNotes.Contains(c.TargetKey)).ToList();
        ApplySceneChange(Scene with { Connections = connections });
        RebuildSnapshot(); RenderNative();
    }

    private void DetachNote(string noteKey)
    {
        var connections = Scene.Connections.Where(c => c.Label != "__note" || !c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase)).ToList();
        ApplySceneChange(Scene with { Connections = connections });
        RebuildSnapshot(); RenderNative();
    }

    private static NoteResizeCorner HitNoteCorner(Rect bounds, Point world)
    {
        const double hs = NoteCornerHandleSize;
        if (new Rect(bounds.X, bounds.Y, hs, hs).Contains(world)) return NoteResizeCorner.TopLeft;
        if (new Rect(bounds.Right - hs, bounds.Y, hs, hs).Contains(world)) return NoteResizeCorner.TopRight;
        if (new Rect(bounds.X, bounds.Bottom - hs, hs, hs).Contains(world)) return NoteResizeCorner.BottomLeft;
        if (new Rect(bounds.Right - hs, bounds.Bottom - hs, hs, hs).Contains(world)) return NoteResizeCorner.BottomRight;
        return NoteResizeCorner.None;
    }

    private void ResizeNoteCorner(string key, NoteResizeCorner corner, double dx, double dy)
    {
        const double minW = 160, minH = 90;
        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return b;
            double x = b.X, y = b.Y, w = b.Width, h = b.Height;
            switch (corner)
            {
                case NoteResizeCorner.TopLeft:
                    { double nw = Math.Max(minW, w - dx); double nh = Math.Max(minH, h - dy); x += w - nw; y += h - nh; w = nw; h = nh; } break;
                case NoteResizeCorner.TopRight:
                    { double nh = Math.Max(minH, h - dy); y += h - nh; w = Math.Max(minW, w + dx); h = nh; } break;
                case NoteResizeCorner.BottomLeft:
                    { double nw = Math.Max(minW, w - dx); x += w - nw; w = nw; h = Math.Max(minH, h + dy); } break;
                case NoteResizeCorner.BottomRight:
                    w = Math.Max(minW, w + dx); h = Math.Max(minH, h + dy); break;
            }
            return b with { X = x, Y = y, Width = w, Height = h };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    // -----------------------------------------------------------------------
    // In-canvas note editing
    // -----------------------------------------------------------------------
    private void BeginNoteEdit(RenderBlock block, Point? clickWorld = null)
    {
        _editingNoteKey = block.Key;
        _editTitle = block.Title;
        _editBody = block.Body ?? string.Empty;
        _editSelectionAnchor = -1;
        _editMouseSelecting = false;

        _editingTitle = false;
        var vis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == block.Key);
        if (clickWorld is Point cw && vis is not null)
        {
            _editCursorPos = HitTestCursorPos(
                _editBody, 11f,
                (float)vis.Bounds.X + 10,
                (float)vis.Bounds.Y + 8,
                (float)vis.Bounds.Width - 20,
                cw,
                wrap: true);
        }
        else
        {
            _editCursorPos = _editBody.Length;
        }
        _editCursorVisible = true;
        _cursorBlinkTimer?.Dispose();
        _cursorBlinkTimer = new System.Threading.Timer(_ =>
        {
            _editCursorVisible = !_editCursorVisible;
            Dispatcher.BeginInvoke(new Action(RenderNative));
        }, null, 530, 530);
        ApplySceneChange(SetSelection(Scene, new[] { block.Key }));
        RebuildSnapshot();
        RenderNative();
    }

    private void CommitNoteEdit(bool save)
    {
        _cursorBlinkTimer?.Dispose();
        _cursorBlinkTimer = null;
        string? key = _editingNoteKey;
        _editingNoteKey = null;
        _editCursorVisible = false;
        _editSelectionAnchor = -1;
        _editMouseSelecting = false;

        if (save && key is not null)
        {
            var noteBlock = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (noteBlock is not null)
            {
                var blocks = Scene.Blocks.Select(b =>
                    b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                        ? b with { Body = _editBody }
                        : b).ToList();
                var annotations = Scene.Annotations.Select(a =>
                    a.Id == noteBlock.Id ? a with { Content = _editBody } : a).ToList();
                ApplySceneChange(Scene with { Blocks = blocks, Annotations = annotations });
                RebuildSnapshot();
                return;
            }
        }
        RenderNative();
    }

    private void HandleEditModeKey(Key key)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        string text = _editingTitle ? _editTitle : _editBody;

        // Clipboard / select-all shortcuts
        if (ctrl && key == Key.A)
        {
            _editSelectionAnchor = 0;
            _editCursorPos = text.Length;
            _editCursorVisible = true; RenderNative(); return;
        }
        if (ctrl && (key == Key.C || key == Key.X))
        {
            var selCx = GetEditSelection();
            if (selCx is not null)
            {
                int s = selCx.Value.Item1, e = selCx.Value.Item2;
                try { System.Windows.Clipboard.SetText(text.Substring(s, e - s)); } catch { }
                if (key == Key.X) DeleteEditSelection();
            }
            _editCursorVisible = true; RenderNative(); return;
        }
        if (ctrl && key == Key.V)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string clip = System.Windows.Clipboard.GetText();
                    if (_editingTitle) clip = clip.Replace("\r", "").Replace("\n", " ");
                    else clip = clip.Replace("\r\n", "\n").Replace("\r", "\n");
                    InsertEditText(clip);
                }
            }
            catch { }
            _editCursorVisible = true; RenderNative(); return;
        }

        switch (key)
        {
            case Key.Escape: CommitNoteEdit(save: false); return;
            case Key.Back:
                if (!DeleteEditSelection() && _editCursorPos > 0)
                {
                    if (_editingTitle) _editTitle = _editTitle.Remove(_editCursorPos - 1, 1);
                    else _editBody = _editBody.Remove(_editCursorPos - 1, 1);
                    _editCursorPos--;
                }
                _editSelectionAnchor = -1;
                break;
            case Key.Delete:
                if (!DeleteEditSelection() && _editCursorPos < text.Length)
                {
                    if (_editingTitle) _editTitle = _editTitle.Remove(_editCursorPos, 1);
                    else _editBody = _editBody.Remove(_editCursorPos, 1);
                }
                _editSelectionAnchor = -1;
                break;
            case Key.Left:
                UpdateSelectionAnchor(shift);
                if (!shift && GetEditSelection() is { } selL) _editCursorPos = selL.Item1;
                else if (_editCursorPos > 0) _editCursorPos--;
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.Right:
                UpdateSelectionAnchor(shift);
                if (!shift && GetEditSelection() is { } selR) _editCursorPos = selR.Item2;
                else if (_editCursorPos < text.Length) _editCursorPos++;
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.Home:
                UpdateSelectionAnchor(shift);
                _editCursorPos = LineStart(text, _editCursorPos);
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.End:
                UpdateSelectionAnchor(shift);
                _editCursorPos = LineEnd(text, _editCursorPos);
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.Up:
                UpdateSelectionAnchor(shift);
                _editCursorPos = MoveLine(text, _editCursorPos, -1);
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.Down:
                UpdateSelectionAnchor(shift);
                _editCursorPos = MoveLine(text, _editCursorPos, +1);
                if (!shift) _editSelectionAnchor = -1;
                break;
            case Key.Tab:
                InsertEditText(_editingTitle ? "    " : "\t");
                break;
            // All other keys: character input handled by WM_CHAR
        }
        _editCursorVisible = true;
        RenderNative();
    }

    private void HandleChar(char c)
    {
        if (_editingNoteKey is null) return;
        if (c == '\b' || c == 27 || c == '\n' || c == '\t') return; // handled elsewhere
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return; // ctrl combos handled by HandleEditModeKey
        if (c == '\r')
        {
            // Title is single-line; Enter only inserts newline in body
            if (!_editingTitle) InsertEditText("\n");
        }
        else if (c >= 32)
        {
            InsertEditText(c.ToString());
        }
        _editCursorVisible = true;
        RenderNative();
    }

    private (int, int)? GetEditSelection()
    {
        if (_editSelectionAnchor < 0 || _editSelectionAnchor == _editCursorPos) return null;
        int a = _editSelectionAnchor, b = _editCursorPos;
        return a < b ? (a, b) : (b, a);
    }

    private bool DeleteEditSelection()
    {
        var sel = GetEditSelection();
        if (sel is null) return false;
        int s = sel.Value.Item1, e = sel.Value.Item2;
        if (_editingTitle) _editTitle = _editTitle.Remove(s, e - s);
        else _editBody = _editBody.Remove(s, e - s);
        _editCursorPos = s;
        _editSelectionAnchor = -1;
        return true;
    }

    private void InsertEditText(string s)
    {
        DeleteEditSelection();
        if (_editingTitle) _editTitle = _editTitle.Insert(_editCursorPos, s);
        else _editBody = _editBody.Insert(_editCursorPos, s);
        _editCursorPos += s.Length;
        _editSelectionAnchor = -1;
    }

    private void UpdateSelectionAnchor(bool shift)
    {
        if (shift && _editSelectionAnchor < 0) _editSelectionAnchor = _editCursorPos;
    }

    private static int LineStart(string text, int pos)
    {
        int i = Math.Min(pos, text.Length) - 1;
        while (i >= 0 && text[i] != '\n') i--;
        return i + 1;
    }

    private static int LineEnd(string text, int pos)
    {
        int i = Math.Min(pos, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return i;
    }

    private static int MoveLine(string text, int pos, int dir)
    {
        int curStart = LineStart(text, pos);
        int col = pos - curStart;
        if (dir < 0)
        {
            if (curStart == 0) return pos;
            int prevEnd = curStart - 1; // points at '\n'
            int prevStart = LineStart(text, prevEnd);
            int prevLen = prevEnd - prevStart;
            return prevStart + Math.Min(col, prevLen);
        }
        else
        {
            int curEnd = LineEnd(text, pos);
            if (curEnd >= text.Length) return pos;
            int nextStart = curEnd + 1;
            int nextEnd = LineEnd(text, nextStart);
            int nextLen = nextEnd - nextStart;
            return nextStart + Math.Min(col, nextLen);
        }
    }

    private int HitTestCursorPos(string text, float fontSize, float textX, float textY, float maxW, Point world, bool wrap = false)
    {
        if (_dwrite is null) return 0;
        string layoutText = text.Length == 0 ? " " : text;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        using var layout = _dwrite.CreateTextLayout(layoutText, fmt, maxW, 9999f);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        SharpGen.Runtime.RawBool isTrailing = false;
        SharpGen.Runtime.RawBool isInside = false;
        var metrics = layout.HitTestPoint((float)(world.X - textX), (float)(world.Y - textY), out isTrailing, out isInside);
        return Math.Clamp((int)metrics.TextPosition + (isTrailing ? 1 : 0), 0, text.Length);
    }

    private void ToggleNoteGlue(string noteKey)
    {
        bool isAttached = Scene.Connections.Any(c =>
            c.Label == "__note" && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase));
        if (isAttached) { DetachNote(noteKey); return; }

        var note = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(noteKey, StringComparison.OrdinalIgnoreCase));
        if (note is null) return;
        var nearest = Scene.Blocks
            .Where(b => b.Kind != BlockKind.Note)
            .OrderBy(b => DistanceBetween(note, b))
            .FirstOrDefault();
        if (nearest is not null)
            AttachNote(nearest.Key, noteKey);
    }

    private static double DistanceBetween(RenderBlock a, RenderBlock b)
    {
        double ax = a.X + a.Width / 2;
        double ay = a.Y + a.Height / 2;
        double bx = b.X + b.Width / 2;
        double by = b.Y + b.Height / 2;
        return Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
    }

    private static RenderScene ToggleSelection(RenderScene scene, string key) =>
        scene with { Blocks = scene.Blocks.Select(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? b with { IsSelected = !b.IsSelected } : b).ToList() };

    private static RenderScene SetSelection(RenderScene scene, IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return scene with { Blocks = scene.Blocks.Select(b => b with { IsSelected = set.Contains(b.Key) }).ToList() };
    }

    private static RenderScene ClearSelection(RenderScene scene) =>
        scene with
        {
            Blocks = scene.Blocks.Select(b => b with { IsSelected = false }).ToList(),
            SwimLanes = scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList()
        };

    private static RenderScene SelectSwimLane(RenderScene scene, string key) =>
        scene with
        {
            Blocks = scene.Blocks.Select(b => b with { IsSelected = false }).ToList(),
            SwimLanes = scene.SwimLanes.Select(l => l with { IsSelected = l.Key.Equals(key, StringComparison.OrdinalIgnoreCase) }).ToList()
        };

    private void CompleteMarquee()
    {
        if (_marqueeStart is null || _marqueeEnd is null) return;
        Rect screenRect = new(_marqueeStart.Value, _marqueeEnd.Value);
        if (screenRect.Width < 4 && screenRect.Height < 4) { ApplySceneChange(ClearConnectionSelection(ClearSelection(Scene))); return; }
        Point topLeft = ToWorld(new Point(screenRect.Left, screenRect.Top));
        Point bottomRight = ToWorld(new Point(screenRect.Right, screenRect.Bottom));
        Rect worldRect = new(topLeft, bottomRight);
        var hit = _snapshot.QueryBlocks(worldRect).Select(v => v.Block.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocks = Scene.Blocks.Select(b => b with { IsSelected = _appendMarquee ? b.IsSelected || hit.Contains(b.Key) : hit.Contains(b.Key) }).ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks }));
        RebuildSnapshot();
    }

    // -----------------------------------------------------------------------
    // Hit testing helpers
    // -----------------------------------------------------------------------
    private SceneBlockVisual? HitBlock(Point world)
    {
        foreach (var v in _snapshot.QueryPoint(world).Reverse())
            if (v.Bounds.Contains(world)) return v;
        return null;
    }

    private SceneSwimLaneVisual? HitSwimLane(Point world)
    {
        foreach (var l in _snapshot.SwimLanes.Reverse<SceneSwimLaneVisual>())
            if (l.Bounds.Contains(world)) return l;
        return null;
    }

    private SceneSwimLaneVisual? HitSwimLaneResize(Point world)
    {
        foreach (var l in _snapshot.SwimLanes)
        {
            var handle = new Rect(l.Bounds.Right - ResizeHandleSize, l.Bounds.Bottom - ResizeHandleSize, ResizeHandleSize, ResizeHandleSize);
            if (handle.Contains(world)) return l;
        }
        return null;
    }

    private bool IsInResize(Rect bounds, Point world)
    {
        var handle = new Rect(bounds.Right - ResizeHandleSize, bounds.Bottom - ResizeHandleSize, ResizeHandleSize, ResizeHandleSize);
        return handle.Contains(world) || IsInRightEdgeResize(bounds, world);
    }

    private bool IsInRightEdgeResize(Rect bounds, Point world)
    {
        const double edgeWidth = 10;
        var edge = new Rect(bounds.Right - edgeWidth, bounds.Y + HeaderH, edgeWidth, Math.Max(0, bounds.Height - HeaderH - FooterH));
        return edge.Contains(world);
    }

    private static Rect GetRestoreButtonBounds(Rect blockBounds)
    {
        const double sz = 22;
        const double margin = 12;
        return new Rect(blockBounds.Right - sz - margin, blockBounds.Y + 12, sz, sz);
    }

    private static bool IsInRestoreButton(Rect blockBounds, Point world) =>
        GetRestoreButtonBounds(blockBounds).Contains(world);

    private int WorldToCodeLine(SceneBlockVisual block, Point world)
    {
        Rect bodyRect = GetBodyRect(block.Bounds);
        double topPadding = block.Block.Focused is not null ? FocusedCodeTopPaddingLines * CodeLineH : 0;
        double relY = world.Y - bodyRect.Y - topPadding - 12;
        int lineIndex = Math.Max(0, (int)(relY / CodeLineH));
        int startLine = block.Block.Focused?.StartLine ?? block.Block.StartLine ?? 1;
        _codeScrollLines.TryGetValue(block.Block.Key, out int scrollLines);
        return startLine + scrollLines + lineIndex;
    }

    private static Rect GetBodyRect(Rect outer) =>
        new(outer.X + 1, outer.Y + HeaderH, outer.Width - 2, outer.Height - HeaderH - FooterH - 1);

}

