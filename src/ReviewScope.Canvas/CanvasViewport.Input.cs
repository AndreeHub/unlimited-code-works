using ReviewScope.Domain;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.Input.cs
 * Purpose: Partial class for CanvasViewport handling user input events (keyboard, mouse, wheel) and tool delegation.
 * Functions:
 * - HandleKeyDown, HandleKeyUp, HandleChar: Keyboard input processing.
 * - HandleLDown, HandleLUp, HandleRDown, HandleRUp, HandleMove: Mouse input processing and tool dispatching.
 * - HandleWheel: Zoom and scroll logic.
 * - HitTestCursorPos, TryHitSymbolToken: Low-level hit testing for text and symbols.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

public sealed partial class CanvasViewport
{
    /// <summary>
    /// Called from MainWindow.xaml.cs OnWindowPreviewKeyDown for keys that WPF would
    /// otherwise eat (Tab as focus traversal, arrows as directional navigation). Routes
    /// the key into the same edit handler the canvas would use if WPF hadn't intercepted.
    /// </summary>
    public void ForwardEditKey(Key key)
    {
        if (_editingNoteKey is not null) HandleEditModeKey(key);
        else if (_editingGroupKey is not null) HandleGroupTitleEditKey(key);
    }

    private void HandleKeyDown(IntPtr wParam)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64());
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (_editingNoteKey is not null) { HandleEditModeKey(key); return; }
        if (_editingGroupKey is not null) { HandleGroupTitleEditKey(key); return; }
        
        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.G) { GroupSelectedBlocks(); return; }
        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.U) { UngroupSelectedGroups(); return; }
        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.C)
        {
            if (CopyRequestedCommand?.CanExecute(null) == true)
                CopyRequestedCommand.Execute(null);
            return;
        }
        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.V)
        {
            WpfPoint world = GetLastMouseWorldPoint();
            if (PasteRequestedCommand?.CanExecute(null) == true)
                PasteRequestedCommand.Execute(new PasteRequestedArgs(world.X, world.Y));
            return;
        }

        if (ActivateBottomToolShortcut(key, modifiers))
            return;
        
        if (key == Key.Delete) { DeleteSelected(); return; }
        if (key == Key.D) { DetachSelectedNotes(); return; }
        if (key == Key.F) { FrameAll(); return; }
        if (key == Key.B) { ToggleBackground(); return; }
        if (key == Key.Space) { ToggleSelectedGroupCollapse(); return; }
        
        if (key == Key.Return)
        {
            var selected = Scene.Blocks.FirstOrDefault(b => b.IsSelected && IsTextEditableBlock(b));
            if (selected is not null) { var vis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == selected.Key); if (vis is not null) BeginNoteEdit(vis.Block); return; }
            selected = Scene.Blocks.FirstOrDefault(b => b.IsSelected && IsColorGroup(b));
            if (selected is not null) { BeginGroupTitleEdit(selected); return; }
            return;
        }
        
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = true; RenderNative(); }
        
        _currentTool?.HandleKeyDown(key, modifiers);
    }

    private void HandleKeyUp(IntPtr wParam)
    {
        Key key = KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64());
        ModifierKeys modifiers = Keyboard.Modifiers;
        
        if (key == Key.LeftAlt || key == Key.RightAlt) { _isExtractMode = false; RenderNative(); }
        if (key == Key.Escape)
        {
            ClearConnectionDrawingState();
            _activeShapeTool = null;
            SyncActiveShapeToolDp();
            PendingItemPlacement = null;
            _shapeDraftStartWorld = null;
            _shapeDraftCurrentWorld = null;
            _shapeDraftPolyline = null;
            _shapeDraftAttachStartKey = null;
            _shapeDraftAttachEndKey = null;
            _shapeDraftStartOffset = null;
            _shapeDraftEndOffset = null;
            _shapeDraftCurvedFlags = null;
            SetTool("Selection");
            RenderNative();
        }
        
        _currentTool?.HandleKeyUp(key, modifiers);
    }

    private void HandleLDown(WpfPoint screen)
    {
        WpfPoint world = ToWorld(screen);
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (_isDrawingConnection) { SetTool("Connection"); _currentTool?.HandleLDown(screen, world, modifiers); return; }

        Focus(); SetFocus(_hwnd);

        // Pending item placement: a click drops the item at that point. The tool stays armed so
        // the user can drop several — click the toolbar icon again, or press Esc, to disarm.
        if (!string.IsNullOrEmpty(PendingItemPlacement))
        {
            var kind = PendingItemPlacement!;
            var args = new ItemPlacementArgs(kind, world.X, world.Y);
            if (ItemPlacementRequestedCommand?.CanExecute(args) == true)
                ItemPlacementRequestedCommand.Execute(args);
            // Text/Note: disarm the tool so the user can immediately type into the
            // new card (auto-edit is triggered by the VM PostCreateEditRequested event).
            // Other kinds stay armed for multi-drop.
            if (string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "note", StringComparison.OrdinalIgnoreCase))
            {
                PendingItemPlacement = null;
                Cursor = Cursors.Arrow;
                return;
            }
            Cursor = Cursors.Cross;
            return;
        }

        if (ShowShapeToolPalette && HitShapeTool(screen) is { } shapeTool)
        {
            if (_editingNoteKey is not null) CommitNoteEdit(save: true);
            if (_editingGroupKey is not null) CommitGroupTitleEdit(save: true);
            _activeShapeTool = string.Equals(_activeShapeTool, shapeTool, StringComparison.OrdinalIgnoreCase) ? null : shapeTool;
            SyncActiveShapeToolDp();
            _shapeDraftStartWorld = null;
            _shapeDraftCurrentWorld = null;
            _shapeDraftPolyline = null;
            _shapeDraftAttachStartKey = null;
            _shapeDraftAttachEndKey = null;
            _shapeDraftStartOffset = null;
            _shapeDraftEndOffset = null;
            _shapeDraftCurvedFlags = null;
            Cursor = _activeShapeTool is null ? Cursors.Arrow : Cursors.Cross;
            RenderNative();
            return;
        }

        if (_activeShapeTool is not null) { SetTool("Shape"); _currentTool?.HandleLDown(screen, world, modifiers); return; }

        if (_editingNoteKey is not null)
        {
            var editVis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _editingNoteKey);
            if (editVis is null || !editVis.Bounds.Contains(world)) CommitNoteEdit(save: true);
            else
            {
                // Outline overlays that respond before cursor positioning: collapse
                // toggle on parent bullets, and TODO/DONE checkboxes.
                if (OutlineDocument.TryHitToggle(editVis.Block, editVis.Bounds, world, out string editToggleLineId))
                {
                    ToggleOutlineLineCollapseInEdit(editToggleLineId);
                    return;
                }
                int editTodoLine = OutlineDocument.TryHitTodoCheckbox(editVis.Block, editVis.Bounds, world);
                if (editTodoLine >= 0)
                {
                    ToggleTodoLineInEdit(editTodoLine);
                    return;
                }
                _editingTitle = IsNoteTitleHit(editVis, world);
                int newPos;
                if (!_editingTitle && OutlineDocument.IsOutlineBlock(editVis.Block) && _dwrite is not null)
                {
                    var style = editVis.Block.Style ?? new BoardItemStyle();
                    var contentRect = OutlineDocument.GetContentRect(editVis.Block, editVis.Bounds);
                    float fontSize = editVis.Block.Kind == BlockKind.Note
                        ? 12.5f
                        : Math.Clamp((float)style.FontSize, 8f, 96f);
                    string fam = editVis.Block.Kind == BlockKind.Note ? "Segoe UI"
                        : (string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily!);
                    newPos = OutlineDocument.HitTestPoint(editVis.Block, contentRect, _editBody, fontSize, fam, style.Bold, style.Italic, world, _dwrite);
                }
                else
                {
                    var metrics = GetEditTextMetrics(editVis, _editingTitle);
                    string editText = _editingTitle ? _editTitle : _editBody;
                    newPos = HitTestCursorPos(editText, metrics.FontSize, metrics.X, metrics.Y, metrics.Width, world, wrap: metrics.Wrap, alignment: metrics.Alignment, maxH: metrics.Height, paragraphAlignment: metrics.ParagraphAlignment, fontFamily: metrics.FontFamily, bold: metrics.Bold, italic: metrics.Italic);
                }
                if (modifiers.HasFlag(ModifierKeys.Shift)) { if (_editSelectionAnchor < 0) _editSelectionAnchor = _editCursorPos; }
                else _editSelectionAnchor = newPos;
                _editCursorPos = newPos;
                _editMouseSelecting = true;
                SetCapture(_hwnd);
                _editCursorVisible = true;
                RenderNative();
                return;
            }
        }

        if (_editingGroupKey is not null)
        {
            var editVis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _editingGroupKey);
            if (editVis is null || !editVis.Bounds.Contains(world)) CommitGroupTitleEdit(save: true);
            else { _editCursorVisible = true; RenderNative(); return; }
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) { SetTool("Pan"); _currentTool?.HandleLDown(screen, world, modifiers); return; }

        var blockHitForDoubleClick = HitBlock(world);
        if (blockHitForDoubleClick is not null && IsColorGroup(blockHitForDoubleClick.Block) && IsDoubleClick(blockHitForDoubleClick.Block.Key, screen))
        { ToggleGroupCollapse(blockHitForDoubleClick.Block.Key); return; }

        if (HitConnectionAnchor(world) is not null || HitConnectionControlNode(world) is not null || HitConnectionArrow(world) is not null || HitConnectionCurve(world, out _) is not null)
        { SetTool("Connection"); _currentTool?.HandleLDown(screen, world, modifiers); return; }

        if (modifiers.HasFlag(ModifierKeys.Control)) { SetTool("Marquee"); _currentTool?.HandleLDown(screen, world, modifiers); return; }

        var outlineToggleHit = HitBlock(world);
        if (outlineToggleHit is not null && OutlineDocument.TryHitToggle(outlineToggleHit.Block, outlineToggleHit.Bounds, world, out string outlineLineId))
        {
            ToggleOutlineLineCollapse(outlineToggleHit.Block.Key, outlineLineId);
            return;
        }
        if (outlineToggleHit is not null)
        {
            int todoLine = OutlineDocument.TryHitTodoCheckbox(outlineToggleHit.Block, outlineToggleHit.Bounds, world);
            if (todoLine >= 0)
            {
                ToggleTodoLine(outlineToggleHit.Block.Key, todoLine);
                return;
            }
        }

        SetTool("Selection");
        _currentTool?.HandleLDown(screen, world, modifiers);
    }

    private void HandleLUp(WpfPoint screen)
    {
        WpfPoint world = ToWorld(screen);
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (_editMouseSelecting)
        {
            _editMouseSelecting = false;
            if (_editSelectionAnchor == _editCursorPos) _editSelectionAnchor = -1;
            ReleaseCapture();
            RenderNative();
            return;
        }

        _currentTool?.HandleLUp(screen, world, modifiers);

        if (_activeShapeTool is null && !_isDrawingConnection) SetTool("Selection");
        ResetInteraction(); UpdateHoverCursor(screen); ReleaseCapture();
    }

    private void HandleRDown(WpfPoint screen)
    {
        Focus(); SetFocus(_hwnd);
        WpfPoint world = ToWorld(screen);
        ModifierKeys modifiers = Keyboard.Modifiers;
        
        if (HitConnectionAnchor(world) is not null) { SetTool("Connection"); _currentTool?.HandleLDown(screen, world, modifiers); return; }
        
        _currentTool?.HandleRDown(screen, world, modifiers);
    }

    private void HandleRUp(WpfPoint screen)
    {
        WpfPoint world = ToWorld(screen);
        ModifierKeys modifiers = Keyboard.Modifiers;
        _currentTool?.HandleLUp(screen, world, modifiers); // Reuse LUp for now or add RUp
    }

    private void HandleMDown(WpfPoint screen)
    {
        Focus(); SetFocus(_hwnd);
        _panPoint = screen;
        Cursor = Cursors.Hand;
        SetCapture(_hwnd);
    }

    private void HandleMUp(WpfPoint screen)
    {
        _panPoint = null;
        UpdateHoverCursor(screen);
        ReleaseCapture();
    }

    private void HandleMove(WpfPoint screen)
    {
        _lastMouseScreenPoint = screen;
        if (_isMinimapDrag) { UpdateCameraFromMinimap(screen); return; }

        // Direct pan handling for middle-button or Alt-drag started outside tool system
        if (_panPoint is WpfPoint last)
        {
            double dx = screen.X - last.X;
            double dy = screen.Y - last.Y;
            _camera = new CameraState(_camera.Zoom, _camera.OffsetX + dx, _camera.OffsetY + dy);
            SetCurrentValue(CameraProperty, _camera);
            _panPoint = screen;
            _visDirty = true;
            RenderNative();
            return;
        }

        WpfPoint world = ToWorld(screen);
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (_editMouseSelecting && _editingNoteKey is not null)
        {
            var ev = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == _editingNoteKey);
            if (ev is not null)
            {
                if (!_editingTitle && OutlineDocument.IsOutlineBlock(ev.Block) && _dwrite is not null)
                {
                    var style2 = ev.Block.Style ?? new BoardItemStyle();
                    var contentRect = OutlineDocument.GetContentRect(ev.Block, ev.Bounds);
                    float fontSize = ev.Block.Kind == BlockKind.Note ? 12.5f : Math.Clamp((float)style2.FontSize, 8f, 96f);
                    string fam = ev.Block.Kind == BlockKind.Note ? "Segoe UI"
                        : (string.IsNullOrWhiteSpace(style2.FontFamily) ? "Segoe UI" : style2.FontFamily!);
                    _editCursorPos = OutlineDocument.HitTestPoint(ev.Block, contentRect, _editBody, fontSize, fam, style2.Bold, style2.Italic, world, _dwrite);
                }
                else
                {
                var metrics = GetEditTextMetrics(ev, _editingTitle);
                string editText = _editingTitle ? _editTitle : _editBody;
                _editCursorPos = HitTestCursorPos(editText, metrics.FontSize, metrics.X, metrics.Y, metrics.Width, world, wrap: metrics.Wrap, alignment: metrics.Alignment, maxH: metrics.Height, paragraphAlignment: metrics.ParagraphAlignment, fontFamily: metrics.FontFamily, bold: metrics.Bold, italic: metrics.Italic);
                }
                _editCursorVisible = true;
                RenderNative();
            }
            return;
        }

        _currentTool?.HandleMouseMove(screen, world, modifiers);
        UpdateHoverCursor(screen);
    }

    private void HandleWheel(WpfPoint screen, int delta)
    {
        if (TryScrollCodeBlock(screen, delta)) return;
        WpfPoint anchor = ToWorld(screen);
        double factor = delta > 0 ? 1.08 : 0.92;
        double nextZoom = Math.Clamp(_camera.Zoom * factor, 0.02, 8.0);
        _camera = new CameraState(nextZoom, screen.X - anchor.X * nextZoom, screen.Y - anchor.Y * nextZoom);
        SetCurrentValue(CameraProperty, _camera);
        _visDirty = true;
        RenderNative();
    }

    private bool TryScrollCodeBlock(WpfPoint screen, int delta)
    {
        WpfPoint world = ToWorld(screen);
        var hit = HitBlock(world);
        if (hit is null || hit.Block.Body is null || hit.Block.Kind is not (BlockKind.File or BlockKind.Extract or BlockKind.MarkdownDoc)) return false;
        Rect bodyRect = CanvasDrawingUtils.GetBodyRect(hit.Bounds);
        if (!bodyRect.Contains(world)) return false;
        int maxScroll = GetMaxCodeScrollLines(hit.Block, bodyRect);
        if (maxScroll <= 0) return true;
        _codeScrollLines.TryGetValue(hit.Block.Key, out int current);
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 12 : 4;
        int next = Math.Clamp(current + (delta < 0 ? step : -step), 0, maxScroll);
        if (next == current) return true;
        _codeScrollLines[hit.Block.Key] = next;
        RenderNative();
        return true;
    }

    // -----------------------------------------------------------------------
    // Scene mutation helpers
    // -----------------------------------------------------------------------
    internal void MoveBlocks(IReadOnlyList<string> keys, double dx, double dy, bool applySnap = true)
    {
        if (keys.Count == 0) return;
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string attachedNoteKey in GetAttachedNoteKeys(set)) set.Add(attachedNoteKey);

        if (applySnap && SnapToGrid)
        {
            var anchorKey = _primaryDrag is not null && set.Contains(_primaryDrag) ? _primaryDrag : keys[0];
            var anchor = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(anchorKey, StringComparison.OrdinalIgnoreCase));
            if (anchor is not null)
            {
                double grid = Math.Clamp(GridSize, 4, 240);
                double snappedX = Math.Round((anchor.X + dx) / grid) * grid;
                double snappedY = Math.Round((anchor.Y + dy) / grid) * grid;
                dx = snappedX - anchor.X; dy = snappedY - anchor.Y;
            }
        }

        if (dx == 0 && dy == 0) return;

        var blocks = Scene.Blocks.Select(b =>
        {
            if (!set.Contains(b.Key) || b.IsLocked) return b;
            var movedGroupState = b.GroupState is null ? null : b.GroupState with { ExpandedX = b.GroupState.ExpandedX + dx, ExpandedY = b.GroupState.ExpandedY + dy };
            return b with { X = b.X + dx, Y = b.Y + dy, GroupState = movedGroupState };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    private void GroupSelectedBlocks()
    {
        var selected = Scene.Blocks.Where(b => b.IsSelected && !b.IsLocked && !IsColorGroup(b)).ToList();
        if (selected.Count == 0) return;
        Rect bounds = new(selected[0].X, selected[0].Y, selected[0].Width, selected[0].Height);
        foreach (var block in selected.Skip(1)) bounds.Union(new Rect(block.X, block.Y, block.Width, block.Height));
        bounds.Inflate(GroupPadX, GroupPadBottom);
        bounds.Y -= GroupPadTop - GroupPadBottom;
        bounds.Height += GroupPadTop - GroupPadBottom;
        var id = Guid.NewGuid();
        int groupNumber = Scene.Blocks.Count(IsColorGroup) + 1;
        var palette = GroupPalette();
        var group = new RenderBlock(id, $"container::{id:N}", BlockKind.Container, $"Group {groupNumber}", $"{selected.Count} items", bounds.X, bounds.Y, Math.Max(220, bounds.Width), Math.Max(140, bounds.Height), IsSelected: true, ZIndex: Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Min(b => b.ZIndex) - 1, LayerKey: "layer::architecture", ShapeType: "color-group", Style: palette[(groupNumber - 1) % palette.Length]);
        var blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).Append(group).ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks }));
        RebuildSnapshot(); RenderNative();
    }

    internal void ToggleGroupCollapse(string groupKey)
    {
        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(groupKey, StringComparison.OrdinalIgnoreCase) || !IsColorGroup(b) || b.IsLocked) return b;
            if (b.IsCollapsed)
            {
                var state = b.GroupState;
                return state is null ? b with { IsCollapsed = false } : b with { IsCollapsed = false, X = state.ExpandedX, Y = state.ExpandedY, Width = state.ExpandedWidth, Height = state.ExpandedHeight, GroupState = null };
            }
            return b with { IsCollapsed = true, Width = CollapsedGroupW, Height = CollapsedGroupH, GroupState = new BoardGroupState(b.X, b.Y, b.Width, b.Height) };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    private void ToggleSelectedGroupCollapse()
    {
        var selectedGroup = Scene.Blocks.FirstOrDefault(b => b.IsSelected && IsColorGroup(b));
        if (selectedGroup is not null) ToggleGroupCollapse(selectedGroup.Key);
    }

    private void ToggleOutlineLineCollapse(string blockKey, string lineId)
    {
        var target = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(blockKey, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsLocked) return;
        var style = target.Style ?? new BoardItemStyle();
        var collapsed = OutlineDocument.ParseCollapsedSet(style);
        if (!collapsed.Add(lineId))
            collapsed.Remove(lineId);

        var next = target with { Style = style with { OutlineCollapsedItems = OutlineDocument.FormatCollapsedSet(collapsed) } };
        var blocks = Scene.Blocks.Select(b => b.Key.Equals(blockKey, StringComparison.OrdinalIgnoreCase) ? next : b).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot();
        RenderNative();
    }

    /// <summary>
    /// Flip TODO ↔ DONE on the given raw line of a block that is NOT currently being
    /// edited. Mutates the persisted body via the scene-change path so the change is
    /// undoable.
    /// </summary>
    private void ToggleTodoLine(string blockKey, int lineIndex)
    {
        var target = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(blockKey, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsLocked) return;
        string nextBody = OutlineDocument.ToggleTodoLine(target.Body ?? string.Empty, lineIndex);
        if (ReferenceEquals(nextBody, target.Body)) return;
        var next = target with { Body = nextBody };
        var blocks = Scene.Blocks.Select(b => b.Key.Equals(blockKey, StringComparison.OrdinalIgnoreCase) ? next : b).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot();
        RenderNative();
    }

    /// <summary>Same as <see cref="ToggleTodoLine"/> but rewrites the live _editBody
    /// rather than committing through the scene, so the edit session stays open.</summary>
    private void ToggleTodoLineInEdit(int lineIndex)
    {
        string nextBody = OutlineDocument.ToggleTodoLine(_editBody, lineIndex);
        if (ReferenceEquals(nextBody, _editBody)) return;
        // Keep cursor where it was; if the prefix length changed we still keep
        // raw column intact because TODO and DONE are both 4 chars.
        _editBody = nextBody;
        _editSelectionAnchor = -1;
        _editCursorVisible = true;
        RenderNative();
    }

    /// <summary>Same as <see cref="ToggleOutlineLineCollapse"/> but for the block being
    /// edited — updates the live block's style so the visual reflects immediately.</summary>
    private void ToggleOutlineLineCollapseInEdit(string lineId)
    {
        if (_editingNoteKey is null) return;
        ToggleOutlineLineCollapse(_editingNoteKey, lineId);
    }

    private void UngroupSelectedGroups()
    {
        var selectedGroupKeys = Scene.Blocks.Where(b => b.IsSelected && IsColorGroup(b) && !b.IsLocked).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedGroupKeys.Count == 0) return;
        var blocks = Scene.Blocks.Where(b => !selectedGroupKeys.Contains(b.Key)).Select(b => b with { IsSelected = false }).ToList();
        var connections = Scene.Connections.Where(c => !selectedGroupKeys.Contains(c.SourceKey) && !selectedGroupKeys.Contains(c.TargetKey)).ToList();
        ApplySceneChange(Scene with { Blocks = blocks, Connections = connections });
        RebuildSnapshot(); RenderNative();
    }

    internal List<string> GetGroupDragKeys(RenderBlock group)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Key };
        Rect groupBounds = GetGroupExpandedBounds(group);
        foreach (var block in Scene.Blocks)
        {
            if (block.Key.Equals(group.Key, StringComparison.OrdinalIgnoreCase) || IsColorGroup(block)) continue;
            if (groupBounds.IntersectsWith(new Rect(block.X, block.Y, block.Width, block.Height))) keys.Add(block.Key);
        }
        return keys.ToList();
    }

    private static BoardItemStyle[] GroupPalette() => new[]
    {
        new BoardItemStyle("#EAF4FF", "#2E7DD7", "#17324D", 1.6, Opacity: 0.18, CornerRadius: 8),
        new BoardItemStyle("#EAFBF3", "#23A26D", "#123B2B", 1.6, Opacity: 0.18, CornerRadius: 8),
        new BoardItemStyle("#FFF4DE", "#D97706", "#4B2E05", 1.6, Opacity: 0.18, CornerRadius: 8),
        new BoardItemStyle("#FCEBEC", "#DC2626", "#4A1111", 1.6, Opacity: 0.18, CornerRadius: 8),
        new BoardItemStyle("#F3EEFF", "#7C3AED", "#2F1A55", 1.6, Opacity: 0.18, CornerRadius: 8)
    };

    internal string DuplicateSelectedBlocksForDrag(string hitKey)
    {
        var selected = Scene.Blocks.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) { var hit = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(hitKey, StringComparison.OrdinalIgnoreCase)); if (hit is not null) selected.Add(hit); }
        if (selected.Count == 0) return hitKey;
        var keyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<RenderBlock>(selected.Count);
        foreach (var block in selected)
        {
            var id = Guid.NewGuid(); string key = $"{block.Kind.ToString().ToLowerInvariant()}::{id:N}"; keyMap[block.Key] = key;
            duplicates.Add(block with { Id = id, Key = key, IsSelected = true, ZIndex = Scene.Blocks.Count + duplicates.Count });
        }
        var annotations = Scene.Annotations.Concat(duplicates.Where(b => b.Kind == BlockKind.Note).Select(b => new RenderAnnotation(b.Id, b.Key, b.Body ?? b.Title, b.X, b.Y))).ToList();
        var blocks = Scene.Blocks.Select(b => b with { IsSelected = false }).Concat(duplicates).ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks, Annotations = annotations }));
        return keyMap.TryGetValue(hitKey, out string? duplicateKey) ? duplicateKey : duplicates[0].Key;
    }

    internal void ResizeBlock(string key, double dw, double dh)
    {
        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || b.IsLocked) return b;
            return b with { Width = Math.Max(MinBlockW, b.Width + dw), Height = Math.Max(MinBlockH, b.Height + dh) };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    internal void MoveSwimLane(string key, double dx, double dy)
    {
        var lanes = Scene.SwimLanes.Select(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? l with { X = l.X + dx, Y = l.Y + dy } : l).ToList();
        ApplySceneChange(Scene with { SwimLanes = lanes });
        RebuildSnapshot(); RenderNative();
    }

    internal void ResizeSwimLane(string key, double dw, double dh)
    {
        var lanes = Scene.SwimLanes.Select(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? l with { Width = Math.Max(200, l.Width + dw), Height = Math.Max(120, l.Height + dh) } : l).ToList();
        ApplySceneChange(Scene with { SwimLanes = lanes });
        RebuildSnapshot(); RenderNative();
    }

    private void DeleteSelected()
    {
        var selectedConnectionIds = Scene.Connections.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        if (_selectedConnectionId is Guid selectedConnectionId) selectedConnectionIds.Add(selectedConnectionId);
        var selectedKeys = Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedLaneKeys = Scene.SwimLanes.Where(l => l.IsSelected).Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletableKeys = Scene.Blocks.Where(b => selectedKeys.Contains(b.Key) && !b.IsLocked).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attachedLine in Scene.Blocks.Where(b => b.Kind == BlockKind.Shape && IsLinearShapeTool(b.ShapeType) && !b.IsLocked && (ParseLinearShapeBody(b.Body).StartKey is string sk && deletableKeys.Contains(sk) || ParseLinearShapeBody(b.Body).EndKey is string ek && deletableKeys.Contains(ek))))
            deletableKeys.Add(attachedLine.Key);
        if (deletableKeys.Count == 0 && selectedLaneKeys.Count == 0 && selectedConnectionIds.Count == 0) return;
        var blocks = Scene.Blocks.Where(b => !deletableKeys.Contains(b.Key)).ToList();
        var connections = Scene.Connections.Where(c => !selectedConnectionIds.Contains(c.Id) && !deletableKeys.Contains(c.SourceKey) && !deletableKeys.Contains(c.TargetKey)).ToList();
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
            if (note?.Kind == BlockKind.Note) yield return note.Key;
        }
    }

    internal void GlueNearbyNotes(string movedKey)
    {
        var moved = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(movedKey, StringComparison.OrdinalIgnoreCase));
        if (moved is null) return;
        if (moved.Kind == BlockKind.Note)
        {
            var nearest = Scene.Blocks.Where(b => b.Kind != BlockKind.Note).Select(b => new { Block = b, Distance = DistanceBetween(moved, b) }).Where(x => x.Distance <= 42).OrderBy(x => x.Distance).FirstOrDefault();
            if (nearest is not null) AttachNote(nearest.Block.Key, moved.Key);
            return;
        }
        foreach (var note in Scene.Blocks.Where(b => b.Kind == BlockKind.Note && DistanceBetween(moved, b) <= 42)) AttachNote(moved.Key, note.Key);
    }

    private void AttachNote(string sourceKey, string noteKey)
    {
        if (Scene.Connections.Any(c => c.Label == "__note" && c.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase) && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase))) return;
        var connections = Scene.Connections.Where(c => !(c.Label == "__note" && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase))).Append(new RenderConnection(Guid.NewGuid(), sourceKey, noteKey, "__note")).ToList();
        ApplySceneChange(Scene with { Connections = connections });
    }

    internal bool TryCompleteConnectionToBlock(SceneBlockVisual targetBlock, WpfPoint world)
    {
        if (targetBlock.Block.Kind == BlockKind.Note || targetBlock.Block.Key.Equals(_connectionSourceKey, StringComparison.OrdinalIgnoreCase)) return false;
        int targetAnchorIndex = FindNearestConnectionAnchor(targetBlock, world);
        if (_rewireConnectionId is Guid id) { CompleteConnectionRewire(new ConnectionAnchorHit(targetBlock, targetAnchorIndex, CanvasDrawingUtils.GetConnectionAnchorPoint(targetBlock, targetAnchorIndex))); return true; }
        if (_connectionSourceKey is null || ConnectionDrawnCommand?.CanExecute(null) != true) return false;
        ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(_connectionSourceKey, targetBlock.Block.Key, _connectionSourceAnchorIndex, targetAnchorIndex, _connectionDraftMidPoint?.X, _connectionDraftMidPoint?.Y, _connectionDraftMidPointBends));
        return true;
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

    internal void ResizeNoteCorner(string key, NoteResizeCorner corner, double dx, double dy)
    {
        var targetBlock = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (targetBlock is null) return;
        double minW = targetBlock.Kind == BlockKind.Note ? 160 : MinBlockW;
        double minH = targetBlock.Kind == BlockKind.Note ? 90 : MinBlockH;

        var blocks = Scene.Blocks.Select(b =>
        {
            if (!b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return b;
            double x = b.X, y = b.Y, w = b.Width, h = b.Height;
            switch (corner)
            {
                case NoteResizeCorner.TopLeft: { double nw = Math.Max(minW, w - dx); double nh = Math.Max(minH, h - dy); x += w - nw; y += h - nh; w = nw; h = nh; } break;
                case NoteResizeCorner.TopRight: { double nh = Math.Max(minH, h - dy); y += h - nh; w = Math.Max(minW, w + dx); h = nh; } break;
                case NoteResizeCorner.BottomLeft: { double nw = Math.Max(minW, w - dx); x += w - nw; w = nw; h = Math.Max(minH, h + dy); } break;
                case NoteResizeCorner.BottomRight: w = Math.Max(minW, w + dx); h = Math.Max(minH, h + dy); break;
            }
            return b with { X = x, Y = y, Width = w, Height = h };
        }).ToList();
        ApplySceneChange(Scene with { Blocks = blocks });
        RebuildSnapshot(); RenderNative();
    }

    // -----------------------------------------------------------------------
    // In-canvas note editing
    // -----------------------------------------------------------------------
    internal void BeginNoteEdit(RenderBlock block, WpfPoint? clickWorld = null)
    {
        _editingNoteKey = block.Key; _editTitle = block.Title; _editBody = block.Body ?? string.Empty; _editSelectionAnchor = -1; _editMouseSelecting = false; _editingTitle = false;

        // Outline blocks always need a leading "- " on every line so Tab / Shift+Tab
        // / Enter (continue bullet) work consistently. Bootstrap an empty body so the
        // user starts on bullet line 1 with the cursor right after the prefix.
        bool bootstrappedOutline = false;
        if (OutlineDocument.IsOutlineBlock(block) && string.IsNullOrEmpty(_editBody))
        {
            _editBody = "- ";
            bootstrappedOutline = true;
        }

        var vis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key == block.Key);
        if (!bootstrappedOutline && clickWorld is WpfPoint cw && vis is not null)
        {
            _editingTitle = IsNoteTitleHit(vis, cw);
            var metrics = GetEditTextMetrics(vis, _editingTitle);
            string editText = _editingTitle ? _editTitle : _editBody;
            _editCursorPos = HitTestCursorPos(editText, metrics.FontSize, metrics.X, metrics.Y, metrics.Width, cw, wrap: metrics.Wrap, alignment: metrics.Alignment, maxH: metrics.Height, paragraphAlignment: metrics.ParagraphAlignment, fontFamily: metrics.FontFamily, bold: metrics.Bold, italic: metrics.Italic);
        }
        else _editCursorPos = _editBody.Length;
        _editCursorVisible = true; _cursorBlinkTimer?.Dispose();
        _cursorBlinkTimer = new System.Threading.Timer(_ => { _editCursorVisible = !_editCursorVisible; Dispatcher.BeginInvoke(new Action(RenderNative)); }, null, 530, 530);
        ApplySceneChange(CanvasViewport.SetSelection(Scene, new[] { block.Key })); RebuildSnapshot(); RenderNative();
        EditStarted?.Invoke(block.Kind);
    }

    private static Vortice.DirectWrite.TextAlignment ToDWriteTextAlignment(string? alignment) =>
        alignment?.Trim().ToLowerInvariant() switch
        {
            "left" => Vortice.DirectWrite.TextAlignment.Leading,
            "right" => Vortice.DirectWrite.TextAlignment.Trailing,
            _ => Vortice.DirectWrite.TextAlignment.Center
        };

    private static (float X, float Y, float Width, float FontSize, bool Wrap, Vortice.DirectWrite.TextAlignment Alignment, float Height, Vortice.DirectWrite.ParagraphAlignment ParagraphAlignment, string? FontFamily, bool Bold, bool Italic) GetEditTextMetrics(SceneBlockVisual visual, bool title = false)
    {
        Rect bounds = visual.Bounds;
        if (visual.Block.Kind == BlockKind.Note && title)
            return ((float)bounds.X + 24, (float)bounds.Y + 12, (float)bounds.Width - 48, 14f, false, Vortice.DirectWrite.TextAlignment.Leading, 22f, Vortice.DirectWrite.ParagraphAlignment.Near, null, false, false);
        if (visual.Block.Kind == BlockKind.Note)
            return ((float)bounds.X + 14, (float)bounds.Y + 42, (float)bounds.Width - 28, 12.5f, true, Vortice.DirectWrite.TextAlignment.Leading, Math.Max(20f, (float)bounds.Height - 50f), Vortice.DirectWrite.ParagraphAlignment.Near, null, false, false);
        if (visual.Block.Kind == BlockKind.Text)
        {
            var style = visual.Block.Style;
            float fs = style is not null ? Math.Clamp((float)style.FontSize, 8f, 48f) : 14f;
            var align = ToDWriteTextAlignment(style?.TextAlign);
            float padL = (float)Math.Max(0, style?.SpacingLeft ?? 4);
            float padR = (float)Math.Max(0, style?.SpacingRight ?? 4);
            float padT = (float)Math.Max(0, style?.SpacingTop ?? 4);
            float padB = (float)Math.Max(0, style?.SpacingBottom ?? 4);
            float w = Math.Max(4f, (float)bounds.Width - padL - padR);
            float h = Math.Max(4f, (float)bounds.Height - padT - padB);
            var vAlign = ToDWriteParagraphAlignment(style?.VerticalAlign);
            string fam = string.IsNullOrWhiteSpace(style?.FontFamily) ? "Segoe UI" : style!.FontFamily!;
            return ((float)bounds.X + padL, (float)bounds.Y + padT, w, fs, true, align, h, vAlign, fam, style?.Bold ?? false, style?.Italic ?? false);
        }
        return ((float)bounds.X + 14, (float)(bounds.Y + bounds.Height / 2 - 8), (float)bounds.Width - 28, 13f, false, Vortice.DirectWrite.TextAlignment.Leading, 18f, Vortice.DirectWrite.ParagraphAlignment.Near, null, false, false);
    }

    private static Vortice.DirectWrite.ParagraphAlignment ToDWriteParagraphAlignment(string? v) =>
        v?.Trim().ToLowerInvariant() switch
        {
            "top" => Vortice.DirectWrite.ParagraphAlignment.Near,
            "bottom" => Vortice.DirectWrite.ParagraphAlignment.Far,
            _ => Vortice.DirectWrite.ParagraphAlignment.Center
        };

    private static bool IsNoteTitleHit(SceneBlockVisual visual, WpfPoint world)
    {
        if (visual.Block.Kind != BlockKind.Note) return false;
        Rect bounds = visual.Bounds;
        return world.Y < bounds.Y + 38;
    }

    internal void CommitNoteEdit(bool save)
    {
        _cursorBlinkTimer?.Dispose(); _cursorBlinkTimer = null; string? key = _editingNoteKey; _editingNoteKey = null; _editCursorVisible = false; _editSelectionAnchor = -1; _editMouseSelecting = false;
        if (save && key is not null)
        {
            var noteBlock = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (noteBlock is not null)
            {
                string title = string.IsNullOrWhiteSpace(_editTitle) ? "Note" : _editTitle.Trim();
                if (noteBlock.Kind == BlockKind.Text)
                {
                    string body = _editBody ?? string.Empty;
                    title = body.Length > 40 ? body.Substring(0, 37) + "..." : body;
                    if (string.IsNullOrWhiteSpace(title)) title = "Text";
                }
                string savedBody = _editBody ?? string.Empty;
                var blocks = Scene.Blocks.Select(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? b with { Title = title, Body = savedBody } : b).ToList();
                var annotations = Scene.Annotations.Select(a => a.Id == noteBlock.Id ? a with { Content = savedBody } : a).ToList();
                ApplySceneChange(Scene with { Blocks = blocks, Annotations = annotations }); RebuildSnapshot(); return;
            }
        }
        RenderNative();
    }

    private void BeginGroupTitleEdit(RenderBlock block)
    {
        if (!IsColorGroup(block) || block.IsLocked) return;
        _editingGroupKey = block.Key; _editTitle = block.Title; _editBody = string.Empty; _editingTitle = true; _editCursorPos = _editTitle.Length; _editSelectionAnchor = 0; _editMouseSelecting = false; _editCursorVisible = true; _cursorBlinkTimer?.Dispose();
        _cursorBlinkTimer = new System.Threading.Timer(_ => { _editCursorVisible = !_editCursorVisible; Dispatcher.BeginInvoke(new Action(RenderNative)); }, null, 530, 530);
        ApplySceneChange(CanvasViewport.SetSelection(Scene, new[] { block.Key })); RebuildSnapshot(); RenderNative();
    }

    internal void CommitGroupTitleEdit(bool save)
    {
        _cursorBlinkTimer?.Dispose(); _cursorBlinkTimer = null; string? key = _editingGroupKey; _editingGroupKey = null; _editCursorVisible = false; _editSelectionAnchor = -1; _editMouseSelecting = false;
        if (save && key is not null)
        {
            string title = string.IsNullOrWhiteSpace(_editTitle) ? "Group" : _editTitle.Trim();
            var blocks = Scene.Blocks.Select(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && IsColorGroup(b) ? b with { Title = title } : b).ToList();
            ApplySceneChange(Scene with { Blocks = blocks }); RebuildSnapshot(); return;
        }
        RenderNative();
    }

    private void HandleGroupTitleEditKey(Key key)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift); bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && key == Key.A) { _editSelectionAnchor = 0; _editCursorPos = _editTitle.Length; _editCursorVisible = true; RenderNative(); return; }
        if (ctrl && (key == Key.C || key == Key.X)) { var selCx = GetEditSelection(); if (selCx is not null) { int s = selCx.Value.Item1, e = selCx.Value.Item2; try { System.Windows.Clipboard.SetText(_editTitle.Substring(s, e - s)); } catch { } if (key == Key.X) DeleteEditSelection(); } _editCursorVisible = true; RenderNative(); return; }
        if (ctrl && key == Key.V) { try { if (System.Windows.Clipboard.ContainsText()) InsertEditText(System.Windows.Clipboard.GetText().Replace("\r", "").Replace("\n", " ")); } catch { } _editCursorVisible = true; RenderNative(); return; }

        switch (key)
        {
            case Key.Return: CommitGroupTitleEdit(save: true); return;
            case Key.Escape: CommitGroupTitleEdit(save: false); return;
            case Key.Back: if (!DeleteEditSelection() && _editCursorPos > 0) { _editTitle = _editTitle.Remove(_editCursorPos - 1, 1); _editCursorPos--; } _editSelectionAnchor = -1; break;
            case Key.Delete: if (!DeleteEditSelection() && _editCursorPos < _editTitle.Length) _editTitle = _editTitle.Remove(_editCursorPos, 1); _editSelectionAnchor = -1; break;
            case Key.Left: UpdateSelectionAnchor(shift); if (!shift && GetEditSelection() is { } selL) _editCursorPos = selL.Item1; else if (_editCursorPos > 0) _editCursorPos--; if (!shift) _editSelectionAnchor = -1; break;
            case Key.Right: UpdateSelectionAnchor(shift); if (!shift && GetEditSelection() is { } selR) _editCursorPos = selR.Item2; else if (_editCursorPos < _editTitle.Length) _editCursorPos++; if (!shift) _editSelectionAnchor = -1; break;
            case Key.Home: UpdateSelectionAnchor(shift); _editCursorPos = 0; if (!shift) _editSelectionAnchor = -1; break;
            case Key.End: UpdateSelectionAnchor(shift); _editCursorPos = _editTitle.Length; if (!shift) _editSelectionAnchor = -1; break;
        }
        _editCursorVisible = true; RenderNative();
    }

    private void HandleEditModeKey(Key key)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift); bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt); string text = _editingTitle ? _editTitle : _editBody;
        if (ctrl && key == Key.A) { _editSelectionAnchor = 0; _editCursorPos = text.Length; _editCursorVisible = true; RenderNative(); return; }
        if (ctrl && (key == Key.C || key == Key.X)) { var selCx = GetEditSelection(); if (selCx is not null) { int s = selCx.Value.Item1, e = selCx.Value.Item2; try { System.Windows.Clipboard.SetText(text.Substring(s, e - s)); } catch { } if (key == Key.X) DeleteEditSelection(); } _editCursorVisible = true; RenderNative(); return; }
        if (ctrl && key == Key.V) { try { if (System.Windows.Clipboard.ContainsText()) { string clip = System.Windows.Clipboard.GetText(); if (_editingTitle) clip = clip.Replace("\r", "").Replace("\n", " "); else clip = clip.Replace("\r\n", "\n").Replace("\r", "\n"); InsertEditText(clip); } } catch { } _editCursorVisible = true; RenderNative(); return; }

        bool outlineEditing = !_editingTitle && CurrentEditingBlock() is RenderBlock cb && OutlineDocument.IsOutlineBlock(cb);
        if (outlineEditing)
        {
            // Inline-markdown wrap-selection shortcuts. With no selection, they
            // insert paired markers and park the cursor between them so the user
            // can type the styled content.
            if (ctrl && !alt)
            {
                if (key == Key.B && !shift) { WrapSelectionWithMarkers("**", "**"); _editCursorVisible = true; RenderNative(); return; }
                if (key == Key.I && !shift) { WrapSelectionWithMarkers("*", "*"); _editCursorVisible = true; RenderNative(); return; }
                if (key == Key.E && !shift) { WrapSelectionWithMarkers("`", "`"); _editCursorVisible = true; RenderNative(); return; }
                if (key == Key.S && shift)  { WrapSelectionWithMarkers("~~", "~~"); _editCursorVisible = true; RenderNative(); return; }
            }

            if (alt && key is Key.Up or Key.Down)
            {
                _editBody = OutlineDocument.MoveSubtree(_editBody, _editCursorPos, key == Key.Up ? -1 : 1, out _editCursorPos);
                _editSelectionAnchor = -1;
                _editCursorVisible = true;
                RenderNative();
                return;
            }

            switch (key)
            {
                case Key.Return:
                    InsertOutlineContinuation();
                    _editCursorVisible = true;
                    RenderNative();
                    return;
                case Key.Tab:
                    IndentCurrentOutlineSubtree(shift ? -1 : 1);
                    _editCursorVisible = true;
                    RenderNative();
                    return;
                case Key.Back:
                    if (TryHandleOutlineBackspace())
                    {
                        _editCursorVisible = true;
                        RenderNative();
                        return;
                    }
                    break;
            }
        }

        switch (key)
        {
            case Key.Escape: CommitNoteEdit(save: false); return;
            case Key.Back: if (!DeleteEditSelection() && _editCursorPos > 0) { if (_editingTitle) _editTitle = _editTitle.Remove(_editCursorPos - 1, 1); else _editBody = _editBody.Remove(_editCursorPos - 1, 1); _editCursorPos--; } _editSelectionAnchor = -1; break;
            case Key.Delete: if (!DeleteEditSelection() && _editCursorPos < text.Length) { if (_editingTitle) _editTitle = _editTitle.Remove(_editCursorPos, 1); else _editBody = _editBody.Remove(_editCursorPos, 1); } _editSelectionAnchor = -1; break;
            case Key.Left: UpdateSelectionAnchor(shift); if (!shift && GetEditSelection() is { } selL) _editCursorPos = selL.Item1; else if (_editCursorPos > 0) _editCursorPos--; if (!shift) _editSelectionAnchor = -1; break;
            case Key.Right: UpdateSelectionAnchor(shift); if (!shift && GetEditSelection() is { } selR) _editCursorPos = selR.Item2; else if (_editCursorPos < text.Length) _editCursorPos++; if (!shift) _editSelectionAnchor = -1; break;
            case Key.Home: UpdateSelectionAnchor(shift); _editCursorPos = LineStart(text, _editCursorPos); if (!shift) _editSelectionAnchor = -1; break;
            case Key.End: UpdateSelectionAnchor(shift); _editCursorPos = LineEnd(text, _editCursorPos); if (!shift) _editSelectionAnchor = -1; break;
            case Key.Up: UpdateSelectionAnchor(shift); _editCursorPos = MoveLine(text, _editCursorPos, -1); if (!shift) _editSelectionAnchor = -1; break;
            case Key.Down: UpdateSelectionAnchor(shift); _editCursorPos = MoveLine(text, _editCursorPos, +1); if (!shift) _editSelectionAnchor = -1; break;
            case Key.Tab: InsertEditText(_editingTitle ? "    " : "\t"); break;
        }

        // Outline blocks: after moving the cursor, snap it out of any bullet prefix
        // area so the caret lands on something the user can see / type into.
        if (outlineEditing && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            int dir = key is Key.Left ? -1 : 1;
            _editCursorPos = OutlineDocument.SnapToVisible(_editBody, CurrentEditingBlock()?.Style, _editCursorPos, dir);
        }

        _editCursorVisible = true; RenderNative();
    }

    private RenderBlock? CurrentEditingBlock() =>
        _editingNoteKey is null
            ? null
            : Scene.Blocks.FirstOrDefault(b => b.Key.Equals(_editingNoteKey, StringComparison.OrdinalIgnoreCase));

    private void InsertOutlineContinuation()
    {
        DeleteEditSelection();
        var line = OutlineDocument.LineAt(_editBody, _editCursorPos);
        string beforeCursor = _editBody[line.Start.._editCursorPos];
        string currentPrefix = OutlineDocument.BulletPrefixForLine(line.Text);
        string currentContent = line.Text[OutlineDocument.PrefixLengthForLine(line.Text)..].Trim();

        if (currentContent.Length == 0 && _editCursorPos >= line.Start + OutlineDocument.PrefixLengthForLine(line.Text))
        {
            int remove = OutlineDocument.PrefixLengthForLine(line.Text);
            _editBody = _editBody.Remove(line.Start, remove).Insert(line.Start, string.Empty);
            _editCursorPos = line.Start;
            InsertEditText("\n");
            return;
        }

        string prefix = string.IsNullOrWhiteSpace(beforeCursor) ? "- " : currentPrefix;
        InsertEditText("\n" + prefix);
    }

    private bool TryHandleOutlineBackspace()
    {
        if (GetEditSelection() is not null) return false;
        var line = OutlineDocument.LineAt(_editBody, _editCursorPos);
        int prefixLength = OutlineDocument.PrefixLengthForLine(line.Text);
        if (prefixLength <= 0) return false;
        int contentStart = line.Start + prefixLength;
        if (_editCursorPos != contentStart) return false;

        if (line.Text.StartsWith("  ", StringComparison.Ordinal) || line.Text.StartsWith("\t", StringComparison.Ordinal))
        {
            IndentCurrentOutlineSubtree(-1);
            return true;
        }

        _editBody = _editBody.Remove(line.Start, prefixLength);
        _editCursorPos = line.Start;
        _editSelectionAnchor = -1;
        return true;
    }

    private void IndentCurrentOutlineSubtree(int direction)
    {
        if (GetEditSelection() is not null) DeleteEditSelection();
        var lines = _editBody.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count == 0) return;
        int current = Math.Clamp(OutlineDocument.LineIndexAt(_editBody, _editCursorPos), 0, lines.Count - 1);
        int currentStartOffset = LineStart(_editBody, _editCursorPos);
        int column = Math.Max(0, _editCursorPos - currentStartOffset);
        int currentLevel = LeadingOutlineLevel(lines[current]);
        int end = current + 1;
        while (end < lines.Count && LeadingOutlineLevel(lines[end]) > currentLevel) end++;

        int delta = 0;
        for (int i = current; i < end; i++)
        {
            string original = lines[i];
            string next = direction > 0 ? OutlineDocument.IndentLine(original) : OutlineDocument.OutdentLine(original);
            lines[i] = next;
            if (i == current) delta = next.Length - original.Length;
        }

        _editBody = string.Join('\n', lines);
        _editCursorPos = Math.Clamp(currentStartOffset + Math.Max(0, column + delta), 0, _editBody.Length);
        _editSelectionAnchor = -1;
    }

    private static int LeadingOutlineLevel(string line)
    {
        int spaces = 0;
        int tabs = 0;
        foreach (char c in line)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') tabs++;
            else break;
        }
        return tabs + spaces / 2;
    }

    private void HandleChar(char c)
    {
        if (_editingNoteKey is null && _editingGroupKey is null) return;
        if (c == '\b' || c == 27 || c == '\n' || c == '\t') return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (_editingGroupKey is not null) { if (c >= 32) InsertEditText(c.ToString()); _editCursorVisible = true; RenderNative(); return; }

        // Outline-edit owns Enter: HandleEditModeKey already ran InsertOutlineContinuation
        // (which inserts "\n- "). Swallow the WM_CHAR '\r' so we don't get a duplicate newline.
        if (c == '\r' && !_editingTitle && CurrentEditingBlock() is RenderBlock outlineBlock && OutlineDocument.IsOutlineBlock(outlineBlock))
            return;

        if (c == '\r') { if (!_editingTitle) InsertEditText("\n"); } else if (c >= 32) InsertEditText(c.ToString());
        _editCursorVisible = true; RenderNative();
    }

    /// <summary>
    /// Wrap the current selection in <paramref name="open"/>/<paramref name="close"/>
    /// inline-markdown markers, or — when there is no selection — insert the pair
    /// at the caret and park the cursor between them. Operates only on the body
    /// (outline edit invokes this; title editing has no selection model wired here).
    /// </summary>
    private void WrapSelectionWithMarkers(string open, string close)
    {
        if (_editingTitle) return;
        var sel = GetEditSelection();
        if (sel is null)
        {
            _editBody = _editBody.Insert(_editCursorPos, open + close);
            _editCursorPos += open.Length;
            _editSelectionAnchor = -1;
            return;
        }
        int s = sel.Value.Item1, e = sel.Value.Item2;
        // Toggle: if the selection is already wrapped, unwrap instead.
        if (s >= open.Length && e + close.Length <= _editBody.Length
            && _editBody.Substring(s - open.Length, open.Length) == open
            && _editBody.Substring(e, close.Length) == close)
        {
            _editBody = _editBody.Remove(e, close.Length).Remove(s - open.Length, open.Length);
            _editSelectionAnchor = s - open.Length;
            _editCursorPos = e - open.Length;
            return;
        }
        _editBody = _editBody.Insert(e, close).Insert(s, open);
        _editSelectionAnchor = s + open.Length;
        _editCursorPos = e + open.Length;
    }

    private (int, int)? GetEditSelection() { if (_editSelectionAnchor < 0 || _editSelectionAnchor == _editCursorPos) return null; int a = _editSelectionAnchor, b = _editCursorPos; return a < b ? (a, b) : (b, a); }

    private bool DeleteEditSelection() { var sel = GetEditSelection(); if (sel is null) return false; int s = sel.Value.Item1, e = sel.Value.Item2; if (_editingTitle) _editTitle = _editTitle.Remove(s, e - s); else _editBody = _editBody.Remove(s, e - s); _editCursorPos = s; _editSelectionAnchor = -1; return true; }

    private void InsertEditText(string s) { DeleteEditSelection(); if (_editingTitle) _editTitle = _editTitle.Insert(_editCursorPos, s); else _editBody = _editBody.Insert(_editCursorPos, s); _editCursorPos += s.Length; _editSelectionAnchor = -1; }

    private void UpdateSelectionAnchor(bool shift) { if (shift && _editSelectionAnchor < 0) _editSelectionAnchor = _editCursorPos; }

    private static int LineStart(string text, int pos) { int i = Math.Min(pos, text.Length) - 1; while (i >= 0 && text[i] != '\n') i--; return i + 1; }
    private static int LineEnd(string text, int pos) { int i = Math.Min(pos, text.Length); while (i < text.Length && text[i] != '\n') i++; return i; }
    private static int MoveLine(string text, int pos, int dir)
    {
        int curStart = LineStart(text, pos); int col = pos - curStart;
        if (dir < 0) { if (curStart == 0) return pos; int prevEnd = curStart - 1; int prevStart = LineStart(text, prevEnd); int prevLen = prevEnd - prevStart; return prevStart + Math.Min(col, prevLen); }
        else { int curEnd = LineEnd(text, pos); if (curEnd >= text.Length) return pos; int nextStart = curEnd + 1; int nextEnd = LineEnd(text, nextStart); int nextLen = nextEnd - nextStart; return nextStart + Math.Min(col, nextLen); }
    }

    private int HitTestCursorPos(string text, float fontSize, float textX, float textY, float maxW, WpfPoint world, bool wrap = false, Vortice.DirectWrite.TextAlignment alignment = Vortice.DirectWrite.TextAlignment.Leading, float maxH = 9999f, Vortice.DirectWrite.ParagraphAlignment paragraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Near, string? fontFamily = null, bool bold = false, bool italic = false)
    {
        if (_dwrite is null) return 0; string layoutText = text.Length == 0 ? " " : text;
        Vortice.DirectWrite.IDWriteTextFormat fmt = fontFamily is null
            ? GetTextFormat(fontSize)
            : GetRichTextFormat(fontFamily, fontSize, bold, italic);
        var oldTextAlign = fmt.TextAlignment;
        var oldParagraph = fmt.ParagraphAlignment;
        fmt.TextAlignment = alignment;
        // Use top alignment on the measurement layout so HitTestPoint works in raw text
        // coordinates; we shift the input click point by vOffset to compensate.
        fmt.ParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Near;

        using var layout = _dwrite.CreateTextLayout(layoutText, fmt, maxW, maxH);
        if (wrap) layout.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;

        float vOffset = 0f;
        if (paragraphAlignment != Vortice.DirectWrite.ParagraphAlignment.Near)
        {
            var lm = layout.Metrics;
            float gap = Math.Max(0, maxH - lm.Height);
            vOffset = paragraphAlignment == Vortice.DirectWrite.ParagraphAlignment.Center ? gap * 0.5f : gap;
        }

        SharpGen.Runtime.RawBool isTrailing = false; SharpGen.Runtime.RawBool isInside = false;
        var metrics = layout.HitTestPoint((float)(world.X - textX), (float)(world.Y - textY - vOffset), out isTrailing, out isInside);

        fmt.TextAlignment = oldTextAlign;
        fmt.ParagraphAlignment = oldParagraph;

        return Math.Clamp((int)metrics.TextPosition + (isTrailing ? 1 : 0), 0, text.Length);
    }

    private void RequestNoteAtLastMousePoint()
    {
        if (AnnotationRequestedCommand?.CanExecute(null) != true) return;
        WpfPoint world = GetLastMouseWorldPoint();
        AnnotationRequestedCommand.Execute(new AnnotationRequestedArgs(world.X, world.Y));
    }

    private void ToggleNoteGlue(string noteKey) { bool isAttached = Scene.Connections.Any(c => c.Label == "__note" && c.TargetKey.Equals(noteKey, StringComparison.OrdinalIgnoreCase)); if (isAttached) { DetachNote(noteKey); return; } var note = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(noteKey, StringComparison.OrdinalIgnoreCase)); if (note is null) return; var nearest = Scene.Blocks.Where(b => b.Kind != BlockKind.Note).OrderBy(b => DistanceBetween(note, b)).FirstOrDefault(); if (nearest is not null) AttachNote(nearest.Key, noteKey); }

    private static double DistanceBetween(RenderBlock a, RenderBlock b) { double ax = a.X + a.Width / 2; double ay = a.Y + a.Height / 2; double bx = b.X + b.Width / 2; double by = b.Y + b.Height / 2; return Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2)); }
}
