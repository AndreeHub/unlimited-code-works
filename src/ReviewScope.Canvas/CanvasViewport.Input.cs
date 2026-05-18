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
        if (key == Key.Delete) { DeleteSelected(); return; }
        if (key == Key.D) { DetachSelectedNotes(); return; }
        if (key == Key.F) { FrameAll(); return; }
        if (key == Key.B) { ToggleBackground(); return; }
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
        if (key == Key.LeftCtrl || key == Key.RightCtrl) { _isFocusMode = true; RenderNative(); }
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = true; RenderNative(); }
    }

    private void HandleKeyUp(IntPtr wParam)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64());
        if (key == Key.LeftCtrl || key == Key.RightCtrl) { _isFocusMode = false; _extractHoverBlock = null; RenderNative(); }
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = false; _extractHoverBlock = null; RenderNative(); }
        if (key == Key.Escape) { _isDrawingConnection = false; RenderNative(); }
    }

    private void HandleLDown(Point screen)
    {
        Focus(); SetFocus(_hwnd);

        if (TryHandleMinimapClick(screen)) return;

        Point world = ToWorld(screen);

        // Draw-connection mode: click to end connection
        if (_isDrawingConnection)
        {
            var hitBlock = HitBlock(world);
            if (hitBlock is not null && hitBlock.Block.Key != _connectionSourceKey)
            {
                _isDrawingConnection = false;
                if (ConnectionDrawnCommand?.CanExecute(null) == true)
                    ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(_connectionSourceKey!, hitBlock.Block.Key));
            }
            else
            {
                _isDrawingConnection = false;
            }
            RenderNative();
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
                ApplySceneChange(ToggleSelection(Scene, hit.Block.Key));
                return;
            }

            // Select the hit block if not already selected
            if (!hit.Block.IsSelected)
            {
                ApplySceneChange(SetSelection(Scene, new[] { hit.Block.Key }));
                RebuildSnapshot();
                hit = HitBlock(world);
                if (hit is null) return;
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
            ApplySceneChange(SelectSwimLane(Scene, laneBodyHit.Lane.Key));
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

        ApplySceneChange(ClearSelection(Scene));
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
                if (visual.Block.Kind == BlockKind.Note && IsDoubleClick(visual.Block.Key, screen))
                {
                    if (BlockActivatedCommand?.CanExecute(null) == true)
                        BlockActivatedCommand.Execute(new BlockActivatedArgs(visual.Block));
                    ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture();
                    return;
                }
                if (IsDoubleClick(visual.Block.Key, screen) && BlockActivatedCommand?.CanExecute(null) == true)
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
        var hit = HitBlock(world);
        if (hit is not null && !_isDrawingConnection)
        {
            // Start drawing connection from this block
            _isDrawingConnection = true;
            _connectionSourceKey = hit.Block.Key;
            _connectionSourceWorld = new Point(hit.Bounds.X + hit.Bounds.Width / 2, hit.Bounds.Y + hit.Bounds.Height / 2);
            _connectionCurrentWorld = world;
            SetCapture(_hwnd);
            RenderNative();
        }
        else if (hit is null)
        {
            if (AnnotationRequestedCommand?.CanExecute(null) == true)
                AnnotationRequestedCommand.Execute(new AnnotationRequestedArgs(null, world.X, world.Y));
        }
    }

    private void HandleRUp(Point screen)
    {
        if (_isDrawingConnection)
        {
            Point world = ToWorld(screen);
            var hit = HitBlock(world);
            if (hit is not null && hit.Block.Key != _connectionSourceKey)
            {
                if (ConnectionDrawnCommand?.CanExecute(null) == true)
                    ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(_connectionSourceKey!, hit.Block.Key));
            }
            else
            {
                // Show context menu or annotation request on empty space
                if (hit is null)
                {
                    if (AnnotationRequestedCommand?.CanExecute(null) == true)
                        AnnotationRequestedCommand.Execute(new AnnotationRequestedArgs(null, world.X, world.Y));
                }
            }
            _isDrawingConnection = false;
            ReleaseCapture();
            RenderNative();
        }
    }

    private void HandleMove(Point screen)
    {
        _lastMouseScreenPoint = screen;
        if (_isMinimapDrag) { UpdateCameraFromMinimap(screen); return; }

        Point world = ToWorld(screen);

        if (_isDrawingConnection)
        {
            _connectionCurrentWorld = world;
            RenderNative();
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
        var selectedKeys = Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedLaneKeys = Scene.SwimLanes.Where(l => l.IsSelected).Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0 && selectedLaneKeys.Count == 0) return;
        var blocks = Scene.Blocks.Where(b => !selectedKeys.Contains(b.Key)).ToList();
        var connections = Scene.Connections
            .Where(c => !selectedKeys.Contains(c.SourceKey) && !selectedKeys.Contains(c.TargetKey))
            .ToList();
        var lanes = Scene.SwimLanes.Where(l => !selectedLaneKeys.Contains(l.Key)).ToList();
        ApplySceneChange(Scene with { Blocks = blocks, Connections = connections, SwimLanes = lanes });
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
        if (screenRect.Width < 4 && screenRect.Height < 4) { ApplySceneChange(ClearSelection(Scene)); return; }
        Point topLeft = ToWorld(new Point(screenRect.Left, screenRect.Top));
        Point bottomRight = ToWorld(new Point(screenRect.Right, screenRect.Bottom));
        Rect worldRect = new(topLeft, bottomRight);
        var hit = _snapshot.QueryBlocks(worldRect).Select(v => v.Block.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocks = Scene.Blocks.Select(b => b with { IsSelected = _appendMarquee ? b.IsSelected || hit.Contains(b.Key) : hit.Contains(b.Key) }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
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
        double relY = world.Y - bodyRect.Y - 12;
        int lineIndex = Math.Max(0, (int)(relY / CodeLineH));
        int startLine = block.Block.Focused?.StartLine ?? block.Block.StartLine ?? 1;
        _codeScrollLines.TryGetValue(block.Block.Key, out int scrollLines);
        return startLine + scrollLines + lineIndex;
    }

    private static Rect GetBodyRect(Rect outer) =>
        new(outer.X + 1, outer.Y + HeaderH, outer.Width - 2, outer.Height - HeaderH - FooterH - 1);

}

