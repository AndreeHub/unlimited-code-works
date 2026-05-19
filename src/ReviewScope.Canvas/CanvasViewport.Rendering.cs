using ReviewScope.Domain;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

internal sealed record ImageBitmapResource(ID2D1Bitmap Bitmap, int PixelWidth, int PixelHeight, DateTime LastWriteUtc) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

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
        var minor = GetBrush(WpfColor.FromArgb(62, 198, 207, 219));
        var major = GetBrush(WpfColor.FromArgb(104, 172, 184, 199));
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
        const double worldSpacing = 24;
        float spacing = (float)(worldSpacing * _camera.Zoom);
        while (spacing < 13f) spacing *= 2f;
        while (spacing > 30f) spacing *= 0.5f;

        float ox = (float)(_camera.OffsetX % spacing);
        float oy = (float)(_camera.OffsetY % spacing);
        byte alpha = (byte)Math.Clamp(132 - Math.Abs(spacing - 20f) * 2.0f, 72, 132);
        var dot = GetBrush(WpfColor.FromArgb(alpha, 176, 186, 200));
        float r = Math.Clamp(spacing / 14f, 1.15f, 1.85f);
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

        WpfColor lineColor = conn.IsSelected
            ? WpfColor.FromArgb(230, 32, 104, 192)
            : ParseColor(conn.Stroke);
        float stroke = InvStroke(conn.IsSelected ? 2.2f : 1.6f);

        ID2D1PathGeometry geom = GetOrBuildConnectionGeometry(connVis);
        var brush = GetBrush(lineColor);
        _rt!.DrawGeometry(geom, brush, stroke);
        if (conn.ArrowKind == ConnectorArrowKind.None)
        {
            // no arrowhead
        }
        else if (conn.ArrowPosition is double t)
        {
            t = Math.Clamp(t, 0.04, 0.96);
            Point arrowPoint = EvaluateConnectionPoint(connVis, t);
            Vector2 tangent = EvaluateConnectionTangent(connVis, t);
            if (!conn.ArrowForward || conn.ArrowKind == ConnectorArrowKind.Backward) tangent = -tangent;
            DrawInlineArrow(arrowPoint, tangent, brush, stroke);
        }
        else
        {
            GetConnectionPathPoints(connVis, out Point startLead, out _, out Point endLead);
            if (conn.ArrowKind is ConnectorArrowKind.Forward or ConnectorArrowKind.Both)
                DrawArrowhead(connVis.End, endLead, brush, stroke);
            if (conn.ArrowKind is ConnectorArrowKind.Backward or ConnectorArrowKind.Both)
                DrawArrowhead(connVis.Start, startLead, brush, stroke);
        }

        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            Point mid = new((connVis.Start.X + connVis.End.X) / 2, (connVis.Start.Y + connVis.End.Y) / 2);
            DrawText(conn.Label!, (float)mid.X - 60, (float)mid.Y - 10, 120, 10, WpfColor.FromRgb(83, 96, 112));
        }

        if (conn.IsSelected)
            DrawConnectionControlNodes(connVis);
    }

    private void DrawInProgressConnection()
    {
        Point end = _connectionHoverTargetWorld ?? _connectionCurrentWorld;
        var color = _connectionHoverTargetWorld is not null
            ? WpfColor.FromArgb(220, 35, 162, 109)
            : WpfColor.FromArgb(180, 69, 132, 203);
        if (_rewireEndpointKind == ConnectionEndpointKind.Source)
            DrawConnectionPreview(end, _connectionHoverTargetAnchorIndex, _rewireFixedWorld, _rewireFixedAnchorIndex, _connectionDraftMidPoint, _connectionDraftMidPointBends, color);
        else
            DrawConnectionPreview(_connectionSourceWorld, _connectionSourceAnchorIndex, end, _connectionHoverTargetAnchorIndex, _connectionDraftMidPoint, _connectionDraftMidPointBends, color);
    }

    private void DrawConnectionControlNodes(SceneConnectionVisual connVis)
    {
        GetConnectionPathPoints(connVis, out _, out Point middleControl, out _);
        var nodeBrush = GetBrush(_selectedConnectionId == connVis.Connection.Id && _selectedConnectionControlKind == ConnectionControlNodeKind.Middle
            ? WpfColor.FromArgb(240, 35, 162, 109)
            : WpfColor.FromArgb(230, 32, 104, 192));

        DrawControlPoint(middleControl, nodeBrush);
    }

    private void DrawConnectionPreview(Point start, int? sourceAnchorIndex, Point end, int? targetAnchorIndex, Point? draftMid, bool draftMidBends, WpfColor color)
    {
        if (_rt is null || _factory is null) return;
        Vector2 sourceNormal = sourceAnchorIndex is int source ? GetConnectionAnchorNormal(source) : Vector2.Zero;
        Vector2 targetNormal = targetAnchorIndex is int target ? GetConnectionAnchorNormal(target) : Vector2.Zero;
        Point startLead = new(start.X + sourceNormal.X * ConnectionLeadDistance, start.Y + sourceNormal.Y * ConnectionLeadDistance);
        Point endLead = new(end.X + targetNormal.X * ConnectionLeadDistance, end.Y + targetNormal.Y * ConnectionLeadDistance);
        Point mid = draftMid ?? new Point((startLead.X + endLead.X) / 2, (startLead.Y + endLead.Y) / 2);
        Point c1;
        Point c2;
        if (draftMid is not null && draftMidBends)
        {
            Point control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            c1 = new(startLead.X + (control.X - startLead.X) * 2 / 3, startLead.Y + (control.Y - startLead.Y) * 2 / 3);
            c2 = new(endLead.X + (control.X - endLead.X) * 2 / 3, endLead.Y + (control.Y - endLead.Y) * 2 / 3);
        }
        else
        {
            double dx = endLead.X - startLead.X;
            double dy = endLead.Y - startLead.Y;
            double tangent = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) * 0.42, MinAutoTangent, MaxAutoTangent);
            c1 = new(startLead.X + sourceNormal.X * tangent, startLead.Y + sourceNormal.Y * tangent);
            c2 = new(endLead.X + targetNormal.X * tangent, endLead.Y + targetNormal.Y * tangent);
        }

        using var path = _factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(new Vector2((float)start.X, (float)start.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2((float)startLead.X, (float)startLead.Y));
        sink.AddBezier(new D2DBezierSegment(
            new Vector2((float)c1.X, (float)c1.Y),
            new Vector2((float)c2.X, (float)c2.Y),
            new Vector2((float)endLead.X, (float)endLead.Y)));
        sink.AddLine(new Vector2((float)end.X, (float)end.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        var brush = GetBrush(color);
        _rt.DrawGeometry(path, brush, InvStroke(_connectionHoverTargetWorld is not null ? 2.1f : 1.5f));
        if (draftMid is Point p)
            DrawControlPoint(p, GetBrush(WpfColor.FromArgb(230, 35, 162, 109)));
    }

    private void DrawControlPoint(Point p, ID2D1SolidColorBrush brush)
    {
        float r = Math.Max(3f, InvStroke(4f));
        _rt!.FillEllipse(new Ellipse(new Vector2((float)p.X, (float)p.Y), r, r), brush);
    }

    private void DrawControlNode(Point p, ID2D1SolidColorBrush fill, ID2D1SolidColorBrush stroke)
    {
        float r = Math.Max(4f, InvStroke(5.5f));
        var e = new Ellipse(new Vector2((float)p.X, (float)p.Y), r, r);
        _rt!.FillEllipse(e, fill);
        _rt.DrawEllipse(e, stroke, InvStroke(1.6f));
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
        if (connVis.Connection.RouteKind is ConnectorRouteKind.Straight or ConnectorRouteKind.Orthogonal)
        {
            var linePath = _factory!.CreatePathGeometry();
            using var lineSink = linePath.Open();
            var points = BuildConnectionPolyline(connVis);
            lineSink.BeginFigure(new Vector2(x1, y1), FigureBegin.Hollow);
            foreach (var point in points.Skip(1))
                lineSink.AddLine(new Vector2((float)point.X, (float)point.Y));
            lineSink.EndFigure(FigureEnd.Open);
            lineSink.Close();

            _connectionGeoms[connVis.Connection.Id] = linePath;
            return linePath;
        }

        GetConnectionPathPoints(connVis, out Point startLead, out Point mid, out Point endLead);
        Point c1;
        Point c2;
        if (HasCustomConnectionMidPoint(connVis.Connection) && connVis.Connection.MidControlBends)
        {
            Point control = GetQuadraticControlThroughMid(startLead, mid, endLead);
            c1 = new(startLead.X + (control.X - startLead.X) * 2 / 3, startLead.Y + (control.Y - startLead.Y) * 2 / 3);
            c2 = new(endLead.X + (control.X - endLead.X) * 2 / 3, endLead.Y + (control.Y - endLead.Y) * 2 / 3);
        }
        else
        {
            GetAutoCubicControls(connVis, startLead, endLead, out c1, out c2);
        }

        var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(new Vector2(x1, y1), FigureBegin.Hollow);
        sink.AddLine(new Vector2((float)startLead.X, (float)startLead.Y));
        sink.AddBezier(new D2DBezierSegment(
            new Vector2((float)c1.X, (float)c1.Y),
            new Vector2((float)c2.X, (float)c2.Y),
            new Vector2((float)endLead.X, (float)endLead.Y)));
        sink.AddLine(new Vector2(x2, y2));
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
            case BlockKind.MarkdownDoc:
                DrawMarkdownDocBlock(blockVis);
                break;
            case BlockKind.Shape:
                DrawShapeBlock(blockVis);
                break;
            case BlockKind.Text:
                DrawTextBlock(blockVis);
                break;
            case BlockKind.Image:
                DrawImageBlock(blockVis);
                break;
            case BlockKind.Container:
                DrawContainerBlock(blockVis);
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
            BlockKind.MarkdownDoc => WpfColor.FromRgb(255, 255, 255),
            BlockKind.Shape => ParseColor(block.Style?.Fill ?? "#EFF6FF"),
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
        string icon = isFocused ? "â-‰ " : block.Kind == BlockKind.Extract ? "âš™ " : "";
        string titleText = isFocused
            ? icon + block.Focused!.SymbolName
            : icon + block.Title;
        DrawText(titleText, (float)outer.X + 14, (float)outer.Y + 12, (float)outer.Width - 60, 14, WpfColor.FromRgb(31, 41, 51));

        string subtitle = isFocused
            ? $"in {block.Title}  *  lines {block.Focused!.StartLine}-{block.Focused.EndLine}"
            : block.Subtitle;
        DrawText(subtitle, (float)outer.X + 14, (float)outer.Y + 36, (float)outer.Width - 60, 10, WpfColor.FromRgb(117, 128, 143));

        if (block.IsCollapsed)
        {
            DrawText("[ collapsed - double-click to expand ]",
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
            ? $"lines {block.StartLine}-{block.EndLine}  *  {footerFileName}"
            : isFocused
                ? $"focused - Ctrl+click another line to refocus  *  {footerFileName}"
                : footerFileName;
        DrawText(footerText, (float)outer.X + 14, (float)footer.Y + 8, (float)outer.Width - 60, 9.5f, WpfColor.FromRgb(117, 128, 143));

        // Resize handle
        float rh = 12;
        _rt.FillRectangle(new RectangleF((float)outer.Right - rh, (float)outer.Bottom - rh, rh, rh),
            GetBrush(WpfColor.FromArgb(60, accent.R, accent.G, accent.B)));

        // Restore button for focused blocks
        if (isFocused) DrawRestoreButton(outer, accent);

        if (ShouldDrawConnectionAnchors(block))
            DrawConnectionAnchors(block, outer, accent);

    }

    private bool ShouldDrawConnectionAnchors(RenderBlock block) =>
        _camera.Zoom > UltraCompactZoom
        && block.Kind is not BlockKind.Note
        && (block.IsSelected || _isDrawingConnection || block.Key == _hoverAnchorBlockKey);

    private void DrawConnectionAnchors(RenderBlock block, Rect bounds, WpfColor accent)
    {
        var fill = GetBrush(WpfColor.FromArgb(230, 255, 255, 255));
        var stroke = GetBrush(WpfColor.FromArgb(210, accent.R, accent.G, accent.B));
        var hoverStroke = GetBrush(WpfColor.FromArgb(235, 35, 162, 109));
        var sourceStroke = GetBrush(WpfColor.FromArgb(235, 32, 104, 192));
        float r = Math.Max(3.5f, InvStroke(4.5f));
        for (int i = 0; i < 16; i++)
        {
            Point p = GetConnectionAnchorPoint(bounds, i);
            bool isSource = _isDrawingConnection
                && block.Key == _connectionSourceKey
                && i == _connectionSourceAnchorIndex;
            bool isTarget = _isDrawingConnection
                && block.Key == _connectionHoverTargetKey
                && i == _connectionHoverTargetAnchorIndex;
            bool isHover = block.Key == _hoverAnchorBlockKey && i == _hoverAnchorIndex;
            float rr = isTarget || isSource || isHover ? r * 1.45f : r;
            var ellipse = new Ellipse(new Vector2((float)p.X, (float)p.Y), r, r);
            _rt!.FillEllipse(ellipse, fill);
            _rt.DrawEllipse(new Ellipse(new Vector2((float)p.X, (float)p.Y), rr, rr),
                isTarget || isHover ? hoverStroke : isSource ? sourceStroke : stroke,
                InvStroke(isTarget || isSource || isHover ? 1.8f : 1.2f));
        }
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
        bool isSelected = block.IsSelected;
        bool isEditing = _editingNoteKey == block.Key;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;

        string bodyText = isEditing ? _editBody : (block.Body ?? string.Empty);

        // Shadow
        _rt!.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(x + 2, y + 5, w, h), 8, 8),
            GetBrush(WpfColor.FromArgb(20, 35, 49, 66)));

        // Selection / edit ring
        if (isSelected || isEditing)
        {
            float pad = InvStroke(1.5f);
            _rt.DrawRoundedRectangle(
                new RoundedRectangle(new RectangleF(x - pad, y - pad, w + pad * 2, h + pad * 2), 10, 10),
                GetBrush(WpfColor.FromArgb(160, 46, 125, 215)), InvStroke(1.5f));
        }

        // Fill
        var rr = new RoundedRectangle(new RectangleF(x, y, w, h), 8, 8);
        _rt.FillRoundedRectangle(rr, GetBrush(WpfColor.FromRgb(250, 237, 180)));

        // Border
        WpfColor borderColor = isSelected || isEditing
            ? WpfColor.FromArgb(235, 46, 125, 215)
            : WpfColor.FromArgb(180, 210, 190, 80);
        _rt.DrawRoundedRectangle(rr, GetBrush(borderColor),
            isSelected || isEditing ? InvStroke(2.0f) : InvStroke(1.2f));

        // Body text + cursor (clipped)
        _rt.PushAxisAlignedClip(
            new RectangleF(x + 2, y + 2, w - 4, h - 4),
            AntialiasMode.PerPrimitive);
        if (isEditing)
            DrawEditSelection(bodyText, 11f, x + 10, y + 8, w - 20, wrap: true);
        if (!string.IsNullOrEmpty(bodyText))
            DrawWrappedText(bodyText, x + 10, y + 8, w - 20, h - 16, 11f,
                WpfColor.FromRgb(60, 52, 18), wrap: true);
        if (isEditing && _editCursorVisible)
            DrawNoteCursor(bodyText, 11f, x + 10, y + 8, w - 20, _editCursorPos, wrap: true);
        _rt.PopAxisAlignedClip();

        // Corner resize handles (selected but not editing)
        if (isSelected && !isEditing && _camera.Zoom > UltraCompactZoom)
        {
            float hs = (float)NoteCornerHandleSize;
            DrawNoteCornerHandle(x, y, hs);
            DrawNoteCornerHandle(x + w - hs, y, hs);
            DrawNoteCornerHandle(x, y + h - hs, hs);
            DrawNoteCornerHandle(x + w - hs, y + h - hs, hs);
        }
    }

    private void DrawNoteCornerHandle(float x, float y, float size)
    {
        var rect = new RectangleF(x, y, size, size);
        _rt!.FillRectangle(rect, GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _rt.DrawRectangle(rect, GetBrush(WpfColor.FromArgb(220, 46, 125, 215)), InvStroke(1.0f));
    }

    private void DrawMarkdownDocBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        DrawCardShell(outer, block.IsSelected, ParseColor(block.Style?.Stroke ?? "#E2E8F0"), 8);
        _rt!.FillRectangle(new RectangleF(x, y, w, 46), GetBrush(WpfColor.FromRgb(248, 250, 252)));
        DrawText(block.Title, x + 16, y + 12, w - 32, 15, WpfColor.FromRgb(17, 24, 39));
        DrawText(block.Subtitle, x + 16, y + 30, w - 32, 9.5f, WpfColor.FromRgb(100, 116, 139));
        _rt.DrawLine(new Vector2(x, y + 46), new Vector2(x + w, y + 46), GetBrush(WpfColor.FromArgb(160, 226, 232, 240)), InvStroke(1));

        _rt.PushAxisAlignedClip(new RectangleF(x + 14, y + 58, w - 28, h - 72), AntialiasMode.PerPrimitive);
        float cy = y + 58;
        foreach (string rawLine in (block.Body ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Take(80))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0) { cy += 10; continue; }
            int level = line.TakeWhile(c => c == '#').Count();
            if (level > 0 && level <= 4 && line.Length > level && line[level] == ' ')
            {
                float size = level == 1 ? 18 : level == 2 ? 15 : 13;
                DrawText(line[(level + 1)..], x + 18, cy, w - 42, size, WpfColor.FromRgb(15, 23, 42));
                cy += size + 12;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                DrawText("*", x + 20, cy, 16, 12, WpfColor.FromRgb(46, 125, 215));
                DrawWrappedText(line[2..], x + 38, cy, w - 58, 42, 11.5f, WpfColor.FromRgb(51, 65, 85), wrap: true);
                cy += 24;
            }
            else
            {
                DrawWrappedText(line, x + 18, cy, w - 42, 48, 11.5f, WpfColor.FromRgb(51, 65, 85), wrap: true);
                cy += 24;
            }
            if (cy > y + h - 20) break;
        }
        _rt.PopAxisAlignedClip();
        DrawGenericResizeHandle(outer, ParseColor(block.Style?.Stroke ?? "#2E7DD7"));
    }

    private void DrawShapeBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        WpfColor fill = ParseColor(block.Style?.Fill ?? "#EFF6FF");
        WpfColor stroke = ParseColor(block.Style?.Stroke ?? "#2E7DD7");
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        string shape = block.ShapeType ?? "service";

        if (shape is "database" or "cache" or "queue")
        {
            var rr = new RoundedRectangle(ToRF(outer), 16, 16);
            _rt!.FillRoundedRectangle(rr, GetBrush(fill));
            _rt.DrawRoundedRectangle(rr, GetBrush(stroke), block.IsSelected ? InvStroke(2) : InvStroke(1.3f));
            _rt.DrawEllipse(new Ellipse(new Vector2(x + w / 2, y + 18), w / 2 - 8, 14), GetBrush(stroke), InvStroke(1.2f));
        }
        else if (shape == "risk" || shape == "decision")
        {
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(new Vector2(x + w / 2, y), FigureBegin.Filled);
                sink.AddLine(new Vector2(x + w, y + h / 2));
                sink.AddLine(new Vector2(x + w / 2, y + h));
                sink.AddLine(new Vector2(x, y + h / 2));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _rt!.FillGeometry(path, GetBrush(fill));
            _rt.DrawGeometry(path, GetBrush(stroke), block.IsSelected ? InvStroke(2) : InvStroke(1.3f));
        }
        else
        {
            var rr = new RoundedRectangle(ToRF(outer), 8, 8);
            _rt!.FillRoundedRectangle(rr, GetBrush(fill));
            _rt.DrawRoundedRectangle(rr, GetBrush(stroke), block.IsSelected ? InvStroke(2) : InvStroke(1.3f));
        }

        DrawText(block.Title, x + 14, y + h / 2 - 8, w - 28, 13, ParseColor(block.Style?.Text ?? "#111827"));
        if (ShouldDrawConnectionAnchors(block)) DrawConnectionAnchors(block, outer, stroke);
        DrawGenericResizeHandle(outer, stroke);
    }

    private void DrawTextBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        DrawCardShell(outer, block.IsSelected, ParseColor(block.Style?.Stroke ?? "#CBD5E1"), 6);
        DrawWrappedText(block.Body ?? block.Title, (float)outer.X + 12, (float)outer.Y + 12, (float)outer.Width - 24, (float)outer.Height - 24, 14, ParseColor(block.Style?.Text ?? "#111827"), wrap: true);
        DrawGenericResizeHandle(outer, ParseColor(block.Style?.Stroke ?? "#CBD5E1"));
    }

    private void DrawImageBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        DrawCardShell(outer, block.IsSelected, ParseColor(block.Style?.Stroke ?? "#CBD5E1"), 8);
        DrawText(block.Title, x + 14, y + 12, w - 28, 12, WpfColor.FromRgb(30, 41, 59));

        var imageArea = new Rect(outer.X + 12, outer.Y + 38, Math.Max(1, outer.Width - 24), Math.Max(1, outer.Height - 56));
        _rt!.FillRectangle(ToRF(imageArea), GetBrush(WpfColor.FromRgb(241, 245, 249)));
        var image = GetOrLoadImageBitmap(block.Source?.AssetPath ?? block.Source?.SourcePath);
        if (image is not null)
        {
            Rect dest = FitRect(imageArea, image.PixelWidth, image.PixelHeight);
            var target = new Vortice.Mathematics.Rect(
                (float)dest.Left,
                (float)dest.Top,
                (float)dest.Width,
                (float)dest.Height);
            var source = new Vortice.Mathematics.Rect(0, 0, image.PixelWidth, image.PixelHeight);
            _rt.DrawBitmap(image.Bitmap, target, (float)Math.Clamp(block.Style?.Opacity ?? 1, 0.05, 1), BitmapInterpolationMode.Linear, source);
        }
        else
        {
            DrawText("Image unavailable", x + 20, y + h / 2 - 10, w - 40, 13, WpfColor.FromRgb(100, 116, 139));
            DrawWrappedText(block.Source?.AssetPath ?? block.Body ?? string.Empty, x + 20, y + h / 2 + 12, w - 40, 42, 9, WpfColor.FromRgb(100, 116, 139), wrap: true);
        }

        WpfColor stroke = ParseColor(block.Style?.Stroke ?? "#CBD5E1");
        if (ShouldDrawConnectionAnchors(block))
            DrawConnectionAnchors(block, outer, stroke);
        DrawGenericResizeHandle(outer, stroke);
    }

    private ImageBitmapResource? GetOrLoadImageBitmap(string? path)
    {
        if (_rt is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        if (_imageBitmaps.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWrite)
            return cached;

        if (cached is not null)
            cached.Dispose();
        _imageBitmaps.Remove(path);

        try
        {
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.UriSource = new Uri(path, UriKind.Absolute);
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            BitmapSource source = bitmapImage.Format == PixelFormats.Bgra32
                ? bitmapImage
                : new FormatConvertedBitmap(bitmapImage, PixelFormats.Bgra32, null, 0);
            if (source.CanFreeze) source.Freeze();

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);
            ForceOpaqueAlpha(pixels);

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var props = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore));
                var bitmap = _rt.CreateBitmap(new Vortice.Mathematics.SizeI(width, height), handle.AddrOfPinnedObject(), (uint)stride, props);
                var resource = new ImageBitmapResource(bitmap, width, height, lastWrite);
                _imageBitmaps[path] = resource;
                return resource;
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }

    private static void ForceOpaqueAlpha(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
    }

    private static Rect FitRect(Rect area, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return area;
        double scale = Math.Min(area.Width / pixelWidth, area.Height / pixelHeight);
        double width = pixelWidth * scale;
        double height = pixelHeight * scale;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    private void DrawContainerBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        if (IsColorGroup(block))
        {
            DrawColorGroupBlock(blockVis);
            return;
        }

        WpfColor stroke = ParseColor(block.Style?.Stroke ?? "#64748B");
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        _rt!.FillRoundedRectangle(new RoundedRectangle(ToRF(outer), 8, 8), GetBrush(WpfColor.FromArgb(58, 248, 250, 252)));
        _rt.DrawRoundedRectangle(new RoundedRectangle(ToRF(outer), 8, 8), GetBrush(stroke), block.IsSelected ? InvStroke(2) : InvStroke(1.3f));
        _rt.FillRectangle(new RectangleF(x, y, w, 32), GetBrush(WpfColor.FromArgb(70, stroke.R, stroke.G, stroke.B)));
        DrawText(block.Title, x + 12, y + 8, w - 24, 12, WpfColor.FromRgb(51, 65, 85));
        DrawGenericResizeHandle(outer, stroke);
    }

    private void DrawColorGroupBlock(SceneBlockVisual blockVis)
    {
        var block = blockVis.Block;
        Rect outer = blockVis.Bounds;
        var style = block.Style ?? new BoardItemStyle("#EAF4FF", "#2E7DD7", "#17324D", 1.6, Opacity: 0.18);
        WpfColor fill = ParseColor(style.Fill);
        WpfColor stroke = ParseColor(style.Stroke);
        WpfColor text = ParseColor(style.Text);
        float radius = (float)Math.Clamp(style.CornerRadius, 4, 10);
        float x = (float)outer.X, y = (float)outer.Y, w = (float)outer.Width, h = (float)outer.Height;
        bool isEditing = _editingGroupKey == block.Key;
        string titleText = isEditing ? _editTitle : block.Title;

        _rt!.FillRoundedRectangle(
            new RoundedRectangle(ToRF(outer), radius, radius),
            GetBrush(WpfColor.FromArgb((byte)Math.Clamp(style.Opacity * 255, 24, 72), fill.R, fill.G, fill.B)));
        _rt.DrawRoundedRectangle(
            new RoundedRectangle(ToRF(outer), radius, radius),
            GetBrush(block.IsSelected ? WpfColor.FromRgb(46, 125, 215) : stroke),
            block.IsSelected ? InvStroke(2.2f) : InvStroke((float)style.StrokeWidth));

        float headerH = block.IsCollapsed ? 42 : 36;
        _rt.FillRectangle(new RectangleF(x, y, w, headerH), GetBrush(WpfColor.FromArgb(54, stroke.R, stroke.G, stroke.B)));
        float titleSize = block.IsCollapsed ? 17f : 16f;
        if (isEditing)
        {
            float titleW = w - 58;
            _rt.FillRoundedRectangle(
                new RoundedRectangle(new RectangleF(x + 10, y + 5, titleW + 8, 27), 4, 4),
                GetBrush(WpfColor.FromArgb(215, 255, 255, 255)));
            DrawEditSelection(titleText, titleSize, x + 14, y + 8, titleW, wrap: false);
            DrawText(titleText, x + 14, y + 8, titleW, titleSize, text);
            if (_editCursorVisible)
                DrawNoteCursor(titleText, titleSize, x + 14, y + 8, titleW, _editCursorPos, wrap: false);
        }
        else
        {
            DrawText(titleText, x + 14, y + 8, w - 58, titleSize, text);
        }

        if (block.IsCollapsed)
        {
            DrawText("double-click to expand", x + 14, y + 28, w - 58, 8.5f, WpfColor.FromArgb(180, text.R, text.G, text.B));
            DrawCollapsedGroupSummary(block, new Rect(outer.X + 12, outer.Y + headerH + 10, outer.Width - 24, outer.Height - headerH - 20), stroke);
        }
        else
        {
            var count = GetGroupMemberBlocks(block).Count;
            string subtitle = count == 0 ? "empty group" : $"{count} item{(count == 1 ? string.Empty : "s")}";
            DrawText(subtitle, x + w - 150, y + 10, 132, 9.5f, WpfColor.FromArgb(180, text.R, text.G, text.B), rightAlign: true);
            DrawGenericResizeHandle(outer, stroke);
        }
    }

    private void DrawCollapsedGroupSummary(RenderBlock group, Rect area, WpfColor accent)
    {
        var members = GetGroupMemberBlocks(group)
            .Where(b => b.Kind is BlockKind.File or BlockKind.Extract)
            .Take(12)
            .ToList();
        if (members.Count == 0)
        {
            DrawText("empty", (float)area.X, (float)area.Y + 8, (float)area.Width, 10, WpfColor.FromArgb(160, accent.R, accent.G, accent.B));
            return;
        }

        const float iconW = 26, iconH = 22, gap = 7;
        float x = (float)area.X;
        float y = (float)area.Y;
        int perRow = Math.Max(1, (int)Math.Floor((area.Width + gap) / (iconW + gap)));
        for (int i = 0; i < members.Count; i++)
        {
            int row = i / perRow;
            int col = i % perRow;
            float ix = x + col * (iconW + gap);
            float iy = y + row * (iconH + gap);
            if (iy + iconH > area.Bottom) break;

            var rr = new RoundedRectangle(new RectangleF(ix, iy, iconW, iconH), 4, 4);
            _rt!.FillRoundedRectangle(rr, GetBrush(WpfColor.FromRgb(255, 255, 255)));
            _rt.DrawRoundedRectangle(rr, GetBrush(WpfColor.FromArgb(150, accent.R, accent.G, accent.B)), InvStroke(1));
            _rt.FillRectangle(new RectangleF(ix + 4, iy + 5, iconW - 8, 2.2f), GetBrush(WpfColor.FromArgb(110, accent.R, accent.G, accent.B)));
            _rt.FillRectangle(new RectangleF(ix + 4, iy + 10, iconW - 12, 2.2f), GetBrush(WpfColor.FromArgb(70, accent.R, accent.G, accent.B)));
            _rt.FillRectangle(new RectangleF(ix + 4, iy + 15, iconW - 15, 2.2f), GetBrush(WpfColor.FromArgb(70, accent.R, accent.G, accent.B)));
        }

        int remaining = GetGroupMemberBlocks(group).Count(b => b.Kind is BlockKind.File or BlockKind.Extract) - members.Count;
        if (remaining > 0)
            DrawText($"+{remaining}", (float)area.Right - 32, (float)area.Bottom - 18, 32, 10, WpfColor.FromArgb(190, accent.R, accent.G, accent.B), rightAlign: true);
    }

    private List<RenderBlock> GetGroupMemberBlocks(RenderBlock group)
    {
        Rect groupBounds = GetGroupExpandedBounds(group);
        return Scene.Blocks
            .Where(b => !b.Key.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                && !IsColorGroup(b)
                && groupBounds.IntersectsWith(new Rect(b.X, b.Y, b.Width, b.Height)))
            .ToList();
    }

    private void DrawCardShell(Rect outer, bool selected, WpfColor stroke, float radius)
    {
        _rt!.FillRoundedRectangle(new RoundedRectangle(new RectangleF((float)outer.X + 2, (float)outer.Y + 5, (float)outer.Width, (float)outer.Height), radius, radius), GetBrush(WpfColor.FromArgb(16, 35, 49, 66)));
        var shell = new RoundedRectangle(ToRF(outer), radius, radius);
        _rt.FillRoundedRectangle(shell, GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _rt.DrawRoundedRectangle(shell, GetBrush(selected ? WpfColor.FromRgb(46, 125, 215) : stroke), selected ? InvStroke(2) : InvStroke(1.1f));
    }

    private void DrawGenericResizeHandle(Rect outer, WpfColor color)
    {
        if (_camera.Zoom <= UltraCompactZoom) return;
        const float rh = 12;
        _rt!.FillRectangle(new RectangleF((float)outer.Right - rh, (float)outer.Bottom - rh, rh, rh),
            GetBrush(WpfColor.FromArgb(70, color.R, color.G, color.B)));
    }

    private void DrawNoteCursor(string text, float fontSize, float textX, float textY, float maxW, int cursorPos, bool wrap = false)
    {
        if (_dwrite is null || _rt is null) return;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        string layoutText = text.Length == 0 ? " " : text;
        int safePos = Math.Clamp(cursorPos, 0, text.Length);
        using var layout = _dwrite.CreateTextLayout(layoutText, fmt, maxW, 9999f);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        layout.HitTestTextPosition((uint)safePos, false, out float cx, out float cy, out _);
        float lineH = fontSize * 1.35f;
        _rt.DrawLine(
            new Vector2(textX + cx, textY + cy),
            new Vector2(textX + cx, textY + cy + lineH),
            GetBrush(WpfColor.FromArgb(210, 38, 33, 8)), InvStroke(1.5f));
    }

    private void DrawEditSelection(string text, float fontSize, float textX, float textY, float maxW, bool wrap = false)
    {
        if (_dwrite is null || _rt is null) return;
        if (_editSelectionAnchor < 0 || _editSelectionAnchor == _editCursorPos) return;
        int a = _editSelectionAnchor, b = _editCursorPos;
        int start = Math.Min(a, b), end = Math.Max(a, b);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (end <= start) return;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        string layoutText = text.Length == 0 ? " " : text;
        using var layout = _dwrite.CreateTextLayout(layoutText, fmt, maxW, 9999f);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        var metrics = layout.HitTestTextRange((uint)start, (uint)(end - start), 0f, 0f);
        var brush = GetBrush(WpfColor.FromArgb(110, 70, 130, 220));
        foreach (var m in metrics)
        {
            _rt.FillRectangle(new RectangleF(textX + m.Left, textY + m.Top, m.Width, m.Height), brush);
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

        int topPaddingLines = block.Focused is not null ? FocusedCodeTopPaddingLines : 0;
        int visibleLines = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH) - topPaddingLines);
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
            float lineY = (float)bodyRect.Y + (i + topPaddingLines) * (float)CodeLineH;
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

        int topPaddingLines = block.Focused is not null ? FocusedCodeTopPaddingLines : 0;
        int visibleLines = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH) - topPaddingLines);
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

        if (_isExtractMode && block.IsSelected && block.SemanticTokens is not null)
            DrawAltSymbolHighlights(block, srcLine, lineText, startX, lineY, maxWidth, layout);

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

    private void DrawAltSymbolHighlights(RenderBlock block, int srcLine, string lineText, float startX, float lineY, float maxWidth, IDWriteTextLayout layout)
    {
        if (_rt is null || block.SemanticTokens is null) return;

        var fill = GetBrush(WpfColor.FromArgb(72, 235, 154, 40));
        var stroke = GetBrush(WpfColor.FromArgb(150, 235, 154, 40));

        foreach (var token in block.SemanticTokens.Where(t => t.IsSymbolCandidate && t.Line == srcLine).OrderBy(t => t.Column))
        {
            int start = Math.Max(0, token.Column - 1);
            if (start >= lineText.Length) continue;

            int length = Math.Min(token.Length, lineText.Length - start);
            if (length <= 0) continue;

            var metrics = layout.HitTestTextRange((uint)start, (uint)length, 0f, 0f);
            foreach (var metric in metrics)
            {
                float padX = 2f;
                float padY = 1f;
                float x = startX + metric.Left - padX;
                float y = lineY + metric.Top + padY;
                float width = Math.Min(maxWidth - metric.Left + padX, metric.Width + padX * 2);
                float height = Math.Max(10f, metric.Height - padY * 2);
                if (width <= 0) continue;

                var rect = new RoundedRectangle(new RectangleF(x, y, width, height), 3f, 3f);
                _rt.FillRoundedRectangle(rect, fill);
                _rt.DrawRoundedRectangle(rect, stroke, InvStroke(0.7f));
            }
        }
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

    private void DrawWrappedText(string text, float x, float y, float maxWidth, float maxHeight, float fontSize, WpfColor color, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || _rt is null || _dwrite is null) return;
        IDWriteTextFormat fmt = GetTextFormat(fontSize);
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, maxHeight);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
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
        foreach (var image in _imageBitmaps.Values) image.Dispose();
        _imageBitmaps.Clear();
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

