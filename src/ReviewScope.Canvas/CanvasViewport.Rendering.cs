using ReviewScope.Domain;
using System.IO;
using System.Globalization;
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
using DWriteParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment;
using D2DBezierSegment = Vortice.Direct2D1.BezierSegment;
using WpfColor = System.Windows.Media.Color;
using RectangleF = System.Drawing.RectangleF;
using Color4 = Vortice.Mathematics.Color4;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.Rendering.cs
 * Purpose: Partial class for CanvasViewport handling the main hardware-accelerated rendering loop using Direct2D.
 * Functions:
 * - RenderNativeInternal: Main entry point for drawing a single frame.
 * - EnsureRT: Ensures the Direct2D render target is valid and initialized.
 * - GetOrLoadImageBitmap: Resource manager for texture assets on the canvas.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

internal sealed record ImageBitmapResource(ID2D1Bitmap Bitmap, int PixelWidth, int PixelHeight, DateTime LastWriteUtc) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

public sealed partial class CanvasViewport
{
    private void RenderNativeInternal()
    {
        if (!EnsureRT() || _rt is null || _drawingContext is null || _blockRenderer is null) return;

        Size viewSize = new(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        EnsureVisible(viewSize);

        _rt.BeginDraw();
        _rt.Transform = Matrix3x2.Identity;
        _rt.Clear(_drawingContext.ToColor4(CanvasTheme.Surface));
        
        _backgroundRenderer?.DrawBackground(viewSize, BackgroundMode, _camera);

        {
            var tx = Matrix3x2.CreateScale((float)_camera.Zoom)
                   * Matrix3x2.CreateTranslation((float)_camera.OffsetX, (float)_camera.OffsetY);
            _rt.Transform = tx;

            // Draw swim-lanes first (behind everything)
            if (_swimLaneRenderer is not null)
            {
                foreach (var lane in _snapshot.SwimLanes)
                    _swimLaneRenderer.DrawSwimLane(lane);
            }

            // Draw connections
            if (_connectionRenderer is not null)
            {
                foreach (var conn in _visibleConnections)
                {
                    if (conn.Connection.Label == "__note") continue;
                    _connectionRenderer.DrawConnection(conn, _selectedConnectionId, _selectedConnectionControlKind);
                }

                if (_isDrawingConnection)
                    DrawInProgressConnection();
            }

            // Draft preview goes BEFORE blocks so any shape the endpoint is being dragged
            // toward (or through) renders on top of the draft line — that way the user sees
            // the line tucking behind the target instead of striping across it.
            if (_blockRenderer is not null)
                DrawShapeDraft();

            // Draw blocks
            if (_blockRenderer is not null)
            {
                _blockRenderer.BlockLookup = _snapshot.Blocks.ToDictionary(b => b.Block.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var block in _visibleBlocks)
                {
                    _blockRenderer.DrawBlock(
                        block, _editingNoteKey, _editingGroupKey, _editTitle, _editBody,
                        _editingTitle, _editCursorVisible, _editCursorPos, _editSelectionAnchor,
                        _isExtractMode, _hoverAnchorBlockKey, _hoverAnchorIndex,
                        _isDrawingConnection, _connectionSourceKey, _connectionSourceAnchorIndex,
                        _connectionHoverTargetKey, _connectionHoverTargetAnchorIndex,
                        ConnectorsEnabled, _codeScrollLines, GetOrLoadImageBitmap,
                        _connectionSourceLineId, _connectionHoverTargetLineId,
                        _connectionSourceBulletLineIndex, _connectionHoverTargetBulletLineIndex);
                }
            }

            _rt.Transform = Matrix3x2.Identity;
            _uiComponentRenderer?.DrawMarquee(_isMarquee, _marqueeStart, _marqueeEnd);
        }

        if (ShowShapeToolPalette)
            _uiComponentRenderer?.DrawShapeToolPalette(viewSize, ShapeToolIds, _activeShapeTool, _hoverShapeTool);

        DrawAutocompletePopup();

        try { _rt.EndDraw(); }
        catch { DisposeRenderTarget(); EnsureRT(); }
    }

    private void DrawShapeDraft()
    {
        if (_activeShapeTool is null || _blockRenderer is null) return;
        if (_shapeDraftCurrentWorld is null) return;

        bool isLinear = IsLinearShapeTool(_activeShapeTool);
        RenderBlock draftBlock;
        Rect bounds;

        if (isLinear && _shapeDraftPolyline is not null && _shapeDraftPolyline.Count >= 1)
        {
            // Polyline preview: confirmed vertices + current cursor as last point
            var verts = new List<WpfPoint>(_shapeDraftPolyline) { _shapeDraftCurrentWorld.Value };
            if (verts.Count < 2) return;
            draftBlock = CreateLinearShapeFromVertices(_activeShapeTool, verts, _shapeDraftAttachStartKey, _shapeDraftAttachEndKey, _shapeDraftStartOffset, _shapeDraftEndOffset, _shapeDraftCurvedFlags) with
            {
                Key = "shape::draft",
                IsSelected = true,
            };
            bounds = new Rect(draftBlock.X, draftBlock.Y, draftBlock.Width, draftBlock.Height);
        }
        else
        {
            if (_shapeDraftStartWorld is null) return;
            bool isSpline = isLinear && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (isSpline)
            {
                var start = _shapeDraftStartWorld.Value;
                var end = _shapeDraftCurrentWorld.Value;
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double mx = (start.X + end.X) / 2;
                double my = (start.Y + end.Y) / 2;
                if (len > 1)
                {
                    double px = -dy / len;
                    double py = dx / len;
                    double offset = len * 0.2;
                    mx += px * offset;
                    my += py * offset;
                }
                var verts = new List<WpfPoint> { start, new WpfPoint(mx, my), end };
                draftBlock = CreateLinearShapeFromVertices(_activeShapeTool, verts, _shapeDraftAttachStartKey, _shapeDraftAttachEndKey, _shapeDraftStartOffset, _shapeDraftEndOffset, new[] { false, true, false }) with
                {
                    Key = "shape::draft",
                    IsSelected = true,
                };
                bounds = new Rect(draftBlock.X, draftBlock.Y, draftBlock.Width, draftBlock.Height);
            }
            else
            {
                bounds = BuildShapeDraftRect(_activeShapeTool, _shapeDraftStartWorld.Value, _shapeDraftCurrentWorld.Value);
                if (bounds.Width <= 1 || bounds.Height <= 1) return;
                string body = isLinear
                    ? CanvasDrawingUtils.BuildLinearShapeBody(bounds, new[] { _shapeDraftStartWorld.Value, _shapeDraftCurrentWorld.Value }, _shapeDraftAttachStartKey, _shapeDraftAttachEndKey, _shapeDraftStartOffset, _shapeDraftEndOffset)
                    : ShapeToolTitle(_activeShapeTool);
                draftBlock = new RenderBlock(
                    Guid.Empty, "shape::draft", BlockKind.Shape,
                    ShapeToolTitle(_activeShapeTool), string.Empty,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    IsSelected: true, Body: body, ShapeType: _activeShapeTool,
                    Style: ShapeToolStyle(_activeShapeTool));
            }
        }

        var visual = new SceneBlockVisual(draftBlock, bounds);
        _blockRenderer.DrawBlock(
            visual, null, null, string.Empty, string.Empty,
            false, false, 0, -1,
            false, null, null,
            false, null, null,
            null, null,
            false, _codeScrollLines, GetOrLoadImageBitmap);
    }

    private void DrawInProgressConnection()
    {
        // Always draw to exact cursor position while dragging; snapping only happens on mouse-up.
        WpfPoint end = _connectionCurrentWorld;
        var color = _connectionHoverTargetWorld is not null
            ? WpfColor.FromArgb(220, 35, 162, 109)
            : WpfColor.FromArgb(180, 69, 132, 203);

        _connectionRenderer?.DrawConnectionPreview(
            _rewireEndpointKind == ConnectionEndpointKind.Source ? end : _connectionSourceWorld,
            _rewireEndpointKind == ConnectionEndpointKind.Source ? _connectionHoverTargetAnchorIndex : _connectionSourceAnchorIndex,
            _rewireEndpointKind == ConnectionEndpointKind.Source ? _rewireFixedWorld : end,
            _rewireEndpointKind == ConnectionEndpointKind.Source ? _rewireFixedAnchorIndex : _connectionHoverTargetAnchorIndex,
            _connectionDraftMidPoint, _connectionDraftMidPointBends, color, _connectionHoverTargetWorld is not null);
    }

    // -----------------------------------------------------------------------
    // D2D resource helpers
    // -----------------------------------------------------------------------
    private ID2D1SolidColorBrush GetBrush(WpfColor color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = _rt!.CreateSolidColorBrush(_drawingContext?.ToColor4(color) ?? new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
        _brushes[key] = b;
        return b;
    }

    private ID2D1StrokeStyle GetDashedStrokeStyle()
    {
        if (_dashedStrokeStyle is not null) return _dashedStrokeStyle;

        var props = new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 10,
            DashStyle = Vortice.Direct2D1.DashStyle.Dash,
            DashOffset = 0
        };
        _dashedStrokeStyle = _factory!.CreateStrokeStyle(props);
        return _dashedStrokeStyle;
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
        
        if (_rt is not null)
        {
            _drawingContext = new DrawingContext(
                _rt, _factory, _dwrite!, _camera,
                GetBrush,
                GetTextFormatForContext,
                GetDashedStrokeStyle());

            _blockRenderer = new BlockRenderer(_drawingContext);
            _connectionRenderer = new ConnectionRenderer(_drawingContext);
            _swimLaneRenderer = new SwimLaneRenderer(_drawingContext);
            _backgroundRenderer = new BackgroundRenderer(_drawingContext);
            _uiComponentRenderer = new UIComponentRenderer(_drawingContext);
        }

        IsReady = _rt is not null;
        return _rt is not null;
    }

    private IDWriteTextFormat GetTextFormatForContext(float size, bool sketchy) => 
        sketchy ? GetSketchyTextFormat(size) : GetTextFormat(size);

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
        _drawingContext = null;
        _blockRenderer = null;
        _connectionRenderer = null;
        _swimLaneRenderer = null;
        _backgroundRenderer = null;
        _uiComponentRenderer = null;
    }

    private IDWriteTextFormat GetRichTextFormat(string fontFamily, float size, bool bold, bool italic)
    {
        string key = $"Rich:{fontFamily}:{size:F1}:{(bold ? 'B' : 'n')}:{(italic ? 'I' : 'n')}";
        if (_textFormats.TryGetValue(key, out var cached)) return cached;
        var weight = bold ? DWriteFontWeight.Bold : DWriteFontWeight.Normal;
        var style = italic ? DWriteFontStyle.Italic : DWriteFontStyle.Normal;
        IDWriteTextFormat fmt;
        try { fmt = _dwrite!.CreateTextFormat(fontFamily, weight, style, DWriteFontStretch.Normal, size); }
        catch { fmt = _dwrite!.CreateTextFormat("Segoe UI", weight, style, DWriteFontStretch.Normal, size); }
        fmt.WordWrapping = WordWrapping.Wrap;
        _textFormats[key] = fmt;
        return fmt;
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

    private IDWriteTextFormat GetSketchyTextFormat(float size)
    {
        string key = $"Sketchy:{size:F1}";
        if (_textFormats.TryGetValue(key, out var fmt)) return fmt;
        
        string[] fonts = { "Segoe Print", "Ink Free", "Segoe Script", "Comic Sans MS" };
        foreach (var fontName in fonts)
        {
            try
            {
                fmt = _dwrite!.CreateTextFormat(fontName, DWriteFontWeight.Normal, DWriteFontStyle.Normal, DWriteFontStretch.Normal, size);
                fmt.WordWrapping = WordWrapping.NoWrap;
                _textFormats[key] = fmt;
                return fmt;
            }
            catch { }
        }
        
        return GetTextFormat(size);
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
            
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

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
}
