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
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static Point GetClientPt(IntPtr lParam)
    {
        int raw = (int)lParam.ToInt64();
        int x = (short)(raw & 0xFFFF);
        int y = (short)((raw >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    private Point GetClientPtScreen(IntPtr lParam)
    {
        int raw = (int)lParam.ToInt64();
        int xs = (short)(raw & 0xFFFF);
        int ys = (short)((raw >> 16) & 0xFFFF);
        var pt = new System.Drawing.Point(xs, ys);
        if (_hwnd != IntPtr.Zero) ScreenToClientPInvoke(_hwnd, ref pt);
        return new Point(pt.X, pt.Y);
    }

    [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
    private static extern bool ScreenToClientPInvoke(IntPtr hWnd, ref System.Drawing.Point lpPoint);

    private static int GetWheelDelta(IntPtr wParam) => (short)(((int)wParam.ToInt64() >> 16) & 0xFFFF);
}

