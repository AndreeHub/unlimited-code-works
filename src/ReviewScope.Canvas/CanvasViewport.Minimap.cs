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
    private bool TryHandleMinimapClick(Point screen)
    {
        return false;
#pragma warning disable CS0162
        if (!TryGetMinimapLayout(out Rect minimapRect)) return false;
        if (!minimapRect.Contains(screen)) return false;
        _isMinimapDrag = true;
        UpdateCameraFromMinimap(screen);
        SetCapture(_hwnd);
        return true;
#pragma warning restore CS0162
    }

    private void UpdateCameraFromMinimap(Point screen)
    {
        if (!TryGetMinimapLayout(out Rect minimapRect) || _snapshot.WorldBounds.IsEmpty) return;
        Rect wb = _snapshot.WorldBounds;
        double scaleX = MinimapW / wb.Width, scaleY = MinimapH / wb.Height;
        double scale = Math.Min(scaleX, scaleY) * 0.9;
        double offX = minimapRect.X + (MinimapW - wb.Width * scale) / 2 - wb.X * scale;
        double offY = minimapRect.Y + (MinimapH - wb.Height * scale) / 2 - wb.Y * scale;
        double worldX = (screen.X - offX) / scale;
        double worldY = (screen.Y - offY) / scale;
        _camera = _camera with
        {
            OffsetX = ActualWidth / 2 - worldX * _camera.Zoom,
            OffsetY = ActualHeight / 2 - worldY * _camera.Zoom
        };
        SetCurrentValue(CameraProperty, _camera);
        _visDirty = true;
        RenderNative();
    }

    private bool TryGetMinimapLayout(out Rect minimapRect)
    {
        if (ActualWidth <= 0) { minimapRect = Rect.Empty; return false; }
        minimapRect = new Rect(ActualWidth - MinimapW - MinimapMargin, ActualHeight - MinimapH - MinimapMargin, MinimapW, MinimapH);
        return true;
    }
}

