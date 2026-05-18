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
    private bool IsDoubleClick(string key, Point screen)
    {
        long now = Environment.TickCount64;
        bool sameKey = string.Equals(_lastClickKey, key, StringComparison.OrdinalIgnoreCase);
        bool sameTime = _lastClickTick >= 0 && (now - _lastClickTick) <= GetDoubleClickTime();
        bool samePlace = !double.IsNaN(_lastClickScreen.X)
            && Math.Abs(screen.X - _lastClickScreen.X) <= 5
            && Math.Abs(screen.Y - _lastClickScreen.Y) <= 5;
        TrackClick(key, screen);
        return sameKey && sameTime && samePlace;
    }

    private void TrackClick(string key, Point screen)
    {
        _lastClickKey = key;
        _lastClickScreen = screen;
        _lastClickTick = Environment.TickCount64;
    }

    // -----------------------------------------------------------------------
    // Interaction helpers
    // -----------------------------------------------------------------------
    private void ResetInteraction()
    {
        _panPoint = null;
        _dragWorldPoint = null;
        _dragStartScreen = null;
        _primaryDrag = null;
        _draggedKeys = new();
        _resizeKey = null;
        _resizeWorldPoint = null;
        _resizeWidthOnly = false;
        _resizeSwimLaneKey = null;
        _resizeSwimLaneWorldPoint = null;
        _dragArrowConnectionId = null;
        _dragConnectionControlId = null;
        _dragConnectionControlKind = ConnectionControlNodeKind.None;
        _marqueeStart = null;
        _marqueeEnd = null;
        _isMarquee = false;
        _appendMarquee = false;
        _didMove = false;
        _isMinimapDrag = false;
    }

    private void UpdateHoverCursor(Point screen)
    {
        Point world = ToWorld(screen);
        var anchor = HitConnectionAnchor(world);
        string? oldHoverKey = _hoverAnchorBlockKey;
        int? oldHoverIndex = _hoverAnchorIndex;
        _hoverAnchorBlockKey = anchor?.Block.Block.Key;
        _hoverAnchorIndex = anchor?.AnchorIndex;
        bool hoverChanged = oldHoverKey != _hoverAnchorBlockKey || oldHoverIndex != _hoverAnchorIndex;
        if (anchor is not null) { Cursor = Cursors.Cross; if (hoverChanged) RenderNative(); return; }
        if (hoverChanged) RenderNative();
        if (HitConnectionControlNode(world) is not null) { Cursor = Cursors.SizeAll; return; }
        if (HitConnectionArrow(world) is not null) { Cursor = Cursors.Hand; return; }
        if (HitConnectionCurve(world, out _) is not null) { Cursor = Cursors.Cross; return; }
        var hit = HitBlock(world);
        if (hit is not null && hit.Block.Focused is not null && IsInRestoreButton(hit.Bounds, world))
        { Cursor = Cursors.Hand; return; }
        if (hit is not null && hit.Block.Kind is BlockKind.File or BlockKind.Extract && IsInResize(hit.Bounds, world))
        { Cursor = IsInRightEdgeResize(hit.Bounds, world) ? Cursors.SizeWE : Cursors.SizeNWSE; return; }
        if (HitSwimLaneResize(world) is not null) { Cursor = Cursors.SizeNWSE; return; }
        Cursor = Cursors.Arrow;
    }
}

