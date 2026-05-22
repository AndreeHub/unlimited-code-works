import os

path = r"c:\Users\lic\Desktop\3 SWD\ReviewScope\src\ReviewScope.Canvas\BlockRenderer.cs"
if not os.path.exists(path):
    print(f"ERROR: File not found at {path}")
    exit(1)

with open(path, "r", encoding="utf-8") as f:
    content = f.read()

# Normalize to LF and strip trailing spaces on each line for robust replacement matching
content_norm = "\n".join(line.rstrip() for line in content.replace("\r\n", "\n").split("\n"))

# 1. Update DrawBlock switch and add DrawLockIndicator call
old_draw_block = """        switch (block.Kind)
        {
            case BlockKind.File:
            case BlockKind.Extract:
                DrawCodeBlock(blockVis, codeScrollLines, isExtractMode, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled);
                break;
            case BlockKind.Note:
                DrawNoteBlock(blockVis, editingNoteKey, editTitle, editBody, editingTitle, editCursorVisible, editCursorPos, editSelectionAnchor);
                break;
            case BlockKind.MarkdownDoc:
                DrawMarkdownDocBlock(blockVis);
                break;
            case BlockKind.Shape:
                DrawShapeBlock(blockVis, editingNoteKey, editTitle, editBody, editCursorVisible, editCursorPos, editSelectionAnchor, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled, imageLoader);
                break;
            case BlockKind.Text:
                DrawTextBlock(blockVis, editingNoteKey, editBody, editCursorVisible, editCursorPos, editSelectionAnchor);
                break;
            case BlockKind.Image:
                DrawImageBlock(blockVis, imageLoader, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled);
                break;
            case BlockKind.Container:
                DrawContainerBlock(blockVis, editingGroupKey, editTitle, editCursorVisible, editCursorPos, editSelectionAnchor, codeScrollLines);
                break;
        }
    }"""

new_draw_block = """        switch (block.Kind)
        {
            case BlockKind.File:
            case BlockKind.Extract:
                DrawCodeBlock(blockVis, codeScrollLines, isExtractMode, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled);
                break;
            case BlockKind.Note:
                DrawNoteBlock(blockVis, editingNoteKey, editTitle, editBody, editingTitle, editCursorVisible, editCursorPos, editSelectionAnchor);
                break;
            case BlockKind.MarkdownDoc:
                DrawMarkdownDocBlock(blockVis);
                break;
            case BlockKind.Shape:
                DrawShapeBlock(blockVis, editingNoteKey, editTitle, editBody, editCursorVisible, editCursorPos, editSelectionAnchor, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled, imageLoader);
                break;
            case BlockKind.Text:
                DrawTextBlock(blockVis, editingNoteKey, editBody, editCursorVisible, editCursorPos, editSelectionAnchor);
                break;
            case BlockKind.Image:
                DrawImageBlock(blockVis, imageLoader, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex, connectorsEnabled);
                break;
            case BlockKind.Container:
                DrawContainerBlock(blockVis, editingGroupKey, editTitle, editCursorVisible, editCursorPos, editSelectionAnchor, codeScrollLines);
                break;
        }

        if (block.IsLocked)
        {
            WpfColor lockStroke = CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#64748B");
            DrawLockIndicator(blockVis.Bounds, lockStroke, block.Key.ToString());
        }
    }

    private void DrawLockIndicator(Rect bounds, WpfColor strokeColor, string seedKey)
    {
        float padX = (float)(bounds.X + bounds.Width - 24);
        float padY = (float)(bounds.Y + 8);
        float w = 14;
        float h = 10;
        
        var bodyRect = new RectangleF(padX, padY + 6, w, h);
        var bodyBrush = _ctx.GetBrush(WpfColor.FromArgb(200, 241, 245, 249));
        var strokeBrush = _ctx.GetBrush(strokeColor);
        float sw = _ctx.InvStroke(1.2f);
        
        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, bodyRect, bodyBrush, strokeBrush, sw, seedKey + "_lock_body", fillStyle: "solid");
        
        Vector2[] loopPoints = new Vector2[]
        {
            new(padX + 3, padY + 6),
            new(padX + 3, padY + 2),
            new(padX + 7, padY),
            new(padX + 11, padY + 2),
            new(padX + 11, padY + 6)
        };
        SketchyDrawer.DrawPolygon(_ctx.RenderTarget, loopPoints, null, strokeBrush, sw, seedKey + "_lock_shackle", close: false);
    }"""

# 2. Modify DrawNoteBlock resize check
old_note_resize = """        // Corner handles for notes
        if (isSelected || isEditing)
        {
            float ch = 8;
            DrawNoteCornerHandle(x - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x - ch / 2, y + h - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y + h - ch / 2, ch);
        }"""

new_note_resize = """        // Corner handles for notes
        if ((isSelected || isEditing) && !block.IsLocked)
        {
            float ch = 8;
            DrawNoteCornerHandle(x - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x - ch / 2, y + h - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y + h - ch / 2, ch);
        }"""

# 3. Modify DrawCodeBlock generic resize handle
old_code_resize = """        // Resize handle
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, accent);"""

new_code_resize = """        // Resize handle
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, accent, block.IsLocked);"""

# 4. Modify DrawMarkdownDocBlock
old_markdown_doc = """    private void DrawMarkdownDocBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        DrawCardShell(outer, block.IsSelected, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#E2E8F0"), 8, block.Key.ToString());"""

new_markdown_doc = """    private void DrawMarkdownDocBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        var style = block.Style ?? new BoardItemStyle();
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        DrawCardShell(outer, block.IsSelected, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "hatch", style.Opacity);"""

old_markdown_resize = """        _ctx.RenderTarget.PopAxisAlignedClip();
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#2E7DD7"));
    }"""

new_markdown_resize = """        _ctx.RenderTarget.PopAxisAlignedClip();
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#2E7DD7"), block.IsLocked);
    }"""

# 5. Modify DrawShapeBlock
old_shape_block = """        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        WpfColor fill = CanvasDrawingUtils.ParseColor(block.Style?.Fill ?? "#EFF6FF");
        WpfColor stroke = CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#2E7DD7");
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        string shape = block.ShapeType ?? "service";
        float baseStroke = (float)Math.Clamp(block.Style?.StrokeWidth ?? 1.3, 0.5, 8.0);
        float strokeWidth = block.IsSelected ? _ctx.InvStroke(Math.Max(2, baseStroke + 0.6f)) : _ctx.InvStroke(baseStroke);
        bool dashed = block.Style?.Dashed == true;

        var fillBrush = _ctx.GetBrush(fill);
        var strokeBrush = _ctx.GetBrush(stroke);
        var strokeStyle = dashed ? _ctx.DashedStroke : null;
        string fillStyle = block.Style?.FillStyle ?? "hatch";"""

new_shape_block = """        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        var style = block.Style ?? new BoardItemStyle();
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        string shape = block.ShapeType ?? "service";
        float baseStroke = (float)Math.Clamp(style.StrokeWidth, 0.5, 8.0);
        float strokeWidth = block.IsSelected ? _ctx.InvStroke(Math.Max(2, baseStroke + 0.6f)) : _ctx.InvStroke(baseStroke);
        bool dashed = style.Dashed;
        double opacity = style.Opacity;
        
        WpfColor opacityFill = WpfColor.FromArgb((byte)(fill.A * opacity), fill.R, fill.G, fill.B);
        WpfColor opacityStroke = WpfColor.FromArgb((byte)(stroke.A * opacity), stroke.R, stroke.G, stroke.B);

        var fillBrush = opacityFill.A == 0 ? null : _ctx.GetBrush(opacityFill);
        var strokeBrush = _ctx.GetBrush(opacityStroke);
        var strokeStyle = dashed ? _ctx.DashedStroke : null;
        string fillStyle = style.FillStyle ?? "hatch";"""

old_shape_square_rect = """        else if (shape is "square")
        {
            Rect square = CanvasDrawingUtils.CenteredSquare(outer);
            SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(square), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
        }
        else if (shape is "rectangle")
        {
            SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
        }"""

new_shape_square_rect = """        else if (shape is "square")
        {
            Rect square = CanvasDrawingUtils.CenteredSquare(outer);
            float radius = (float)style.CornerRadius;
            if (radius > 0)
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(square), radius, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
            else
                SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(square), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
        }
        else if (shape is "rectangle")
        {
            float radius = (float)style.CornerRadius;
            if (radius > 0)
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), radius, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
            else
                SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle);
        }"""

old_shape_resize = """        if (ShouldDrawConnectionAnchors(block, connectorsEnabled)) 
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke);
    }"""

new_shape_resize = """        if (ShouldDrawConnectionAnchors(block, connectorsEnabled)) 
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }"""

# 6. Modify DrawTextBlock
old_text_block = """    private void DrawTextBlock(SceneBlockVisual blockVis, string? editingNoteKey, string editBody, bool editCursorVisible, int editCursorPos, int editSelectionAnchor)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        bool isEditing = editingNoteKey == block.Key;
        DrawCardShell(outer, block.IsSelected || isEditing, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#CBD5E1"), 6, block.Key.ToString());
        string text = isEditing ? editBody : block.Body ?? block.Title;
        float x = (float)outer.X + 12;
        float y = (float)outer.Y + 12;
        float w = (float)outer.Width - 24;
        if (isEditing)
            DrawEditSelection(text, 14f, x, y, w, wrap: true, sketchy: true, editCursorPos, editSelectionAnchor);
        _ctx.DrawWrappedText(text, x, y, w, (float)outer.Height - 24, 14, CanvasDrawingUtils.ParseColor(block.Style?.Text ?? "#111827"), wrap: true, sketchy: true);
        if (isEditing && editCursorVisible)
            DrawNoteCursor(text, 14f, x, y, w, editCursorPos, wrap: true, sketchy: true);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#CBD5E1"));
    }"""

new_text_block = """    private void DrawTextBlock(SceneBlockVisual blockVis, string? editingNoteKey, string editBody, bool editCursorVisible, int editCursorPos, int editSelectionAnchor)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        bool isEditing = editingNoteKey == block.Key;
        var style = block.Style ?? new BoardItemStyle();
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        WpfColor textColor = CanvasDrawingUtils.ParseColor(style.Text);
        float fontSize = Math.Clamp((float)style.FontSize, 8f, 48f);
        DrawCardShell(outer, block.IsSelected || isEditing, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "hatch", style.Opacity);
        string text = isEditing ? editBody : block.Body ?? block.Title;
        float x = (float)outer.X + 12;
        float y = (float)outer.Y + 12;
        float w = (float)outer.Width - 24;
        if (isEditing)
            DrawEditSelection(text, fontSize, x, y, w, wrap: true, sketchy: true, editCursorPos, editSelectionAnchor);
        _ctx.DrawWrappedText(text, x, y, w, (float)outer.Height - 24, fontSize, WpfColor.FromArgb((byte)(textColor.A * style.Opacity), textColor.R, textColor.G, textColor.B), wrap: true, sketchy: true);
        if (isEditing && editCursorVisible)
            DrawNoteCursor(text, fontSize, x, y, w, editCursorPos, wrap: true, sketchy: true);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }"""

old_text_resize = """// Removed redundant text resize replacement"""

new_text_resize = """// Removed redundant text resize replacement"""

# 7. Modify DrawImageBlock
old_image_block = """    private void DrawImageBlock(
        SceneBlockVisual blockVis,
        Func<string?, ImageBitmapResource?> imageLoader,
        string? hoverAnchorBlockKey,
        int? hoverAnchorIndex,
        bool isDrawingConnection,
        string? connectionSourceKey,
        int? connectionSourceAnchorIndex,
        string? connectionHoverTargetKey,
        int? connectionHoverTargetAnchorIndex,
        bool connectorsEnabled)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        DrawCardShell(outer, block.IsSelected, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#CBD5E1"), 8, block.Key.ToString());"""

new_image_block = """    private void DrawImageBlock(
        SceneBlockVisual blockVis,
        Func<string?, ImageBitmapResource?> imageLoader,
        string? hoverAnchorBlockKey,
        int? hoverAnchorIndex,
        bool isDrawingConnection,
        string? connectionSourceKey,
        int? connectionSourceAnchorIndex,
        string? connectionHoverTargetKey,
        int? connectionHoverTargetAnchorIndex,
        bool connectorsEnabled)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        var style = block.Style ?? new BoardItemStyle();
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        DrawCardShell(outer, block.IsSelected, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "hatch", style.Opacity);"""

old_image_resize = """        WpfColor stroke = CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#CBD5E1");
        if (ShouldDrawConnectionAnchors(block, connectorsEnabled))
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke);
    }"""

new_image_resize = """        if (ShouldDrawConnectionAnchors(block, connectorsEnabled))
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }"""

# 8. Modify DrawContainerBlock
old_container_resize = """        _ctx.DrawText(block.Title, x + 12, y + 8, w - 24, 12, WpfColor.FromRgb(51, 65, 85), sketchy: true);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke);
    }"""

new_container_resize = """        _ctx.DrawText(block.Title, x + 12, y + 8, w - 24, 12, WpfColor.FromRgb(51, 65, 85), sketchy: true);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }"""

# 9. Modify DrawColorGroupBlock
old_color_group_resize = """        else
        {
            if (block.IsSelected)
                DrawGenericResizeHandle(outer, stroke);
        }
    }"""

new_color_group_resize = """        else
        {
            if (block.IsSelected)
                DrawGenericResizeHandle(outer, stroke, block.IsLocked);
        }
    }"""

# 10. Update DrawCardShell and DrawGenericResizeHandle definitions
old_card_shell_defs = """    private void DrawCardShell(Rect outer, bool selected, WpfColor stroke, float radius, string seedKey)
    {
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x + 4, y + 4, w, h), _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), 1f, seedKey + "_shadow");
        var fillBrush = _ctx.GetBrush(WpfColor.FromRgb(255, 255, 255));
        WpfColor borderColor = selected ? WpfColor.FromRgb(46, 125, 215) : stroke;
        var strokeBrush = _ctx.GetBrush(borderColor);
        float sw = selected ? _ctx.InvStroke(2.0f) : _ctx.InvStroke(1.1f);
        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), fillBrush, strokeBrush, sw, seedKey);
    }

    private void DrawGenericResizeHandle(Rect outer, WpfColor color)
    {
        if (_ctx.Zoom <= UltraCompactZoom) return;
        float x = (float)outer.X;
        float y = (float)outer.Y;
        float w = (float)outer.Width;
        float h = (float)outer.Height;
        float ch = 8;
        DrawNoteCornerHandle(x - ch / 2, y - ch / 2, ch);
        DrawNoteCornerHandle(x + w - ch / 2, y - ch / 2, ch);
        DrawNoteCornerHandle(x - ch / 2, y + h - ch / 2, ch);
        DrawNoteCornerHandle(x + w - ch / 2, y + h - ch / 2, ch);
    }"""

new_card_shell_defs = """    private void DrawCardShell(Rect outer, bool selected, WpfColor stroke, float radius, string seedKey, WpfColor? fill = null, string fillStyle = "hatch", double opacity = 1.0)
    {
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        
        WpfColor fillVal = fill ?? WpfColor.FromRgb(255, 255, 255);
        byte fillA = (byte)(fillVal.A * opacity);
        byte strokeA = (byte)(stroke.A * opacity);
        
        bool isFillTransparent = fillA == 0;
        bool isStrokeTransparent = strokeA == 0;
        
        if (isFillTransparent && isStrokeTransparent)
        {
            if (selected)
            {
                WpfColor selectedStroke = WpfColor.FromRgb(46, 125, 215);
                var strokeBrush = _ctx.GetBrush(selectedStroke);
                float sw = _ctx.InvStroke(2.0f);
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), radius, null, strokeBrush, sw, seedKey, strokeStyle: _ctx.DashedStroke, fillStyle: fillStyle);
            }
            return;
        }

        if (fill != null && fillA > 0)
        {
            SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, new RectangleF(x + 4, y + 4, w, h), radius, _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), 1f, seedKey + "_shadow", fillStyle: "solid");
        }

        var opacityFill = WpfColor.FromArgb(fillA, fillVal.R, fillVal.G, fillVal.B);
        var opacityStroke = WpfColor.FromArgb(strokeA, stroke.R, stroke.G, stroke.B);
        
        var fillBrush = isFillTransparent ? null : _ctx.GetBrush(opacityFill);
        WpfColor borderColor = selected ? WpfColor.FromRgb(46, 125, 215) : opacityStroke;
        var strokeBrush = _ctx.GetBrush(borderColor);
        float sw = selected ? _ctx.InvStroke(2.0f) : _ctx.InvStroke(1.1f);
        
        SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), radius, fillBrush, strokeBrush, sw, seedKey, fillStyle: fillStyle);
    }

    private void DrawGenericResizeHandle(Rect outer, WpfColor color, bool isLocked)
    {
        if (isLocked || _ctx.Zoom <= UltraCompactZoom) return;
        float x = (float)outer.X;
        float y = (float)outer.Y;
        float w = (float)outer.Width;
        float h = (float)outer.Height;
        float ch = 8;
        DrawNoteCornerHandle(x - ch / 2, y - ch / 2, ch);
        DrawNoteCornerHandle(x + w - ch / 2, y - ch / 2, ch);
        DrawNoteCornerHandle(x - ch / 2, y + h - ch / 2, ch);
        DrawNoteCornerHandle(x + w - ch / 2, y + h - ch / 2, ch);
    }"""


# Check and replace
replacements = [
    (old_draw_block, new_draw_block, "DrawBlock switch"),
    (old_note_resize, new_note_resize, "DrawNoteBlock resize"),
    (old_code_resize, new_code_resize, "DrawCodeBlock resize"),
    (old_markdown_doc, new_markdown_doc, "DrawMarkdownDocBlock signature/params"),
    (old_markdown_resize, new_markdown_resize, "DrawMarkdownDocBlock resize"),
    (old_shape_block, new_shape_block, "DrawShapeBlock styles"),
    (old_shape_square_rect, new_shape_square_rect, "DrawShapeBlock square/rect"),
    (old_shape_resize, new_shape_resize, "DrawShapeBlock resize"),
    (old_text_block, new_text_block, "DrawTextBlock parameters"),
    (old_image_block, new_image_block, "DrawImageBlock parameters"),
    (old_image_resize, new_image_resize, "DrawImageBlock resize"),
    (old_container_resize, new_container_resize, "DrawContainerBlock resize"),
    (old_color_group_resize, new_color_group_resize, "DrawColorGroupBlock resize"),
    (old_card_shell_defs, new_card_shell_defs, "DrawCardShell and DrawGenericResizeHandle definitions")
]

all_ok = True
for old, new, desc in replacements:
    old_norm = "\n".join(line.rstrip() for line in old.replace("\r\n", "\n").split("\n"))
    new_norm = "\n".join(line.rstrip() for line in new.replace("\r\n", "\n").split("\n"))
    if old_norm in content_norm:
        content_norm = content_norm.replace(old_norm, new_norm)
        print(f"Patched: {desc}")
    else:
        # Check if already patched
        if new_norm in content_norm:
            print(f"Already patched: {desc}")
        else:
            print(f"FAILED TO MATCH: {desc}")
            all_ok = False

if all_ok:
    # Restore CRLF
    content = content_norm.replace("\n", "\r\n")
    with open(path, "w", newline="", encoding="utf-8") as f:
        f.write(content)
    print("ALL MATCHES SUCCESSFULLY PATCHED!")
else:
    print("SOME MATCHES FAILED, ABORTING WRITE.")
