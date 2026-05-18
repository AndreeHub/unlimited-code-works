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
    private void RenderNative()
    {
        if (!EnsureRT() || _rt is null || _dwrite is null) return;

        Size viewSize = new(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        EnsureVisible(viewSize);

        _rt.BeginDraw();
        _rt.Transform = Matrix3x2.Identity;
        _rt.Clear(ToColor4(WpfColor.FromRgb(251, 252, 253)));
        DrawBackground(viewSize);

        if (_snapshot.Blocks.Count == 0 && _snapshot.SwimLanes.Count == 0)
        {
            DrawEmptyHint(viewSize);
        }
        else
        {
            var tx = Matrix3x2.CreateScale((float)_camera.Zoom)
                   * Matrix3x2.CreateTranslation((float)_camera.OffsetX, (float)_camera.OffsetY);
            _rt.Transform = tx;

            // Draw swim-lanes first (behind everything)
            foreach (var lane in _snapshot.SwimLanes)
                DrawSwimLane(lane);

            // Draw connections
        foreach (var conn in _visibleConnections)
        {
            if (conn.Connection.Label == "__note") continue;
            DrawConnection(conn);
        }

            // Draw in-progress connection
            if (_isDrawingConnection)
                DrawInProgressConnection();

            // Draw blocks
            foreach (var block in _visibleBlocks)
                DrawBlock(block);

            _rt.Transform = Matrix3x2.Identity;
            DrawMarquee();
        }

        try { _rt.EndDraw(); }
        catch { DisposeRenderTarget(); EnsureRT(); }
    }

    private void DrawBackground(Size viewSize)
    {
        if (BackgroundMode == CanvasBackgroundMode.Dots)
            DrawDots(viewSize);
        else
            DrawGrid(viewSize);
    }

    private void DrawGrid(Size viewSize)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        var minor = GetBrush(WpfColor.FromArgb(42, 210, 218, 228));
        var major = GetBrush(WpfColor.FromArgb(72, 190, 202, 216));
        // Camera-aware grid: lines stay locked to world units so panning feels physical
        float spacing = 36f;
        float majorEvery = 5f;
        double zoom = _camera.Zoom;
        float gridStep = (float)(spacing * zoom);
        if (gridStep < 12f) gridStep = (float)(spacing * 4 * zoom);
        if (gridStep < 8f) return;

        float ox = (float)(_camera.OffsetX % gridStep);
        float oy = (float)(_camera.OffsetY % gridStep);
        int i = 0;
        for (float x = ox; x < w; x += gridStep, i++)
            _rt!.DrawLine(new Vector2(x, 0), new Vector2(x, h), i % (int)majorEvery == 0 ? major : minor, 1f);
        i = 0;
        for (float y = oy; y < h; y += gridStep, i++)
            _rt!.DrawLine(new Vector2(0, y), new Vector2(w, y), i % (int)majorEvery == 0 ? major : minor, 1f);
    }

    private void DrawDots(Size viewSize)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        const double worldSpacing = 28;
        float spacing = (float)(worldSpacing * _camera.Zoom);
        while (spacing < 14f) spacing *= 2f;
        while (spacing > 34f) spacing *= 0.5f;

        float ox = (float)(_camera.OffsetX % spacing);
        float oy = (float)(_camera.OffsetY % spacing);
        byte alpha = (byte)Math.Clamp(92 - Math.Abs(spacing - 22f) * 2.2f, 46, 92);
        var dot = GetBrush(WpfColor.FromArgb(alpha, 198, 207, 219));
        float r = Math.Clamp(spacing / 18f, 0.9f, 1.55f);
        for (float y = oy; y < h; y += spacing)
            for (float x = ox; x < w; x += spacing)
                _rt!.FillEllipse(new Ellipse(new Vector2(x, y), r, r), dot);
    }

    private void DrawEmptyHint(Size viewSize)
    {
        float cw = 480, ch = 180;
        float x = (float)((viewSize.Width - cw) / 2), y = (float)((viewSize.Height - ch) / 2);
        var rr = new RoundedRectangle(new RectangleF(x, y, cw, ch), 8, 8);
        _rt!.FillRoundedRectangle(rr, GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _rt.DrawRoundedRectangle(rr, GetBrush(WpfColor.FromArgb(220, 226, 232, 240)), 1f);
        DrawText("ReviewScope", x + 24, y + 22, cw - 48, 16, WpfColor.FromRgb(31, 41, 51));
        DrawText("Open a project, then place files and extracted methods on the canvas.", x + 24, y + 52, cw - 48, 13, WpfColor.FromRgb(83, 96, 112));
        DrawText("Use the explorer, command bar, and note tools to map review context.", x + 24, y + 90, cw - 48, 11, WpfColor.FromRgb(119, 132, 150));
    }

    // -----------------------------------------------------------------------
    // Swim-lane drawing
    // -----------------------------------------------------------------------
    private void DrawSwimLane(SceneSwimLaneVisual laneVis)
    {
        var lane = laneVis.Lane;
        WpfColor color = ParseColor(lane.Color);
        bool selected = lane.IsSelected;

        float x = (float)laneVis.Bounds.X, y = (float)laneVis.Bounds.Y;
        float w = (float)laneVis.Bounds.Width, h = (float)laneVis.Bounds.Height;

        // Fill: translucent color
        _rt!.FillRectangle(new RectangleF(x, y, w, h),
            GetBrush(WpfColor.FromArgb(22, color.R, color.G, color.B)));

        // Dashed border
        var borderBrush = GetBrush(selected
            ? WpfColor.FromArgb(200, color.R, color.G, color.B)
            : WpfColor.FromArgb(100, color.R, color.G, color.B));
        float stroke = InvStroke(selected ? 2.0f : 1.25f);

        _rt.DrawRectangle(new RectangleF(x, y, w, h), borderBrush, stroke);

        // Label bar at top
        float labelH = 32;
        _rt.FillRectangle(new RectangleF(x, y, w, labelH),
            GetBrush(WpfColor.FromArgb(55, color.R, color.G, color.B)));
        DrawText(lane.Name, x + 12, y + 8, w - 24, 13, WpfColor.FromArgb(220, color.R, color.G, color.B));

        // Resize handle indicator
        float hs = 12;
        _rt.FillRectangle(new RectangleF(x + w - hs, y + h - hs, hs, hs),
            GetBrush(WpfColor.FromArgb(80, color.R, color.G, color.B)));
    }

    // -----------------------------------------------------------------------
    // Connection drawing
    // -----------------------------------------------------------------------
    private void DrawConnection(SceneConnectionVisual connVis)
    {
        var conn = connVis.Connection;
        if (conn.IsDimmed) return;

        WpfColor lineColor = WpfColor.FromArgb(180, 69, 132, 203);
        float stroke = InvStroke(1.6f);

        ID2D1PathGeometry geom = GetOrBuildConnectionGeometry(connVis);
        var brush = GetBrush(lineColor);
        _rt!.DrawGeometry(geom, brush, stroke);
        DrawArrowhead(connVis.End, connVis.Start, brush, stroke);

        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            Point mid = new((connVis.Start.X + connVis.End.X) / 2, (connVis.Start.Y + connVis.End.Y) / 2);
            DrawText(conn.Label!, (float)mid.X - 60, (float)mid.Y - 10, 120, 10, WpfColor.FromRgb(83, 96, 112));
        }
    }

    private void DrawInProgressConnection()
    {
        _rt!.DrawLine(
            new Vector2((float)_connectionSourceWorld.X, (float)_connectionSourceWorld.Y),
            new Vector2((float)_connectionCurrentWorld.X, (float)_connectionCurrentWorld.Y),
            GetBrush(WpfColor.FromArgb(180, 69, 132, 203)), InvStroke(1.5f));
    }

    private void DrawArrowhead(Point tip, Point from, ID2D1SolidColorBrush brush, float stroke)
    {
        Vector2 dir = new Vector2((float)(tip.X - from.X), (float)(tip.Y - from.Y));
        float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.01f) return;
        dir /= len;
        Vector2 perp = new(-dir.Y, dir.X);
        float aLen = Math.Max(8f, stroke * 5);
        Vector2 tipV = new((float)tip.X, (float)tip.Y);
        Vector2 left = tipV - dir * aLen + perp * (aLen * 0.45f);
        Vector2 right = tipV - dir * aLen - perp * (aLen * 0.45f);

        using var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(tipV, FigureBegin.Filled);
        sink.AddLine(left);
        sink.AddLine(right);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        _rt!.FillGeometry(path, brush);
    }

    private ID2D1PathGeometry GetOrBuildConnectionGeometry(SceneConnectionVisual connVis)
    {
        if (_connectionGeoms.TryGetValue(connVis.Connection.Id, out var cached)) return cached;

        float x1 = (float)connVis.Start.X, y1 = (float)connVis.Start.Y;
        float x2 = (float)connVis.End.X, y2 = (float)connVis.End.Y;
        float dx = Math.Abs(x2 - x1) * 0.5f;

        var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(new Vector2(x1, y1), FigureBegin.Hollow);
        sink.AddBezier(new D2DBezierSegment(
            new Vector2(x1 + dx, y1),
            new Vector2(x2 - dx, y2),
            new Vector2(x2, y2)));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        _connectionGeoms[connVis.Connection.Id] = path;
        return path;
    }

    // -----------------------------------------------------------------------
    // Block drawing
    // -----------------------------------------------------------------------
    private void DrawBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        if (_camera.Zoom <= UltraCompactZoom) { DrawMicroBlock(blockVis); return; }

        switch (block.Kind)
        {
            case BlockKind.File:
            case BlockKind.Extract:
                DrawCodeBlock(blockVis);
                break;
            case BlockKind.Note:
                DrawNoteBlock(blockVis);
                break;
        }
    }

    private void DrawMicroBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        WpfColor fill = block.Kind switch
        {
            BlockKind.File => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Extract => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Note => WpfColor.FromRgb(255, 245, 201),
            _ => WpfColor.FromRgb(255, 255, 255)
        };
        WpfColor border = block.IsSelected
            ? WpfColor.FromArgb(230, 46, 125, 215)
            : block.Kind == BlockKind.Extract
                ? WpfColor.FromArgb(150, 35, 162, 109)
                : WpfColor.FromArgb(130, 46, 125, 215);

        var rr = new RoundedRectangle(ToRF(blockVis.Bounds), 8, 8);
        _rt!.FillRoundedRectangle(rr, GetBrush(fill));
        _rt.DrawRoundedRectangle(rr, GetBrush(border), InvStroke(1.0f));
    }

    private void DrawCodeBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        bool compact = _camera.Zoom <= CompactZoom;
        bool preview = !compact && _camera.Zoom <= PreviewZoom;
        bool isFocused = block.Focused is not null;

        WpfColor accent = isFocused
            ? WpfColor.FromRgb(46, 125, 215)
            : block.Kind == BlockKind.Extract
                ? WpfColor.FromRgb(35, 162, 109)
                : WpfColor.FromRgb(46, 125, 215);

        WpfColor border = block.IsSelected
            ? WpfColor.FromArgb(235, 46, 125, 215)
            : WpfColor.FromArgb(170, accent.R, accent.G, accent.B);

        Rect outer = blockVis.Bounds;
        Rect header = new(outer.X, outer.Y, outer.Width, HeaderH);
        Rect footer = new(outer.X, outer.Bottom - FooterH, outer.Width, FooterH);
        Rect code = new(outer.X + 1, header.Bottom, outer.Width - 2, outer.Height - HeaderH - FooterH - 1);

        // Drop shadow (subtle)
        var shadow = new RoundedRectangle(new RectangleF((float)outer.X + 2, (float)outer.Y + 5, (float)outer.Width, (float)outer.Height), 8, 8);
        _rt!.FillRoundedRectangle(shadow, GetBrush(WpfColor.FromArgb(18, 35, 49, 66)));

        // Shell
        var shell = new RoundedRectangle(ToRF(outer), 8, 8);
        _rt.FillRoundedRectangle(shell, GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _rt.DrawRoundedRectangle(shell, GetBrush(border),
            block.IsSelected ? InvStroke(2.0f) : InvStroke(1.1f));

        // Header tint (slightly stronger for focused)
        byte headerAlpha = isFocused ? (byte)24 : (byte)14;
        _rt.FillRectangle(ToRF(header), GetBrush(WpfColor.FromArgb(headerAlpha, accent.R, accent.G, accent.B)));
        // Header underline
        _rt.DrawLine(new Vector2((float)outer.X, (float)header.Bottom), new Vector2((float)outer.Right, (float)header.Bottom),
            GetBrush(WpfColor.FromArgb(60, accent.R, accent.G, accent.B)), InvStroke(0.8f));

        _rt.FillRectangle(ToRF(code), GetBrush(WpfColor.FromRgb(255, 255, 255)));

        // Footer
        _rt.FillRectangle(ToRF(footer), GetBrush(WpfColor.FromRgb(250, 252, 254)));

        // Header text
        string icon = isFocused ? "â—‰ " : block.Kind == BlockKind.Extract ? "âš™ " : "";
        string titleText = isFocused
            ? icon + block.Focused!.SymbolName
            : icon + block.Title;
        DrawText(titleText, (float)outer.X + 14, (float)outer.Y + 12, (float)outer.Width - 60, 14, WpfColor.FromRgb(31, 41, 51));

        string subtitle = isFocused
            ? $"in {block.Title}  â€¢  lines {block.Focused!.StartLine}â€“{block.Focused.EndLine}"
            : block.Subtitle;
        DrawText(subtitle, (float)outer.X + 14, (float)outer.Y + 36, (float)outer.Width - 60, 10, WpfColor.FromRgb(117, 128, 143));

        if (block.IsCollapsed)
        {
            DrawText("[ collapsed â€” double-click to expand ]",
                (float)code.X + 16, (float)code.Y + 12, (float)code.Width - 32, 11, WpfColor.FromRgb(117, 128, 143));
        }
        else if (!string.IsNullOrWhiteSpace(block.Body))
        {
            if (compact)
                DrawPreviewBars(code, accent, 4);
            else if (preview)
                DrawPreviewBars(code, accent, 9);
            else
                DrawCodeBody(block, code);
        }

        // Footer text
        string footerFileName = block.FilePath is not null ? IOPath.GetFileName(block.FilePath) : string.Empty;
        string footerText = (block.Kind == BlockKind.Extract && block.StartLine.HasValue)
            ? $"lines {block.StartLine}â€“{block.EndLine}  â€¢  {footerFileName}"
            : isFocused
                ? $"focused â€” Ctrl+click another line to refocus  â€¢  {footerFileName}"
                : footerFileName;
        DrawText(footerText, (float)outer.X + 14, (float)footer.Y + 8, (float)outer.Width - 60, 9.5f, WpfColor.FromRgb(117, 128, 143));

        // Resize handle
        float rh = 12;
        _rt.FillRectangle(new RectangleF((float)outer.Right - rh, (float)outer.Bottom - rh, rh, rh),
            GetBrush(WpfColor.FromArgb(60, accent.R, accent.G, accent.B)));

        // Restore button for focused blocks
        if (isFocused) DrawRestoreButton(outer, accent);

        // Extract/Focus-mode highlight overlay
        if ((_isFocusMode || _isExtractMode) && _extractHoverBlock?.Block.Key == block.Key)
            DrawExtractHighlight(code);
    }

    private void DrawRestoreButton(Rect blockBounds, WpfColor accent)
    {
        Rect btn = GetRestoreButtonBounds(blockBounds);
        var rr = new RoundedRectangle(ToRF(btn), 6, 6);
        _rt!.FillRoundedRectangle(rr, GetBrush(WpfColor.FromArgb(60, accent.R, accent.G, accent.B)));
        _rt.DrawRoundedRectangle(rr, GetBrush(WpfColor.FromArgb(180, accent.R, accent.G, accent.B)), InvStroke(1.0f));
        DrawText("R", (float)btn.X + 7, (float)btn.Y + 3, (float)btn.Width - 4, 12, WpfColor.FromRgb(46, 125, 215));
    }

    private void DrawNoteBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        WpfColor fill = WpfColor.FromRgb(250, 237, 180);
        WpfColor border = block.IsSelected
            ? WpfColor.FromArgb(220, 255, 200, 40)
            : WpfColor.FromArgb(180, 210, 190, 80);

        var rr = new RoundedRectangle(ToRF(blockVis.Bounds), 8, 8);
        _rt!.FillRoundedRectangle(rr, GetBrush(fill));
        _rt.DrawRoundedRectangle(rr, GetBrush(border), InvStroke(1.2f));

        DrawText(block.Title, (float)blockVis.Bounds.X + 12, (float)blockVis.Bounds.Y + 10,
            (float)blockVis.Bounds.Width - 24, 12, WpfColor.FromRgb(40, 35, 10));

        if (!string.IsNullOrWhiteSpace(block.Body))
        {
            DrawWrappedText(block.Body, (float)blockVis.Bounds.X + 12, (float)blockVis.Bounds.Y + 32,
                (float)blockVis.Bounds.Width - 24, (float)blockVis.Bounds.Height - 44, 11,
                WpfColor.FromRgb(60, 52, 20));
        }
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
            _rt!.FillRoundedRectangle(
                new RoundedRectangle(new RectangleF(startX, barY, avail, barH), 3, 3),
                GetBrush(WpfColor.FromArgb(alpha, accent.R, accent.G, accent.B)));
        }
    }

    private void DrawCodeBody(RenderBlock block, Rect bodyRect)
    {
        if (_rt is null) return;
        _rt.PushAxisAlignedClip(ToRF(bodyRect), AntialiasMode.PerPrimitive);

        string body = block.Body ?? string.Empty;
        string[] allLines = body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int blockStartLine = block.StartLine ?? 1;

        // Slice lines for focused mode
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

        int visibleLines = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH));
        int maxScroll = Math.Max(0, sliceCount - visibleLines);
        _codeScrollLines.TryGetValue(block.Key, out int scrollLines);
        scrollLines = Math.Clamp(scrollLines, 0, maxScroll);
        if (scrollLines == 0)
            _codeScrollLines.Remove(block.Key);
        else
            _codeScrollLines[block.Key] = scrollLines;

        int linesToShow = Math.Min(sliceCount - scrollLines, visibleLines);

        for (int i = 0; i < linesToShow; i++)
        {
            int srcIdx = sliceStartIdx + scrollLines + i;
            if (srcIdx >= allLines.Length) break;
            string lineText = NormalizeCodeLine(allLines[srcIdx]);
            float lineY = (float)bodyRect.Y + i * (float)CodeLineH;
            int srcLine = firstShownSrcLine + scrollLines + i;

            // Line number
            DrawText(srcLine.ToString(), (float)bodyRect.X, lineY, (float)CodeGutterW - 6, 11,
                WpfColor.FromRgb(85, 98, 108), rightAlign: true);

            // Code text with syntax highlighting
            float codeX = (float)bodyRect.X + (float)CodeGutterW;
            DrawScopeGuides(lineText, codeX, lineY, bodyRect);
            DrawEditorLine(block, srcLine, lineText, codeX, lineY, bodyRect);
        }

        if (maxScroll > 0)
            DrawCodeScrollbar(bodyRect, scrollLines, visibleLines, sliceCount);

        _rt.PopAxisAlignedClip();
    }

    private int GetMaxCodeScrollLines(RenderBlock block, Rect bodyRect)
    {
        string body = block.Body ?? string.Empty;
        string[] allLines = body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int sliceCount = allLines.Length;
        if (block.Focused is not null)
        {
            int blockStartLine = block.StartLine ?? 1;
            int sliceStartIdx = Math.Max(0, block.Focused.StartLine - blockStartLine);
            int sliceEndIdx = Math.Min(allLines.Length - 1, block.Focused.EndLine - blockStartLine);
            sliceCount = Math.Max(0, sliceEndIdx - sliceStartIdx + 1);
        }

        int visibleLines = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH));
        return Math.Max(0, sliceCount - visibleLines);
    }

    private void DrawCodeScrollbar(Rect bodyRect, int scrollLines, int visibleLines, int totalLines)
    {
        if (_rt is null || totalLines <= 0) return;
        float trackW = 4f;
        float trackX = (float)bodyRect.Right - trackW - 4;
        float trackY = (float)bodyRect.Y + 8;
        float trackH = (float)bodyRect.Height - 16;
        if (trackH <= 12) return;

        float thumbH = Math.Max(18f, trackH * Math.Min(1f, visibleLines / (float)totalLines));
        float maxThumbY = trackH - thumbH;
        float thumbY = trackY + (totalLines == visibleLines ? 0 : maxThumbY * (scrollLines / (float)Math.Max(1, totalLines - visibleLines)));

        _rt.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(trackX, trackY, trackW, trackH), 2, 2),
            GetBrush(WpfColor.FromArgb(45, 181, 190, 203)));
        _rt.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(trackX, thumbY, trackW, thumbH), 2, 2),
            GetBrush(WpfColor.FromArgb(145, 120, 132, 150)));
    }

    private void DrawEditorLine(RenderBlock block, int srcLine, string lineText, float startX, float lineY, Rect bodyRect)
    {
        if (_rt is null || _dwrite is null || string.IsNullOrEmpty(lineText)) return;

        const float fontSize = 11.5f;
        float maxWidth = (float)(bodyRect.Right - startX - 14);
        if (maxWidth < 4) return;

        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        using var layout = _dwrite.CreateTextLayout(lineText, fmt, maxWidth, fontSize * 2.2f);

        if (block.SemanticTokens is not null)
        {
            foreach (var token in block.SemanticTokens.Where(t => t.Line == srcLine).OrderBy(t => t.Column))
            {
                int start = Math.Max(0, token.Column - 1);
                if (start >= lineText.Length) continue;

                int length = Math.Min(token.Length, lineText.Length - start);
                if (length <= 0) continue;

                layout.SetDrawingEffect(GetBrush(TokenColor(token.Kind)), new TextRange((uint)start, (uint)length));
            }
        }

        _rt.DrawTextLayout(new Vector2(startX, lineY), layout, GetBrush(WpfColor.FromRgb(45, 55, 72)), DrawTextOptions.Clip);
    }

    private void DrawScopeGuides(string lineText, float codeX, float lineY, Rect bodyRect)
    {
        int leadingSpaces = lineText.TakeWhile(c => c == ' ').Count();
        int guideCount = leadingSpaces / 4;
        if (guideCount <= 0 || _rt is null) return;

        var guideBrush = GetBrush(WpfColor.FromArgb(70, 203, 213, 225));
        float top = lineY;
        float bottom = lineY + (float)CodeLineH;
        for (int i = 1; i <= guideCount; i++)
        {
            float x = codeX + i * 4 * (float)CodeCharW - 7;
            if (x <= bodyRect.Left || x >= bodyRect.Right - 12) continue;
            _rt.DrawLine(new Vector2(x, top), new Vector2(x, bottom), guideBrush, 1f);
        }
    }

    private static string NormalizeCodeLine(string text) =>
        text.Replace("\t", "    ", StringComparison.Ordinal);

    private static WpfColor TokenColor(SemanticTokenKind kind) => kind switch
    {
        SemanticTokenKind.Keyword => WpfColor.FromRgb(124, 58, 237),
        SemanticTokenKind.Type => WpfColor.FromRgb(14, 116, 144),
        SemanticTokenKind.Function => WpfColor.FromRgb(37, 99, 235),
        SemanticTokenKind.Property => WpfColor.FromRgb(3, 105, 161),
        SemanticTokenKind.Field => WpfColor.FromRgb(79, 70, 229),
        SemanticTokenKind.String => WpfColor.FromRgb(194, 65, 12),
        SemanticTokenKind.Comment => WpfColor.FromRgb(100, 116, 139),
        SemanticTokenKind.Number => WpfColor.FromRgb(180, 83, 9),
        SemanticTokenKind.Preprocessor => WpfColor.FromRgb(107, 114, 128),
        SemanticTokenKind.Operator => WpfColor.FromRgb(75, 85, 99),
        _ => WpfColor.FromRgb(17, 24, 39)
    };

    private void DrawExtractHighlight(Rect codeRect)
    {
        if (_extractHoverStartLine <= 0) return;
        int startLine = _extractHoverBlock!.Block.Focused?.StartLine ?? _extractHoverBlock.Block.StartLine ?? 1;
        float relStart = (_extractHoverStartLine - startLine) * (float)CodeLineH;
        float relEnd = (_extractHoverEndLine - startLine + 1) * (float)CodeLineH;
        float y1 = (float)codeRect.Y + relStart;
        float y2 = Math.Min((float)codeRect.Bottom, (float)codeRect.Y + relEnd);
        if (y2 <= y1) return;
        WpfColor c = _isFocusMode
            ? WpfColor.FromRgb(46, 125, 215)
            : WpfColor.FromRgb(235, 154, 40);
        _rt!.FillRectangle(new RectangleF((float)codeRect.X, y1, (float)codeRect.Width, y2 - y1),
            GetBrush(WpfColor.FromArgb(48, c.R, c.G, c.B)));
        string hint = _isFocusMode ? "Ctrl+click â€” focus this function" : "Alt+click â€” extract to new block";
        DrawText(hint, (float)codeRect.X + 8, y1 - 14, 240, 9, c);
    }

    // -----------------------------------------------------------------------
    // Minimap
    // -----------------------------------------------------------------------
    private void DrawMinimap(Size viewSize)
    {
        if (_snapshot.WorldBounds.IsEmpty) return;
        float mx = (float)(viewSize.Width - MinimapW - MinimapMargin);
        float my = (float)(viewSize.Height - MinimapH - MinimapMargin);
        float mw = (float)MinimapW, mh = (float)MinimapH;

        _rt!.FillRectangle(new RectangleF(mx, my, mw, mh), GetBrush(WpfColor.FromArgb(235, 255, 255, 255)));
        _rt.DrawRectangle(new RectangleF(mx, my, mw, mh), GetBrush(WpfColor.FromArgb(220, 226, 232, 240)), 1f);

        Rect wb = _snapshot.WorldBounds;
        double scaleX = MinimapW / wb.Width, scaleY = MinimapH / wb.Height;
        double scale = Math.Min(scaleX, scaleY) * 0.9;
        double offX = mx + (MinimapW - wb.Width * scale) / 2 - wb.X * scale;
        double offY = my + (MinimapH - wb.Height * scale) / 2 - wb.Y * scale;

        foreach (var b in _snapshot.Blocks)
        {
            float bx = (float)(b.Bounds.X * scale + offX);
            float by = (float)(b.Bounds.Y * scale + offY);
            float bw = (float)Math.Max(4, b.Bounds.Width * scale);
            float bh = (float)Math.Max(3, b.Bounds.Height * scale);
            WpfColor c = b.Block.Kind == BlockKind.Note ? WpfColor.FromRgb(226, 186, 76) : WpfColor.FromRgb(46, 125, 215);
            _rt.FillRectangle(new RectangleF(bx, by, bw, bh), GetBrush(WpfColor.FromArgb(140, c.R, c.G, c.B)));
        }

        // Viewport indicator
        Point wvTL = ToWorld(new Point(0, 0));
        Point wvBR = ToWorld(new Point(viewSize.Width, viewSize.Height));
        float vx = (float)(wvTL.X * scale + offX);
        float vy = (float)(wvTL.Y * scale + offY);
        float vw = (float)((wvBR.X - wvTL.X) * scale);
        float vh = (float)((wvBR.Y - wvTL.Y) * scale);
        _rt.DrawRectangle(new RectangleF(vx, vy, Math.Max(2, vw), Math.Max(2, vh)),
            GetBrush(WpfColor.FromArgb(210, 46, 125, 215)), 1f);
    }

    private void DrawMarquee()
    {
        if (!_isMarquee || _marqueeStart is null || _marqueeEnd is null) return;
        Rect r = new(_marqueeStart.Value, _marqueeEnd.Value);
        _rt!.FillRectangle(ToRF(r), GetBrush(WpfColor.FromArgb(36, 46, 125, 215)));
        _rt.DrawRectangle(ToRF(r), GetBrush(WpfColor.FromArgb(150, 46, 125, 215)), 1f);
    }

    // -----------------------------------------------------------------------
    // Text helpers
    // -----------------------------------------------------------------------
    private void DrawText(string text, float x, float y, float maxWidth, float fontSize, WpfColor color, bool rightAlign = false)
    {
        if (string.IsNullOrEmpty(text) || _rt is null || _dwrite is null || maxWidth < 4) return;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        if (rightAlign) fmt.TextAlignment = DWriteTextAlignment.Trailing;
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, fontSize * 2.2f);
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
        if (rightAlign) fmt.TextAlignment = DWriteTextAlignment.Leading;
    }

    private void DrawWrappedText(string text, float x, float y, float maxWidth, float maxHeight, float fontSize, WpfColor color)
    {
        if (string.IsNullOrEmpty(text) || _rt is null || _dwrite is null) return;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, maxHeight);
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
    }

    private IDWriteTextFormat GetTextFormat(float size)
    {
        string key = $"JetBrainsMono:{size:F1}";
        if (_textFormats.TryGetValue(key, out var fmt)) return fmt;
        fmt = _dwrite!.CreateTextFormat("JetBrains Mono NL", DWriteFontWeight.Normal, DWriteFontStyle.Normal, DWriteFontStretch.Normal, size);
        fmt.WordWrapping = WordWrapping.NoWrap;
        _textFormats[key] = fmt;
        return fmt;
    }

    // -----------------------------------------------------------------------
    // D2D resource helpers
    // -----------------------------------------------------------------------
    private ID2D1SolidColorBrush GetBrush(WpfColor color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = _rt!.CreateSolidColorBrush(ToColor4(color));
        _brushes[key] = b;
        return b;
    }

    private bool EnsureRT()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return false;
        if (_factory is null) _factory = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        if (_dwrite is null) _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(FactoryType.Shared);
        if (_rt is not null) { IsReady = true; return true; }

        GetClientRect(_hwnd, out RECT cr);
        int pw = Math.Max(1, cr.Right - cr.Left), ph = Math.Max(1, cr.Bottom - cr.Top);
        var rtProps = new RenderTargetProperties(
            RenderTargetType.Hardware,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 0, 0,
            RenderTargetUsage.None, FeatureLevel.Default);
        var hwndProps = new HwndRenderTargetProperties { Hwnd = _hwnd, PixelSize = new Vortice.Mathematics.SizeI(pw, ph), PresentOptions = PresentOptions.Immediately };
        _rt = _factory.CreateHwndRenderTarget(rtProps, hwndProps);
        IsReady = _rt is not null;
        return _rt is not null;
    }

    private void ResizeRT()
    {
        if (_rt is null || _hwnd == IntPtr.Zero) return;
        GetClientRect(_hwnd, out RECT cr);
        int pw = Math.Max(1, cr.Right - cr.Left), ph = Math.Max(1, cr.Bottom - cr.Top);
        try { _rt.Resize(new Vortice.Mathematics.SizeI(pw, ph)); }
        catch { DisposeRenderTarget(); EnsureRT(); }
    }

    private void DisposeRenderTarget()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
        foreach (var f in _textFormats.Values) f.Dispose();
        _textFormats.Clear();
        foreach (var g in _connectionGeoms.Values) g.Dispose();
        _connectionGeoms.Clear();
        _rt?.Dispose(); _rt = null;
    }

    private float InvStroke(float worldStroke) =>
        (float)Math.Max(0.5, worldStroke / _camera.Zoom);

    private static Color4 ToColor4(WpfColor c) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static RectangleF ToRF(Rect r) =>
        new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static WpfColor ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith('#')) hex = hex[1..];
            uint val = Convert.ToUInt32(hex, 16);
            return hex.Length == 6
                ? WpfColor.FromRgb((byte)(val >> 16), (byte)(val >> 8), (byte)val)
                : WpfColor.FromArgb((byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val);
        }
        catch { return WpfColor.FromRgb(100, 140, 200); }
    }
}

