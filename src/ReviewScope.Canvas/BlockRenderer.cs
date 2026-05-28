using System.Drawing;
using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;
using DWriteTextAlignment = Vortice.DirectWrite.TextAlignment;
using DWriteParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment;

namespace ReviewScope.Canvas;

/*
 * File: BlockRenderer.cs
 * Purpose: Handles the specialized rendering of all block types (File, Extract, Note, Shape, etc.) on the canvas.
 * Functions:
 * - DrawBlock: Main entry point for drawing any block, dispatching to specific type drawers.
 * - DrawCodeBlock, DrawNoteBlock, DrawShapeBlock, etc.: Specialized drawing logic for each block kind.
 * - DrawCodeBody: High-fidelity code rendering with line numbers and scope guides.
 * - DrawMicroBlock: Simplified representation for ultra-low zoom levels.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal sealed class BlockRenderer
{
    private readonly DrawingContext _ctx;

    /// <summary>Snapshot of blocks for resolving linear shape attachments. Set by viewport per frame.</summary>
    public IReadOnlyDictionary<string, SceneBlockVisual>? BlockLookup { get; set; }

    // Viewport-specific constants (should match CanvasViewport)
    private const double HeaderH = 56;
    private const double FooterH = 28;
    private const double CodeGutterW = 54;
    private const double CodeTextPadX = 12;
    private const double CodeScrollbarReserveW = 34;
    private const double CodeLineH = 18;
    private const double CodeCharW = 7.2;
    private const int FocusedCodeTopPaddingLines = 2;
    private const double CompactZoom = 0.45;
    private const double PreviewZoom = 0.75;
    private const double UltraCompactZoom = 0.15;

    public BlockRenderer(DrawingContext ctx)
    {
        _ctx = ctx;
    }

    public void DrawBlock(
        SceneBlockVisual blockVis,
        string? editingNoteKey,
        string? editingGroupKey,
        string editTitle,
        string editBody,
        bool editingTitle,
        bool editCursorVisible,
        int editCursorPos,
        int editSelectionAnchor,
        bool isExtractMode,
        string? hoverAnchorBlockKey,
        int? hoverAnchorIndex,
        bool isDrawingConnection,
        string? connectionSourceKey,
        int? connectionSourceAnchorIndex,
        string? connectionHoverTargetKey,
        int? connectionHoverTargetAnchorIndex,
        bool connectorsEnabled,
        Dictionary<string, int> codeScrollLines,
        Func<string?, ImageBitmapResource?> imageLoader)
    {
        var block = blockVis.Block;
        if (_ctx.Zoom <= UltraCompactZoom) { DrawMicroBlock(blockVis); return; }

        switch (block.Kind)
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
    }

    private void DrawMicroBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        WpfColor fill = block.Kind switch
        {
            BlockKind.File => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Extract => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Note => WpfColor.FromRgb(255, 245, 201),
            BlockKind.MarkdownDoc => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Shape => CanvasDrawingUtils.ParseColor(block.Style?.Fill ?? "#EFF6FF"),
            BlockKind.Text => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Image => WpfColor.FromRgb(248, 250, 252),
            BlockKind.Container => WpfColor.FromRgb(248, 250, 252),
            _ => WpfColor.FromRgb(255, 255, 255)
        };
        WpfColor border = block.IsSelected
            ? WpfColor.FromArgb(230, 46, 125, 215)
            : block.Kind == BlockKind.Extract
                ? WpfColor.FromArgb(150, 35, 162, 109)
                : WpfColor.FromArgb(130, 46, 125, 215);

        var rr = new RoundedRectangle(CanvasDrawingUtils.ToRF(blockVis.Bounds), 8, 8);
        _ctx.RenderTarget.FillRoundedRectangle(rr, _ctx.GetBrush(fill));
        _ctx.RenderTarget.DrawRoundedRectangle(rr, _ctx.GetBrush(border), _ctx.InvStroke(1.0f));
    }

    private void DrawCodeBlock(
        SceneBlockVisual blockVis,
        Dictionary<string, int> codeScrollLines,
        bool isExtractMode,
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
        bool compact = _ctx.Zoom <= CompactZoom;
        bool preview = !compact && _ctx.Zoom <= PreviewZoom;
        bool isFocused = block.Focused is not null;
        bool isMarkdown = IsMarkdownFileBlock(block);

        WpfColor accent = isFocused
            ? WpfColor.FromRgb(46, 125, 215)
            : isMarkdown
                ? WpfColor.FromRgb(148, 163, 184)
            : block.Kind == BlockKind.Extract
                ? WpfColor.FromRgb(35, 162, 109)
                : WpfColor.FromRgb(46, 125, 215);

        WpfColor border = block.IsSelected
            ? WpfColor.FromArgb(235, 46, 125, 215)
            : isMarkdown
                ? WpfColor.FromArgb(210, 148, 163, 184)
            : WpfColor.FromArgb(170, accent.R, accent.G, accent.B);

        Rect outer = blockVis.Bounds;
        Rect header = new(outer.X, outer.Y, outer.Width, HeaderH);
        Rect footer = new(outer.X, outer.Bottom - FooterH, outer.Width, FooterH);
        Rect code = new(outer.X + 1, header.Bottom, outer.Width - 2, outer.Height - HeaderH - FooterH - 1);

        // Drop shadow (subtle)
        var shadow = new RoundedRectangle(new RectangleF((float)outer.X + 2, (float)outer.Y + 5, (float)outer.Width, (float)outer.Height), 8, 8);
        _ctx.RenderTarget.FillRoundedRectangle(shadow, _ctx.GetBrush(WpfColor.FromArgb(18, 35, 49, 66)));

        // Main card fill
        var rr = new RoundedRectangle(CanvasDrawingUtils.ToRF(outer), 8, 8);
        _ctx.RenderTarget.FillRoundedRectangle(rr, _ctx.GetBrush(WpfColor.FromRgb(255, 255, 255)));

        // Selection glow if selected
        if (block.IsSelected)
        {
            var glow = new RoundedRectangle(new RectangleF((float)outer.X - 2.5f, (float)outer.Y - 2.5f, (float)outer.Width + 5f, (float)outer.Height + 5f), 10, 10);
            _ctx.RenderTarget.DrawRoundedRectangle(glow, _ctx.GetBrush(WpfColor.FromArgb(45, 46, 125, 215)), _ctx.InvStroke(4.0f));
        }

        _ctx.RenderTarget.DrawRoundedRectangle(rr, _ctx.GetBrush(border), _ctx.InvStroke(block.IsSelected ? 2.0f : 1.15f));

        // Header background (top 2 corners rounded)
        _ctx.RenderTarget.FillRectangle(new RectangleF((float)header.X + 1, (float)header.Y + 1, (float)header.Width - 2, (float)header.Height - 1),
            _ctx.GetBrush(WpfColor.FromArgb(12, accent.R, accent.G, accent.B)));

        // Header separator
        _ctx.RenderTarget.DrawLine(new Vector2((float)header.Left, (float)header.Bottom), new Vector2((float)header.Right, (float)header.Bottom),
            _ctx.GetBrush(WpfColor.FromArgb(110, 226, 232, 240)), _ctx.InvStroke(1.0f));

        // Block Title / Icon
        string icon = block.Kind == BlockKind.Extract ? "f " : ":: ";
        string titleText = block.IsSelected && block.Focused != null
            ? icon + block.Focused.SymbolName
            : icon + block.Title;
        _ctx.DrawText(titleText, (float)outer.X + 14, (float)outer.Y + 12, (float)outer.Width - 60, 14, WpfColor.FromRgb(31, 41, 51));

        string subtitle = isFocused
            ? $"in {block.Title}  *  lines {block.Focused!.StartLine}-{block.Focused.EndLine}"
            : block.Subtitle;
        _ctx.DrawText(subtitle, (float)outer.X + 14, (float)outer.Y + 36, (float)outer.Width - 60, 10, WpfColor.FromRgb(117, 128, 143));

        if (block.IsCollapsed)
        {
            _ctx.DrawText("[ collapsed - double-click to expand ]",
                (float)code.X + 16, (float)code.Y + 12, (float)code.Width - 32, 11, WpfColor.FromRgb(117, 128, 143));
        }
        else if (!string.IsNullOrWhiteSpace(block.Body))
        {
            if (compact)
                DrawPreviewBars(code, accent, 4);
            else if (preview)
                DrawPreviewBars(code, accent, 9);
            else if (isMarkdown)
                DrawMarkdownFileBody(block, code, codeScrollLines);
            else
                DrawCodeBody(block, code, codeScrollLines, isExtractMode);
        }

        // Footer text
        string footerFileName = block.FilePath is not null ? System.IO.Path.GetFileName(block.FilePath) : string.Empty;
        string footerText = (block.Kind == BlockKind.Extract && block.StartLine.HasValue)
            ? $"lines {block.StartLine}-{block.EndLine}  *  {footerFileName}"
            : isFocused
                ? $"focused - Ctrl+click another line to refocus  *  {footerFileName}"
                : footerFileName;
        _ctx.DrawText(footerText, (float)outer.X + 14, (float)footer.Y + 8, (float)outer.Width - 60, 9.5f, WpfColor.FromRgb(117, 128, 143));

        // Resize handle
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, accent, block.IsLocked);

        // Restore button for focused blocks
        if (isFocused) DrawRestoreButton(outer, accent);

        if (ShouldDrawConnectionAnchors(block, connectorsEnabled))
            DrawConnectionAnchors(block, outer, accent, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
    }

    private void DrawNoteBlock(SceneBlockVisual blockVis, string? editingNoteKey, string editTitle, string editBody, bool editingTitle, bool editCursorVisible, int editCursorPos, int editSelectionAnchor)
    {
        var block = blockVis.Block;
        bool isSelected = block.IsSelected;
        bool isEditing = editingNoteKey == block.Key;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        float titleW = Math.Max(1f, w - 48f);
        float bodyClipW = w - 24f;
        float bodyW = w - 28f;
        float bodyH = h - 54f;

        string bodyText = isEditing ? editBody : (block.Body ?? string.Empty);

        // Resolve styled font sizes
        var style = block.Style ?? new BoardItemStyle();
        float baseFontSize = Math.Clamp((float)style.FontSize, 8f, 48f);
        float bodyFontSize = baseFontSize;
        float titleFontSize = baseFontSize + 1.5f;

        // Shadow
        _ctx.RenderTarget.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(x + 2, y + 5, w, h), 8, 8),
            _ctx.GetBrush(WpfColor.FromArgb(20, 35, 49, 66)));

        // Selection / edit ring
        if (isSelected || isEditing)
        {
            var glow = new RoundedRectangle(new RectangleF(x - 2.5f, y - 2.5f, w + 5f, h + 5f), 10, 10);
            _ctx.RenderTarget.DrawRoundedRectangle(glow, _ctx.GetBrush(WpfColor.FromArgb(55, 230, 175, 40)), _ctx.InvStroke(4.0f));
        }

        // Card fill — honor user-picked colors from the inspector while still keeping
        // the default note look when no override is set.
        WpfColor fillColor = style.BackgroundColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Fill)
            : WpfColor.FromRgb(255, 249, 219);
        WpfColor strokeColor = style.BorderColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Stroke)
            : WpfColor.FromArgb(180, 226, 186, 76);
        WpfColor bodyColor = style.FontColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Text)
            : WpfColor.FromRgb(100, 75, 25);
        WpfColor titleColor = WpfColor.FromArgb(
            (byte)Math.Clamp((int)(bodyColor.A * 1.0), 0, 255),
            (byte)Math.Min(255, bodyColor.R + 40),
            (byte)Math.Min(255, bodyColor.G + 25),
            (byte)Math.Max(0, bodyColor.B - 10));

        var rr = new RoundedRectangle(CanvasDrawingUtils.ToRF(outer), 6, 6);
        _ctx.RenderTarget.FillRoundedRectangle(rr, _ctx.GetBrush(fillColor));
        _ctx.RenderTarget.DrawRoundedRectangle(rr, _ctx.GetBrush(strokeColor), _ctx.InvStroke(1.1f));

        // Pin icon (sketchy)
        SketchyDrawer.DrawLine(_ctx.RenderTarget, new Vector2(x + 12, y + 14), new Vector2(x + 12, y + 24), _ctx.GetBrush(WpfColor.FromRgb(215, 145, 40)), _ctx.InvStroke(1.5f), block.Key + "_pin");

        // Title
        string titleText = isEditing ? editTitle : block.Title;
        if (isEditing && editingTitle)
            DrawEditSelection(titleText, titleFontSize, x + 24, y + 12, titleW, wrap: false, sketchy: false, editCursorPos, editSelectionAnchor);

        _ctx.DrawText(titleText, x + 24, y + 12, titleW, titleFontSize, titleColor);

        if (isEditing && editingTitle && editCursorVisible)
            DrawNoteCursor(titleText, titleFontSize, x + 24, y + 12, titleW, editCursorPos);

        // Body
        if (bodyClipW > 0f && bodyW > 0f && bodyH > 0f)
        {
            _ctx.RenderTarget.PushAxisAlignedClip(new RectangleF(x + 12, y + 42, bodyClipW, bodyH), AntialiasMode.PerPrimitive);
            float bx = x + 14, by = y + 42;
            string noteFontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily;

            if (OutlineDocument.IsOutlineBlock(block))
            {
                OutlineDocument.Draw(
                    _ctx,
                    block,
                    new Rect(bx, by, bodyW, bodyH),
                    bodyText,
                    bodyColor,
                    bodyFontSize,
                    noteFontFamily,
                    bold: style.Bold,
                    italic: style.Italic,
                    editCursorPos: (isEditing && !editingTitle) ? editCursorPos : -1,
                    editSelectionAnchor: (isEditing && !editingTitle) ? editSelectionAnchor : -1,
                    editCursorVisible: isEditing && !editingTitle && editCursorVisible);
            }
            else
            {
                if (isEditing && !editingTitle)
                    DrawEditSelection(bodyText, bodyFontSize, bx, by, bodyW, wrap: true, sketchy: false, editCursorPos, editSelectionAnchor);

                _ctx.DrawWrappedText(bodyText, bx, by, bodyW, bodyH, bodyFontSize, bodyColor, wrap: true);

                if (isEditing && !editingTitle && editCursorVisible)
                    DrawNoteCursor(bodyText, bodyFontSize, bx, by, bodyW, editCursorPos, wrap: true);
            }

            _ctx.RenderTarget.PopAxisAlignedClip();
        }

        // Corner handles for notes
        if ((isSelected || isEditing) && !block.IsLocked)
        {
            float ch = 8;
            DrawNoteCornerHandle(x - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y - ch / 2, ch);
            DrawNoteCornerHandle(x - ch / 2, y + h - ch / 2, ch);
            DrawNoteCornerHandle(x + w - ch / 2, y + h - ch / 2, ch);
        }
    }

    private void DrawNoteCornerHandle(float x, float y, float size)
    {
        var rect = new RectangleF(x, y, size, size);
        _ctx.RenderTarget.FillRectangle(rect, _ctx.GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _ctx.RenderTarget.DrawRectangle(rect, _ctx.GetBrush(WpfColor.FromArgb(220, 46, 125, 215)), _ctx.InvStroke(1.0f));
    }

    private void DrawMarkdownDocBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        var style = block.Style ?? new BoardItemStyle();
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        DrawCardShell(outer, block.IsSelected, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "hatch", style.Opacity, (float)style.HatchOpacity);
        _ctx.RenderTarget.FillRectangle(new RectangleF(x, y, w, 46), _ctx.GetBrush(WpfColor.FromRgb(248, 250, 252)));
        _ctx.DrawText(block.Title, x + 16, y + 12, w - 32, 15, WpfColor.FromRgb(17, 24, 39));
        _ctx.DrawText(block.Subtitle, x + 16, y + 30, w - 32, 9.5f, WpfColor.FromRgb(100, 116, 139));
        _ctx.RenderTarget.DrawLine(new Vector2(x, y + 46), new Vector2(x + w, y + 46), _ctx.GetBrush(WpfColor.FromArgb(160, 226, 232, 240)), _ctx.InvStroke(1));

        _ctx.RenderTarget.PushAxisAlignedClip(new RectangleF(x + 14, y + 58, w - 28, h - 72), AntialiasMode.PerPrimitive);
        float cy = y + 58;
        string[] lines = (block.Body ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < Math.Min(lines.Length, 80); i++)
        {
            string rawLine = lines[i];
            string line = rawLine.TrimEnd();
            if (line.Length == 0) { cy += 10; continue; }
            string trimmed = line.Trim();
            if (IsMarkdownTableRow(trimmed))
            {
                if (LooksLikeTableSeparator(trimmed)) continue;
                DrawMarkdownTableRow(trimmed, x + 18, cy, w - 42, header: i + 1 < lines.Length && LooksLikeTableSeparator(lines[i + 1].Trim()));
                cy += 24;
            }
            else
            {
            int level = line.TakeWhile(c => c == '#').Count();
            if (level > 0 && level <= 4 && line.Length > level && line[level] == ' ')
            {
                float size = level == 1 ? 18 : level == 2 ? 15 : 13;
                _ctx.DrawText(line[(level + 1)..], x + 18, cy, w - 42, size, WpfColor.FromRgb(15, 23, 42));
                cy += size + 12;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                _ctx.DrawText("*", x + 20, cy, 16, 12, WpfColor.FromRgb(46, 125, 215));
                _ctx.DrawWrappedText(line[2..], x + 38, cy, w - 58, 42, 11.5f, WpfColor.FromRgb(51, 65, 85), wrap: true);
                cy += 24;
            }
            else
            {
                _ctx.DrawWrappedText(line, x + 18, cy, w - 42, 48, 11.5f, WpfColor.FromRgb(51, 65, 85), wrap: true);
                cy += 24;
            }
            }
            if (cy > y + h - 20) break;
        }
        _ctx.RenderTarget.PopAxisAlignedClip();
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#2E7DD7"), block.IsLocked);
    }

    private void DrawShapeBlock(
        SceneBlockVisual blockVis,
        string? editingNoteKey,
        string editTitle,
        string editBody,
        bool editCursorVisible,
        int editCursorPos,
        int editSelectionAnchor,
        string? hoverAnchorBlockKey,
        int? hoverAnchorIndex,
        bool isDrawingConnection,
        string? connectionSourceKey,
        int? connectionSourceAnchorIndex,
        string? connectionHoverTargetKey,
        int? connectionHoverTargetAnchorIndex,
        bool connectorsEnabled,
        Func<string?, ImageBitmapResource?> imageLoader)
    {
        var block = blockVis.Block;
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
        string fillStyle = style.FillStyle ?? "hatch";
        float hatchOp = (float)style.HatchOpacity;

        if (IsLinearShapeTool(shape))
        {
            DrawLinearShape(block, outer, stroke, strokeWidth, dashed);
            return;
        }
        else if (shape is "database" or "cache" or "queue")
        {
            SketchyDrawer.DrawEllipse(_ctx.RenderTarget, new RectangleF(x, y, w, Math.Min(h, 24f)), fillBrush, strokeBrush, strokeWidth, block.Key.ToString() + "_db_top", strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
            SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x, y + 12f, w, h - 12f), fillBrush, strokeBrush, strokeWidth, block.Key.ToString() + "_db_body", strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "circle" or "oval")
        {
            Rect ellipseBounds = shape == "circle" ? CanvasDrawingUtils.CenteredSquare(outer) : outer;
            SketchyDrawer.DrawEllipse(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(ellipseBounds), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "triangle")
        {
            SketchyDrawer.DrawPolygon(_ctx.RenderTarget, new[]
            {
                new Vector2(x + w / 2f, y),
                new Vector2(x + w, y + h),
                new Vector2(x, y + h)
            }, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), close: true, strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "risk" or "decision" or "diamond")
        {
            SketchyDrawer.DrawPolygon(_ctx.RenderTarget, new[]
            {
                new Vector2(x + w / 2f, y),
                new Vector2(x + w, y + h / 2f),
                new Vector2(x + w / 2f, y + h),
                new Vector2(x, y + h / 2f)
            }, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), close: true, strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "star")
        {
            SketchyDrawer.DrawPolygon(_ctx.RenderTarget, BuildStarPoints(outer).ToArray(), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), close: true, strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "hexagon")
        {
            SketchyDrawer.DrawPolygon(_ctx.RenderTarget, BuildRegularPolygonPoints(outer, 6, -MathF.PI / 6).ToArray(), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), close: true, strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "square")
        {
            Rect square = CanvasDrawingUtils.CenteredSquare(outer);
            float radius = (float)style.CornerRadius;
            if (radius > 0)
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(square), radius, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
            else
                SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(square), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else if (shape is "rectangle")
        {
            float radius = (float)style.CornerRadius;
            if (radius > 0)
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), radius, fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
            else
                SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }
        else
        {
            SketchyDrawer.DrawRectangle(_ctx.RenderTarget, CanvasDrawingUtils.ToRF(outer), fillBrush, strokeBrush, strokeWidth, block.Key.ToString(), strokeStyle: strokeStyle, fillStyle: fillStyle, hatchOpacity: hatchOp);
        }

        bool isEditing = editingNoteKey == block.Key;
        if (!IsLinearShapeTool(shape))
        {
            string label = isEditing
                ? editBody
                : string.IsNullOrWhiteSpace(block.Body) ? block.Title : block.Body!;
            if (isEditing)
            {
                float textX = x + 14;
                float textY = y + h / 2 - 8;
                float textW = w - 28;
                DrawEditSelection(label, 13f, textX, textY, textW, wrap: false, sketchy: true, editCursorPos, editSelectionAnchor);
                _ctx.DrawText(label, textX, textY, textW, 13, CanvasDrawingUtils.ParseColor(block.Style?.Text ?? "#111827"), sketchy: true);
                if (editCursorVisible)
                    DrawNoteCursor(label, 13f, textX, textY, textW, editCursorPos, wrap: false, sketchy: true);
            }
            else
            {
                DrawShapeText(label, outer, CanvasDrawingUtils.ParseColor(block.Style?.Text ?? "#111827"), block.Style?.TextAlign);
            }
        }
        if (ShouldDrawConnectionAnchors(block, connectorsEnabled))
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }

    private void DrawLinearShape(RenderBlock block, Rect outer, WpfColor stroke, float strokeWidth, bool dashed)
    {
        var points = CanvasDrawingUtils.ResolveLinearShapePoints(block, outer, BlockLookup);
        if (points.Count < 2) return;

        var body = CanvasDrawingUtils.ParseLinearShapeBody(block.Body);
        var curvedFlags = body.CurvedFlags;

        var brush = _ctx.GetBrush(stroke);
        var strokeStyle = dashed ? _ctx.DashedStroke : null;

        using var path = _ctx.Factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(new Vector2((float)points[0].X, (float)points[0].Y), FigureBegin.Hollow);

        // Walk corner-to-corner. Consecutive curved (shift-clicked) vertices between two corners
        // form a single Bézier span — the curved vertices are control points, not on-curve points.
        // This gives wide arcs instead of small rounded corners stacked together.
        int idx = 0;
        while (idx < points.Count - 1)
        {
            int nextCorner = idx + 1;
            while (nextCorner < points.Count - 1
                   && curvedFlags is not null
                   && nextCorner < curvedFlags.Count
                   && curvedFlags[nextCorner])
                nextCorner++;

            int controlCount = nextCorner - idx - 1;
            Vector2 endCorner = new((float)points[nextCorner].X, (float)points[nextCorner].Y);

            if (controlCount == 0)
            {
                sink.AddLine(endCorner);
            }
            else if (controlCount == 1)
            {
                Vector2 c1 = new((float)points[idx + 1].X, (float)points[idx + 1].Y);
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = c1, Point2 = endCorner });
            }
            else if (controlCount == 2)
            {
                Vector2 c1 = new((float)points[idx + 1].X, (float)points[idx + 1].Y);
                Vector2 c2 = new((float)points[idx + 2].X, (float)points[idx + 2].Y);
                sink.AddBezier(new Vortice.Direct2D1.BezierSegment { Point1 = c1, Point2 = c2, Point3 = endCorner });
            }
            else
            {
                // 3+ controls: chain of quadratic Béziers through midpoints (quadratic B-spline).
                // Each control point is used as a Bézier handle; the curve is joined smoothly
                // at the midpoint between consecutive controls.
                for (int k = 0; k < controlCount; k++)
                {
                    Vector2 ctrl = new((float)points[idx + 1 + k].X, (float)points[idx + 1 + k].Y);
                    Vector2 end;
                    if (k < controlCount - 1)
                    {
                        Vector2 ctrlNext = new((float)points[idx + 2 + k].X, (float)points[idx + 2 + k].Y);
                        end = (ctrl + ctrlNext) * 0.5f;
                    }
                    else
                    {
                        end = endCorner;
                    }
                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = ctrl, Point2 = end });
                }
            }

            idx = nextCorner;
        }
        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        if (strokeStyle is not null)
            _ctx.RenderTarget.DrawGeometry(path, brush, strokeWidth, strokeStyle);
        else
            _ctx.RenderTarget.DrawGeometry(path, brush, strokeWidth);

        if (block.ShapeType is "arrow")
            DrawArrowhead(points[^1], points[^2], brush, strokeWidth);

        // Vertex control dots on selected linear shapes
        if (block.IsSelected && points.Count >= 2)
            DrawElbowDots(points, stroke, curvedFlags);
    }

    private void DrawElbowDots(IReadOnlyList<System.Windows.Point> points, WpfColor accent, IReadOnlyList<bool>? curvedFlags)
    {
        float r = Math.Max(3f, _ctx.InvStroke(4.5f));
        var fillBrush = _ctx.GetBrush(WpfColor.FromArgb(245, 255, 255, 255));
        var strokeBrush = _ctx.GetBrush(WpfColor.FromArgb(235, 32, 104, 192));
        float sw = _ctx.InvStroke(1.2f);
        for (int i = 0; i < points.Count; i++)
        {
            var c = new Vector2((float)points[i].X, (float)points[i].Y);
            bool isCurved = curvedFlags is not null && i < curvedFlags.Count && curvedFlags[i];
            if (isCurved)
            {
                _ctx.RenderTarget.FillEllipse(new Ellipse(c, r, r), fillBrush);
                _ctx.RenderTarget.DrawEllipse(new Ellipse(c, r, r), strokeBrush, sw);
            }
            else
            {
                var rect = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
                _ctx.RenderTarget.FillRectangle(rect, fillBrush);
                _ctx.RenderTarget.DrawRectangle(rect, strokeBrush, sw);
            }
        }
    }

    private void DrawArrowhead(System.Windows.Point tip, System.Windows.Point from, ID2D1SolidColorBrush brush, float stroke)
    {
        Vector2 dir = new((float)(tip.X - from.X), (float)(tip.Y - from.Y));
        float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.01f) return;
        dir /= len;
        Vector2 perp = new(-dir.Y, dir.X);
        float aLen = Math.Max(8f, stroke * 5);
        Vector2 tipV = new((float)tip.X, (float)tip.Y);
        Vector2 left = tipV - dir * aLen + perp * (aLen * 0.45f);
        Vector2 right = tipV - dir * aLen - perp * (aLen * 0.45f);
        using var path = _ctx.Factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(tipV, FigureBegin.Filled);
        sink.AddLine(left);
        sink.AddLine(right);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        _ctx.RenderTarget.FillGeometry(path, brush);
    }

    private void DrawShapeText(string text, Rect outer, WpfColor color, string? alignment)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        const float pad = 12f;
        float x = (float)outer.X + pad;
        float y = (float)outer.Y + pad;
        float w = Math.Max(4, (float)outer.Width - pad * 2);
        float h = Math.Max(4, (float)outer.Height - pad * 2);
        IDWriteTextFormat fmt = _ctx.GetTextFormat(13f, sketchy: true);
        var oldTextAlign = fmt.TextAlignment;
        var oldParagraphAlign = fmt.ParagraphAlignment;
        fmt.TextAlignment = ToDWriteTextAlignment(alignment);
        fmt.ParagraphAlignment = DWriteParagraphAlignment.Center;
        using var layout = _ctx.DWriteFactory.CreateTextLayout(text, fmt, w, h);
        layout.WordWrapping = WordWrapping.Wrap;
        _ctx.RenderTarget.DrawTextLayout(new Vector2(x, y), layout, _ctx.GetBrush(color), DrawTextOptions.Clip);
        fmt.TextAlignment = oldTextAlign;
        fmt.ParagraphAlignment = oldParagraphAlign;
    }

    private static DWriteTextAlignment ToDWriteTextAlignment(string? alignment) =>
        alignment?.Trim().ToLowerInvariant() switch
        {
            "left" => DWriteTextAlignment.Leading,
            "right" => DWriteTextAlignment.Trailing,
            _ => DWriteTextAlignment.Center
        };

    private void DrawTextBlock(SceneBlockVisual blockVis, string? editingNoteKey, string editBody, bool editCursorVisible, int editCursorPos, int editSelectionAnchor)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        bool isEditing = editingNoteKey == block.Key;
        var style = block.Style ?? new BoardItemStyle();

        // Background / border are conditionally drawn based on the enable flags from the inspector.
        WpfColor fill = style.BackgroundColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Fill)
            : WpfColor.FromArgb(0, 0, 0, 0);
        WpfColor stroke = style.BorderColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Stroke)
            : WpfColor.FromArgb(0, 0, 0, 0);
        WpfColor textColor = style.FontColorEnabled
            ? CanvasDrawingUtils.ParseColor(style.Text)
            : WpfColor.FromArgb(0, 0, 0, 0);

        // Shell still draws selection outline regardless of fill/stroke visibility.
        DrawCardShell(outer, block.IsSelected || isEditing, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "solid", style.Opacity);

        string text = isEditing ? editBody : block.Body ?? block.Title;

        // Auto font-size: choose the largest size that fits the box height.
        float baseFont = (float)Math.Clamp(style.FontSize, 8, 96);
        float fontSize = baseFont;
        if (style.AutoFontSize && !string.IsNullOrEmpty(text))
        {
            // Simple heuristic: fit roughly height/(1.4*lines) where lines is estimated.
            float lines = Math.Max(1, text.Split('\n').Length);
            fontSize = (float)Math.Clamp((outer.Height - 8) / (lines * 1.4f), 6f, 96f);
        }

        // Spacing (padding) inside the text box.
        float padL = (float)Math.Max(0, style.SpacingLeft);
        float padR = (float)Math.Max(0, style.SpacingRight);
        float padT = (float)Math.Max(0, style.SpacingTop);
        float padB = (float)Math.Max(0, style.SpacingBottom);

        float x = (float)outer.X + padL;
        float y = (float)outer.Y + padT;
        float w = Math.Max(4, (float)outer.Width - padL - padR);
        float h = Math.Max(4, (float)outer.Height - padT - padB);

        var hAlign = ToDWriteTextAlignment(style.TextAlign);
        var vAlign = ToParagraphAlignment(style.VerticalAlign);
        bool wrap = style.WordWrap;

        string editFontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily;

        var renderColor = WpfColor.FromArgb((byte)(textColor.A * style.Opacity), textColor.R, textColor.G, textColor.B);

        if (OutlineDocument.IsOutlineBlock(block))
        {
            // Outline draw handles bullets + indent guides + per-line text + caret +
            // selection together so the cursor lands inside the *visible* content
            // rather than mid-prefix (Logseq-style).
            OutlineDocument.Draw(
                _ctx,
                block,
                new Rect(x, y, w, h),
                text,
                renderColor,
                fontSize,
                editFontFamily,
                style.Bold,
                style.Italic,
                editCursorPos: isEditing ? editCursorPos : -1,
                editSelectionAnchor: isEditing ? editSelectionAnchor : -1,
                editCursorVisible: isEditing && editCursorVisible);
        }
        else
        {
            if (isEditing)
                DrawEditSelection(text, fontSize, x, y, w, wrap: wrap, sketchy: false, editCursorPos, editSelectionAnchor, hAlign, maxH: h, paragraphAlignment: vAlign, fontFamily: editFontFamily, bold: style.Bold, italic: style.Italic);

            _ctx.DrawRichText(
                text,
                x, y, w, h,
                editFontFamily,
                fontSize,
                style.Bold,
                style.Italic,
                style.Underline,
                style.Strikethrough,
                renderColor,
                hAlign,
                vAlign,
                wrap,
                style.ShadowEnabled);

            if (isEditing && editCursorVisible)
                DrawNoteCursor(text, fontSize, x, y, w, editCursorPos, wrap: wrap, sketchy: false, hAlign, maxH: h, paragraphAlignment: vAlign, fontFamily: editFontFamily, bold: style.Bold, italic: style.Italic);
        }

        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }

    private static DWriteParagraphAlignment ToParagraphAlignment(string? v) =>
        v?.Trim().ToLowerInvariant() switch
        {
            "top" => DWriteParagraphAlignment.Near,
            "bottom" => DWriteParagraphAlignment.Far,
            _ => DWriteParagraphAlignment.Center
        };

    private void DrawImageBlock(
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
        DrawCardShell(outer, block.IsSelected, stroke, (float)style.CornerRadius, block.Key.ToString(), fill, style.FillStyle ?? "hatch", style.Opacity, (float)style.HatchOpacity);
        _ctx.DrawText(block.Title, x + 14, y + 12, w - 28, 12, WpfColor.FromRgb(30, 41, 59), sketchy: true);

        var imageArea = new Rect(outer.X + 12, outer.Y + 38, Math.Max(1, outer.Width - 24), Math.Max(1, outer.Height - 56));
        _ctx.RenderTarget.FillRectangle(CanvasDrawingUtils.ToRF(imageArea), _ctx.GetBrush(WpfColor.FromRgb(241, 245, 249)));
        var image = imageLoader(block.Source?.AssetPath ?? block.Source?.SourcePath);
        if (image is not null)
        {
            Rect dest = FitRect(imageArea, image.PixelWidth, image.PixelHeight);
            var target = new Vortice.Mathematics.Rect((float)dest.Left, (float)dest.Top, (float)dest.Width, (float)dest.Height);
            var source = new Vortice.Mathematics.Rect(0, 0, image.PixelWidth, image.PixelHeight);
            _ctx.RenderTarget.DrawBitmap(image.Bitmap, target, (float)Math.Clamp(block.Style?.Opacity ?? 1, 0.05, 1), BitmapInterpolationMode.Linear, source);
        }
        else
        {
            _ctx.DrawText("Image unavailable", x + 20, y + h / 2 - 10, w - 40, 13, WpfColor.FromRgb(100, 116, 139), sketchy: true);
            _ctx.DrawWrappedText(block.Source?.AssetPath ?? block.Body ?? string.Empty, x + 20, y + h / 2 + 12, w - 40, 42, 9, WpfColor.FromRgb(100, 116, 139), wrap: true, sketchy: true);
        }

        stroke = CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#CBD5E1");
        if (ShouldDrawConnectionAnchors(block, connectorsEnabled))
            DrawConnectionAnchors(block, outer, stroke, hoverAnchorBlockKey, hoverAnchorIndex, isDrawingConnection, connectionSourceKey, connectionSourceAnchorIndex, connectionHoverTargetKey, connectionHoverTargetAnchorIndex);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }

    private void DrawContainerBlock(
        SceneBlockVisual blockVis,
        string? editingGroupKey,
        string editTitle,
        bool editCursorVisible,
        int editCursorPos,
        int editSelectionAnchor,
        Dictionary<string, int> codeScrollLines)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        if (IsColorGroup(block))
        {
            DrawColorGroupBlock(blockVis, editingGroupKey, editTitle, editCursorVisible, editCursorPos, editSelectionAnchor);
            return;
        }

        WpfColor stroke = CanvasDrawingUtils.ParseColor(block.Style?.Stroke ?? "#64748B");
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;

        var fillBrush = _ctx.GetBrush(WpfColor.FromArgb(58, 248, 250, 252));
        var strokeBrush = _ctx.GetBrush(stroke);
        float sw = block.IsSelected ? _ctx.InvStroke(2.0f) : _ctx.InvStroke(1.3f);

        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x + 4, y + 4, w, h), _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), _ctx.GetBrush(WpfColor.FromArgb(12, 35, 49, 66)), 1f, block.Key.ToString() + "_shadow");
        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), fillBrush, strokeBrush, sw, block.Key.ToString());
        _ctx.RenderTarget.FillRectangle(new RectangleF(x + 2, y + 2, w - 4, 30), _ctx.GetBrush(WpfColor.FromArgb(54, stroke.R, stroke.G, stroke.B)));
        SketchyDrawer.DrawLine(_ctx.RenderTarget, new Vector2(x, y + 32), new Vector2(x + w, y + 32), strokeBrush, sw, block.Key.ToString() + "_header");

        _ctx.DrawText(block.Title, x + 12, y + 8, w - 24, 12, WpfColor.FromRgb(51, 65, 85), sketchy: true);
        if (block.IsSelected)
            DrawGenericResizeHandle(outer, stroke, block.IsLocked);
    }

    private void DrawColorGroupBlock(SceneBlockVisual blockVis, string? editingGroupKey, string editTitle, bool editCursorVisible, int editCursorPos, int editSelectionAnchor)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        var style = block.Style ?? new BoardItemStyle("#EAF4FF", "#2E7DD7", "#17324D", 1.6, Opacity: 0.18);
        WpfColor fill = CanvasDrawingUtils.ParseColor(style.Fill);
        WpfColor stroke = CanvasDrawingUtils.ParseColor(style.Stroke);
        WpfColor text = CanvasDrawingUtils.ParseColor(style.Text);
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        bool isEditing = editingGroupKey == block.Key;
        string titleText = isEditing ? editTitle : block.Title;

        var fillBrush = _ctx.GetBrush(WpfColor.FromArgb((byte)Math.Clamp(style.Opacity * 255, 24, 72), fill.R, fill.G, fill.B));
        WpfColor borderColor = block.IsSelected ? WpfColor.FromRgb(46, 125, 215) : stroke;
        var strokeBrush = _ctx.GetBrush(borderColor);
        float sw = block.IsSelected ? _ctx.InvStroke(2.2f) : _ctx.InvStroke((float)style.StrokeWidth);

        SketchyDrawer.DrawRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), fillBrush, strokeBrush, sw, block.Key.ToString(), fillStyle: style.FillStyle);

        float titleSize = Math.Clamp((float)style.FontSize, 8f, 48f);
        float headerH = Math.Max(block.IsCollapsed ? 42 : 36, titleSize + (block.IsCollapsed ? 28 : 20));
        _ctx.RenderTarget.FillRectangle(new RectangleF(x + 2, y + 2, w - 4, headerH - 2), _ctx.GetBrush(WpfColor.FromArgb(48, fill.R, fill.G, fill.B)));
        SketchyDrawer.DrawLine(_ctx.RenderTarget, new Vector2(x, y + headerH), new Vector2(x + w, y + headerH), strokeBrush, sw, block.Key.ToString() + "_header");

        float titleW = w - 58;
        float titlePadY = Math.Max(3f, (headerH - titleSize) / 2f - 3f);
        var titleBacking = new RectangleF(x + 10, y + titlePadY, titleW + 8, titleSize + 8);
        if (isEditing)
        {
            var inputBgBrush = _ctx.GetBrush(WpfColor.FromArgb(215, 255, 255, 255));
            var inputBorderBrush = _ctx.GetBrush(WpfColor.FromRgb(46, 125, 215));
            SketchyDrawer.DrawRectangle(_ctx.RenderTarget, titleBacking, inputBgBrush, inputBorderBrush, _ctx.InvStroke(1f), block.Key.ToString() + "_editbg");

            DrawEditSelection(titleText, titleSize, x + 14, titleBacking.Y + 4, titleW, wrap: false, sketchy: false, editCursorPos, editSelectionAnchor);
            _ctx.DrawText(titleText, x + 14, titleBacking.Y + 4, titleW, titleSize, text);
            if (editCursorVisible)
                DrawNoteCursor(titleText, titleSize, x + 14, titleBacking.Y + 4, titleW, editCursorPos, wrap: false);
        }
        else
        {
            _ctx.RenderTarget.FillRoundedRectangle(new RoundedRectangle(titleBacking, 4, 4), _ctx.GetBrush(WpfColor.FromArgb(210, 255, 255, 255)));
            _ctx.DrawText(titleText, x + 14, titleBacking.Y + 4, titleW, titleSize, text);
        }

        if (block.IsCollapsed)
        {
            _ctx.DrawText("double-click to expand", x + 14, y + headerH - 14, w - 58, 8.5f, WpfColor.FromArgb(180, text.R, text.G, text.B));
            // DrawCollapsedGroupSummary is complex and depends on Scene.Blocks, maybe passed in or handled via callback
        }
        else
        {
            if (block.IsSelected)
                DrawGenericResizeHandle(outer, stroke, block.IsLocked);
        }
    }

    private void DrawCardShell(Rect outer, bool selected, WpfColor stroke, float radius, string seedKey, WpfColor? fill = null, string fillStyle = "hatch", double opacity = 1.0, float hatchOpacity = 1f)
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
                var selStrokeBrush = _ctx.GetBrush(selectedStroke);
                float selSw = _ctx.InvStroke(2.0f);
                SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), radius, null, selStrokeBrush, selSw, seedKey, strokeStyle: _ctx.DashedStroke, fillStyle: fillStyle, hatchOpacity: hatchOpacity);
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

        SketchyDrawer.DrawRoundedRectangle(_ctx.RenderTarget, new RectangleF(x, y, w, h), radius, fillBrush, strokeBrush, sw, seedKey, fillStyle: fillStyle, hatchOpacity: hatchOpacity);
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
    }

    private void DrawPreviewBars(Rect codeRect, WpfColor accent, int count)
    {
        float startX = (float)codeRect.X + 14, startY = (float)codeRect.Y + 10;
        float avail = (float)(codeRect.Width - 28);
        float gap = 3f;
        float barH = Math.Max(5f, ((float)codeRect.Height - 20 - (count - 1) * gap) / count);
        for (int i = 0; i < count; i++)
        {
            float barY = startY + i * (barH + gap);
            byte alpha = i == 0 ? (byte)80 : (byte)35;
            _ctx.RenderTarget.FillRoundedRectangle(
                new RoundedRectangle(new RectangleF(startX, barY, avail, barH), 3, 3),
                _ctx.GetBrush(WpfColor.FromArgb(alpha, accent.R, accent.G, accent.B)));
        }
    }

    private void DrawMarkdownFileBody(RenderBlock block, Rect bodyRect, Dictionary<string, int> codeScrollLines)
    {
        _ctx.RenderTarget.PushAxisAlignedClip(CanvasDrawingUtils.ToRF(bodyRect), AntialiasMode.PerPrimitive);

        string[] allLines = (block.Body ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int visibleLines = Math.Max(1, (int)Math.Floor(bodyRect.Height / CodeLineH));
        int maxScroll = Math.Max(0, allLines.Length - visibleLines);
        codeScrollLines.TryGetValue(block.Key, out int scrollLines);
        scrollLines = Math.Clamp(scrollLines, 0, maxScroll);

        float x = (float)(bodyRect.X + 22);
        float y = (float)(bodyRect.Y + 16);
        float maxW = (float)(bodyRect.Width - 44 - CodeScrollbarReserveW);
        bool inCodeFence = false;

        for (int lineIndex = scrollLines; lineIndex < allLines.Length; lineIndex++)
        {
            if (y > bodyRect.Bottom - 18) break;
            string rawLine = allLines[lineIndex];
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                y += 8;
                continue;
            }

            if (trimmed.Length == 0)
            {
                y += 10;
                continue;
            }

            if (inCodeFence)
            {
                var codeBg = new RectangleF(x - 8, y - 3, maxW + 16, 20);
                _ctx.RenderTarget.FillRoundedRectangle(new RoundedRectangle(codeBg, 4, 4),
                    _ctx.GetBrush(WpfColor.FromRgb(248, 250, 252)));
                _ctx.DrawText(NormalizeCodeLine(line), x, y, maxW, 11.2f, WpfColor.FromRgb(51, 65, 85));
                y += 20;
                continue;
            }

            int headingLevel = trimmed.TakeWhile(c => c == '#').Count();
            if (headingLevel is > 0 and <= 4 && trimmed.Length > headingLevel && trimmed[headingLevel] == ' ')
            {
                float size = headingLevel == 1 ? 17f : headingLevel == 2 ? 14.5f : 12.8f;
                string heading = trimmed[(headingLevel + 1)..];
                float textH = EstimateWrappedHeight(heading, maxW, size, 2);
                _ctx.DrawWrappedText(heading, x, y, maxW, textH, size, WpfColor.FromRgb(15, 23, 42), wrap: true);
                y += textH + (headingLevel == 1 ? 10 : 7);
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                _ctx.RenderTarget.DrawLine(new Vector2(x, y + 6), new Vector2(x + maxW, y + 6),
                    _ctx.GetBrush(WpfColor.FromArgb(180, 203, 213, 225)), _ctx.InvStroke(1.0f));
                y += 16;
                continue;
            }

            if (IsMarkdownTableRow(trimmed))
            {
                if (!LooksLikeTableSeparator(trimmed))
                {
                    bool isHeader = lineIndex + 1 < allLines.Length &&
                        LooksLikeTableSeparator(allLines[lineIndex + 1].Trim());
                    DrawMarkdownTableRow(trimmed, x, y, maxW, isHeader);
                    y += 24;
                }
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                string bullet = StripMarkdownInline(trimmed[2..]);
                float textH = EstimateWrappedHeight(bullet, maxW - 18, 11.4f, 3);
                _ctx.DrawText("*", x, y, 16, 12, WpfColor.FromRgb(100, 116, 139));
                _ctx.DrawWrappedText(bullet, x + 18, y, maxW - 18, textH, 11.4f, WpfColor.FromRgb(51, 65, 85), wrap: true);
                y += textH + 7;
                continue;
            }

            string paragraph = StripMarkdownInline(trimmed);
            float paragraphH = EstimateWrappedHeight(paragraph, maxW, 11.4f, 4);
            _ctx.DrawWrappedText(paragraph, x, y, maxW, paragraphH, 11.4f, WpfColor.FromRgb(51, 65, 85), wrap: true);
            y += paragraphH + 7;
        }

        if (maxScroll > 0)
            DrawCodeScrollbar(bodyRect, scrollLines, visibleLines, allLines.Length);

        _ctx.RenderTarget.PopAxisAlignedClip();
    }

    private void DrawCodeBody(RenderBlock block, Rect bodyRect, Dictionary<string, int> codeScrollLines, bool isExtractMode)
    {
        _ctx.RenderTarget.PushAxisAlignedClip(CanvasDrawingUtils.ToRF(bodyRect), AntialiasMode.PerPrimitive);

        string body = block.Body ?? string.Empty;
        string[] allLines = body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int blockStartLine = block.StartLine ?? 1;

        int sliceStartIdx = 0;
        int sliceCount = allLines.Length;
        int firstShownSrcLine = blockStartLine;
        if (block.Focused is not null)
        {
            sliceStartIdx = Math.Max(0, block.Focused.StartLine - blockStartLine);
            int sliceEndIdx = Math.Min(allLines.Length - 1, block.Focused.EndLine - blockStartLine);
            sliceCount = Math.Max(0, sliceEndIdx - sliceStartIdx + 1);
            firstShownSrcLine = block.Focused.StartLine;
        }

        int topPaddingLines = block.Focused is not null ? FocusedCodeTopPaddingLines : 0;
        int visibleLines = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH) - topPaddingLines);
        int maxScroll = Math.Max(0, sliceCount - visibleLines);
        codeScrollLines.TryGetValue(block.Key, out int scrollLines);
        scrollLines = Math.Clamp(scrollLines, 0, maxScroll);

        int linesToShow = Math.Min(sliceCount - scrollLines, visibleLines);

        for (int i = 0; i < linesToShow; i++)
        {
            int srcIdx = sliceStartIdx + scrollLines + i;
            if (srcIdx >= allLines.Length) break;
            string lineText = NormalizeCodeLine(allLines[srcIdx]);
            float lineY = (float)bodyRect.Y + (i + topPaddingLines) * (float)CodeLineH;
            int srcLine = firstShownSrcLine + scrollLines + i;

            _ctx.DrawText(srcLine.ToString(), (float)bodyRect.X, lineY, (float)CodeGutterW - 6, 11,
                WpfColor.FromRgb(85, 98, 108));

            float codeX = (float)(bodyRect.X + CodeGutterW + CodeTextPadX);
            DrawScopeGuides(lineText, codeX, lineY, bodyRect);
            DrawEditorLine(block, srcLine, lineText, codeX, lineY, bodyRect, isExtractMode);
        }

        if (maxScroll > 0)
            DrawCodeScrollbar(bodyRect, scrollLines, visibleLines, sliceCount);

        _ctx.RenderTarget.PopAxisAlignedClip();
    }

    private void DrawEditorLine(RenderBlock block, int srcLine, string lineText, float startX, float lineY, Rect bodyRect, bool isExtractMode)
    {
        if (string.IsNullOrEmpty(lineText)) return;
        const float fontSize = 11.5f;
        float maxWidth = (float)(bodyRect.Right - startX - CodeScrollbarReserveW);
        if (maxWidth < 4) return;

        IDWriteTextFormat fmt = _ctx.GetTextFormat(fontSize);
        using var layout = _ctx.DWriteFactory.CreateTextLayout(lineText, fmt, maxWidth, fontSize * 2.2f);

        if (isExtractMode && block.IsSelected && block.SemanticTokens is not null)
            DrawAltSymbolHighlights(block, srcLine, lineText, startX, lineY, maxWidth, layout);

        if (block.SemanticTokens is not null)
        {
            foreach (var token in block.SemanticTokens.Where(t => t.Line == srcLine).OrderBy(t => t.Column))
            {
                int start = Math.Max(0, token.Column - 1);
                if (start >= lineText.Length) continue;
                int length = Math.Min(token.Length, lineText.Length - start);
                if (length <= 0) continue;
                layout.SetDrawingEffect(_ctx.GetBrush(CanvasDrawingUtils.TokenColor(token.Kind)), new TextRange((uint)start, (uint)length));
            }
        }
        _ctx.RenderTarget.DrawTextLayout(new Vector2(startX, lineY), layout, _ctx.GetBrush(WpfColor.FromRgb(45, 55, 72)), DrawTextOptions.Clip);
    }

    private void DrawAltSymbolHighlights(RenderBlock block, int srcLine, string lineText, float startX, float lineY, float maxWidth, IDWriteTextLayout layout)
    {
        if (block.SemanticTokens is null) return;
        var fill = _ctx.GetBrush(WpfColor.FromArgb(72, 235, 154, 40));
        var stroke = _ctx.GetBrush(WpfColor.FromArgb(150, 235, 154, 40));
        foreach (var token in block.SemanticTokens.Where(t => t.IsSymbolCandidate && t.Line == srcLine).OrderBy(t => t.Column))
        {
            int start = Math.Max(0, token.Column - 1);
            if (start >= lineText.Length) continue;
            int length = Math.Min(token.Length, lineText.Length - start);
            if (length <= 0) continue;
            var metrics = layout.HitTestTextRange((uint)start, (uint)length, 0f, 0f);
            foreach (var m in metrics)
            {
                float padX = 2f, padY = 1f;
                float x = startX + m.Left - padX;
                float y = lineY + m.Top + padY;
                float width = Math.Min(maxWidth - m.Left + padX, m.Width + padX * 2);
                float height = Math.Max(10f, m.Height - padY * 2);
                if (width <= 0) continue;
                var rect = new RoundedRectangle(new RectangleF(x, y, width, height), 3f, 3f);
                _ctx.RenderTarget.FillRoundedRectangle(rect, fill);
                _ctx.RenderTarget.DrawRoundedRectangle(rect, stroke, _ctx.InvStroke(0.7f));
            }
        }
    }

    private void DrawScopeGuides(string lineText, float codeX, float lineY, Rect bodyRect)
    {
        int leadingSpaces = lineText.TakeWhile(c => c == ' ').Count();
        int guideCount = leadingSpaces / 4;
        if (guideCount <= 0) return;

        var guideBrush = _ctx.GetBrush(WpfColor.FromArgb(70, 203, 213, 225));
        float top = lineY;
        float bottom = lineY + (float)CodeLineH;
        for (int i = 1; i <= guideCount; i++)
        {
            float x = codeX + i * 4 * (float)CodeCharW - 7;
            if (x <= bodyRect.Left || x >= bodyRect.Right - CodeScrollbarReserveW) continue;
            _ctx.RenderTarget.DrawLine(new Vector2(x, top), new Vector2(x, bottom), guideBrush, 1f);
        }
    }

    private void DrawCodeScrollbar(Rect bodyRect, int scrollLines, int visibleLines, int totalLines)
    {
        if (totalLines <= 0) return;
        const float trackRightInset = 12f;
        float trackW = 4f;
        float trackX = (float)bodyRect.Right - trackW - trackRightInset;
        float trackY = (float)bodyRect.Y + 8;
        float trackH = (float)bodyRect.Height - 16;
        if (trackH <= 12) return;

        float thumbH = Math.Max(18f, trackH * Math.Min(1f, visibleLines / (float)totalLines));
        float maxThumbY = trackH - thumbH;
        float thumbY = trackY + (totalLines == visibleLines ? 0 : maxThumbY * (scrollLines / (float)Math.Max(1, totalLines - visibleLines)));

        _ctx.RenderTarget.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(trackX, trackY, trackW, trackH), 2, 2),
            _ctx.GetBrush(WpfColor.FromArgb(45, 181, 190, 203)));
        _ctx.RenderTarget.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(trackX, thumbY, trackW, thumbH), 2, 2),
            _ctx.GetBrush(WpfColor.FromArgb(145, 120, 132, 150)));
    }

    private void DrawRestoreButton(Rect blockBounds, WpfColor accent)
    {
        Rect btn = CanvasDrawingUtils.GetRestoreButtonBounds(blockBounds);
        var rr = new RoundedRectangle(CanvasDrawingUtils.ToRF(btn), 6, 6);
        _ctx.RenderTarget.FillRoundedRectangle(rr, _ctx.GetBrush(WpfColor.FromArgb(60, accent.R, accent.G, accent.B)));
        _ctx.RenderTarget.DrawRoundedRectangle(rr, _ctx.GetBrush(WpfColor.FromArgb(180, accent.R, accent.G, accent.B)), _ctx.InvStroke(1.0f));
        _ctx.DrawText("R", (float)btn.X + 7, (float)btn.Y + 3, (float)btn.Width - 4, 12, WpfColor.FromRgb(46, 125, 215));
    }

    private void DrawConnectionAnchors(
        RenderBlock block,
        Rect bounds,
        WpfColor accent,
        string? hoverAnchorBlockKey,
        int? hoverAnchorIndex,
        bool isDrawingConnection,
        string? connectionSourceKey,
        int? connectionSourceAnchorIndex,
        string? connectionHoverTargetKey,
        int? connectionHoverTargetAnchorIndex)
    {
        var fill = _ctx.GetBrush(WpfColor.FromArgb(230, 255, 255, 255));
        var stroke = _ctx.GetBrush(WpfColor.FromArgb(210, accent.R, accent.G, accent.B));
        var hoverStroke = _ctx.GetBrush(WpfColor.FromArgb(235, 35, 162, 109));
        var sourceStroke = _ctx.GetBrush(WpfColor.FromArgb(235, 32, 104, 192));
        float r = Math.Max(3.0f, _ctx.InvStroke(4.0f));
        for (int i = 0; i < 16; i++)
        {
            System.Windows.Point p = CanvasDrawingUtils.GetBlockOutlinePoint(block, bounds, CanvasDrawingUtils.GetConnectionAnchorPoint(bounds, i));
            bool isSource = isDrawingConnection && block.Key == connectionSourceKey && i == connectionSourceAnchorIndex;
            bool isTarget = isDrawingConnection && block.Key == connectionHoverTargetKey && i == connectionHoverTargetAnchorIndex;
            bool isHover = block.Key == hoverAnchorBlockKey && i == hoverAnchorIndex;
            float rr = isTarget || isSource || isHover ? r * 1.45f : r;
            var ellipse = new Ellipse(new Vector2((float)p.X, (float)p.Y), r, r);
            _ctx.RenderTarget.FillEllipse(ellipse, fill);
            _ctx.RenderTarget.DrawEllipse(new Ellipse(new Vector2((float)p.X, (float)p.Y), rr, rr),
                isTarget || isHover ? hoverStroke : isSource ? sourceStroke : stroke,
                _ctx.InvStroke(isTarget || isSource || isHover ? 1.2f : 0.85f));
        }
    }

    private bool ShouldDrawConnectionAnchors(RenderBlock block, bool connectorsEnabled) =>
        connectorsEnabled
        && _ctx.Zoom > UltraCompactZoom
        && block.Kind is not (BlockKind.Note or BlockKind.Shape);

    private void DrawNoteCursor(string text, float fontSize, float textX, float textY, float maxW, int cursorPos, bool wrap = false, bool sketchy = false, DWriteTextAlignment alignment = DWriteTextAlignment.Leading, float maxH = 9999f, DWriteParagraphAlignment paragraphAlignment = DWriteParagraphAlignment.Near, string? fontFamily = null, bool bold = false, bool italic = false)
    {
        if (maxW < 1f || maxH < 1f) return;

        IDWriteTextFormat fmt = fontFamily is null
            ? _ctx.GetTextFormat(fontSize, sketchy)
            : _ctx.GetRichFormat(fontFamily, fontSize, bold, italic);
        var oldTextAlign = fmt.TextAlignment;
        var oldParagraph = fmt.ParagraphAlignment;
        fmt.TextAlignment = alignment;
        // Keep paragraph alignment at Near for the hit-test pass so cy is in raw
        // (top-of-text) coordinates regardless of how the visible text is aligned.
        fmt.ParagraphAlignment = DWriteParagraphAlignment.Near;

        string layoutText = text.Length == 0 ? " " : text;
        int safePos = Math.Clamp(cursorPos, 0, text.Length);
        using var layout = _ctx.DWriteFactory.CreateTextLayout(layoutText, fmt, maxW, maxH);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        layout.HitTestTextPosition((uint)safePos, false, out float cx, out float cy, out _);

        // Apply the paragraph-alignment offset manually so the caret lines up with
        // the rendered (potentially centered or bottom-aligned) text.
        float vOffset = 0f;
        if (paragraphAlignment != DWriteParagraphAlignment.Near)
        {
            var metrics = layout.Metrics;
            float gap = Math.Max(0, maxH - metrics.Height);
            vOffset = paragraphAlignment == DWriteParagraphAlignment.Center ? gap * 0.5f : gap;
        }

        fmt.TextAlignment = oldTextAlign;
        fmt.ParagraphAlignment = oldParagraph;

        float lineH = fontSize * 1.35f;
        _ctx.RenderTarget.DrawLine(
            new Vector2(textX + cx, textY + cy + vOffset),
            new Vector2(textX + cx, textY + cy + vOffset + lineH),
            _ctx.GetBrush(WpfColor.FromArgb(210, 38, 33, 8)), _ctx.InvStroke(1.5f));
    }

    private void DrawEditSelection(string text, float fontSize, float textX, float textY, float maxW, bool wrap = false, bool sketchy = false, int cursorPos = 0, int selectionAnchor = -1, DWriteTextAlignment alignment = DWriteTextAlignment.Leading, float maxH = 9999f, DWriteParagraphAlignment paragraphAlignment = DWriteParagraphAlignment.Near, string? fontFamily = null, bool bold = false, bool italic = false)
    {
        if (maxW < 1f || maxH < 1f) return;
        if (selectionAnchor < 0 || selectionAnchor == cursorPos) return;
        int start = Math.Min(selectionAnchor, cursorPos);
        int end = Math.Max(selectionAnchor, cursorPos);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (end <= start) return;
        IDWriteTextFormat fmt = fontFamily is null
            ? _ctx.GetTextFormat(fontSize, sketchy)
            : _ctx.GetRichFormat(fontFamily, fontSize, bold, italic);
        var oldTextAlign = fmt.TextAlignment;
        var oldParagraph = fmt.ParagraphAlignment;
        fmt.TextAlignment = alignment;
        // Force top alignment for the measurement layout so the returned rects are
        // in raw text coordinates; we apply the vertical offset ourselves below.
        fmt.ParagraphAlignment = DWriteParagraphAlignment.Near;

        string layoutText = text.Length == 0 ? " " : text;
        using var layout = _ctx.DWriteFactory.CreateTextLayout(layoutText, fmt, maxW, maxH);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        var metrics = layout.HitTestTextRange((uint)start, (uint)(end - start), 0f, 0f);

        float vOffset = 0f;
        if (paragraphAlignment != DWriteParagraphAlignment.Near)
        {
            var lm = layout.Metrics;
            float gap = Math.Max(0, maxH - lm.Height);
            vOffset = paragraphAlignment == DWriteParagraphAlignment.Center ? gap * 0.5f : gap;
        }

        fmt.TextAlignment = oldTextAlign;
        fmt.ParagraphAlignment = oldParagraph;

        var brush = _ctx.GetBrush(WpfColor.FromArgb(110, 70, 130, 220));
        foreach (var m in metrics)
            _ctx.RenderTarget.FillRectangle(new RectangleF(textX + m.Left, textY + m.Top + vOffset, m.Width, m.Height), brush);
    }

    private static bool IsColorGroup(RenderBlock block) =>
        block.Kind == BlockKind.Container && string.Equals(block.ShapeType, "color-group", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinearShapeTool(string? shapeType) =>
        shapeType is "line" or "arrow" or "polyline";

    private static string NormalizeCodeLine(string text) => text.Replace("\t", "    ", StringComparison.Ordinal);

    private static bool IsMarkdownFileBlock(RenderBlock block) =>
        string.Equals(block.ShapeType, "markdown", StringComparison.OrdinalIgnoreCase)
        || string.Equals(System.IO.Path.GetExtension(block.FilePath ?? string.Empty), ".md", StringComparison.OrdinalIgnoreCase)
        || string.Equals(System.IO.Path.GetExtension(block.FilePath ?? string.Empty), ".markdown", StringComparison.OrdinalIgnoreCase);

    private void DrawMarkdownTableRow(string line, float x, float y, float width, bool header)
    {
        var cells = line.Trim().Trim('|').Split('|').Select(c => StripMarkdownInline(c.Trim())).ToArray();
        if (cells.Length == 0) return;

        float cellW = Math.Max(42, width / cells.Length);
        var bg = header
            ? WpfColor.FromRgb(241, 245, 249)
            : WpfColor.FromArgb(90, 248, 250, 252);
        var border = WpfColor.FromArgb(170, 203, 213, 225);
        _ctx.RenderTarget.FillRectangle(new RectangleF(x, y - 3, width, 22), _ctx.GetBrush(bg));
        _ctx.RenderTarget.DrawRectangle(new RectangleF(x, y - 3, width, 22), _ctx.GetBrush(border), _ctx.InvStroke(0.8f));

        for (int i = 1; i < cells.Length; i++)
        {
            float cx = x + i * cellW;
            _ctx.RenderTarget.DrawLine(new Vector2(cx, y - 3), new Vector2(cx, y + 19), _ctx.GetBrush(border), _ctx.InvStroke(0.7f));
        }

        for (int i = 0; i < cells.Length; i++)
        {
            float cellX = x + i * cellW + 6;
            _ctx.DrawText(cells[i], cellX, y + 1, Math.Max(12, cellW - 12), header ? 10.8f : 10.4f,
                header ? WpfColor.FromRgb(30, 41, 59) : WpfColor.FromRgb(51, 65, 85));
        }
    }

    private static bool IsMarkdownTableRow(string line) =>
        line.Count(c => c == '|') >= 2;

    private static bool LooksLikeTableSeparator(string line)
    {
        if (!IsMarkdownTableRow(line)) return false;
        string text = line.Trim().Trim('|').Replace(" ", string.Empty, StringComparison.Ordinal);
        return text.Length > 0 && text.All(c => c is '-' or ':' or '|');
    }

    private static string StripMarkdownInline(string text) =>
        text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

    private static float EstimateWrappedHeight(string text, float maxWidth, float fontSize, int maxLines)
    {
        float avgCharW = Math.Max(4.5f, fontSize * 0.58f);
        int charsPerLine = Math.Max(12, (int)Math.Floor(maxWidth / avgCharW));
        int lines = Math.Clamp((int)Math.Ceiling(Math.Max(1, text.Length) / (double)charsPerLine), 1, Math.Max(1, maxLines));
        return lines * fontSize * 1.45f;
    }

    private static IReadOnlyList<Vector2> BuildRegularPolygonPoints(Rect bounds, int sides, float rotation)
    {
        var points = new Vector2[Math.Max(3, sides)];
        float cx = (float)(bounds.X + bounds.Width / 2);
        float cy = (float)(bounds.Y + bounds.Height / 2);
        float rx = (float)bounds.Width / 2;
        float ry = (float)bounds.Height / 2;
        for (int i = 0; i < points.Length; i++)
        {
            float angle = rotation + MathF.PI * 2 * i / points.Length;
            points[i] = new Vector2(cx + MathF.Cos(angle) * rx, cy + MathF.Sin(angle) * ry);
        }
        return points;
    }

    private static IReadOnlyList<Vector2> BuildStarPoints(Rect bounds)
    {
        const int pointCount = 10;
        var points = new Vector2[pointCount];
        float cx = (float)(bounds.X + bounds.Width / 2);
        float cy = (float)(bounds.Y + bounds.Height / 2);
        float outerRx = (float)bounds.Width / 2;
        float outerRy = (float)bounds.Height / 2;
        float innerRx = outerRx * 0.46f;
        float innerRy = outerRy * 0.46f;
        for (int i = 0; i < pointCount; i++)
        {
            bool outer = i % 2 == 0;
            float angle = -MathF.PI / 2 + MathF.PI * 2 * i / pointCount;
            float rx = outer ? outerRx : innerRx;
            float ry = outer ? outerRy : innerRy;
            points[i] = new Vector2(cx + MathF.Cos(angle) * rx, cy + MathF.Sin(angle) * ry);
        }
        return points;
    }

    private static Rect FitRect(Rect area, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return area;
        double scale = Math.Min(area.Width / pixelWidth, area.Height / pixelHeight);
        double width = pixelWidth * scale;
        double height = pixelHeight * scale;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }
}
