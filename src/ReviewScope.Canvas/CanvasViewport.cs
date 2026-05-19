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

// -----------------------------------------------------------------------
// Events raised by the viewport back to the ViewModel
// -----------------------------------------------------------------------
public sealed record BlockActivatedArgs(RenderBlock Block);
internal enum NoteResizeCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }
internal enum ConnectionControlNodeKind { None, Middle }
internal enum ConnectionEndpointKind { None, Source, Target }
public sealed record ExtractRequestedArgs(RenderBlock SourceBlock, int Line, int Column);
public sealed record FocusRequestedArgs(RenderBlock SourceBlock, int Line, int Column);
public sealed record RestoreRequestedArgs(RenderBlock Block);
public sealed record ConnectionDrawnArgs(
    string SourceKey,
    string TargetKey,
    int? SourceAnchorIndex = null,
    int? TargetAnchorIndex = null,
    double? MidControlX = null,
    double? MidControlY = null,
    bool MidControlBends = false);
public sealed record AnnotationRequestedArgs(string? AttachedBlockKey, double WorldX, double WorldY);
public sealed record SwimLaneResizedArgs(string Key, double X, double Y, double Width, double Height);
public sealed record PasteRequestedArgs(double WorldX, double WorldY);

public enum CanvasBackgroundMode { Dots, Grid }

// -----------------------------------------------------------------------
// Main viewport control
// -----------------------------------------------------------------------
public sealed partial class CanvasViewport : HwndHost, IDisposable
{
    // Win32 constants
    private const int WsChild = 0x40000000, WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000, WsClipChildren = 0x02000000;
    private const int WsTabStop = 0x00010000, SsNotify = 0x00000100;
    private const int WmPaint = 0x000F, WmSize = 0x0005, WmEraseBkgnd = 0x0014;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102, WmKillFocus = 0x0008;
    private const int WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105, WmSysChar = 0x0106;
    private const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;

    // Layout
    private const double CullPadding = 240;
    private const double MinimapW = 100, MinimapH = 70, MinimapMargin = 12;
    private const double MinBlockW = 560, MinBlockH = 180;
    private const double ResizeHandleSize = 16;
    private const double HeaderH = 68, FooterH = 30;
    private const double CodeLineH = 18, CodeGutterW = 52, CodeCharW = 6.75;
    private const int FocusedCodeTopPaddingLines = 2;
    private const double AnnotationW = 280, AnnotationH = 120;
    private const double NoteCornerHandleSize = 7;
    private const double ConnectionDragThreshold = 10;
    private const double GroupPadX = 48, GroupPadTop = 64, GroupPadBottom = 48;
    private const double CollapsedGroupW = 280, CollapsedGroupH = 118;

    // LOD zoom thresholds
    private const double UltraCompactZoom = 0.06, CompactZoom = 0.12, PreviewZoom = 0.26;

    // Dependency properties
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(RenderScene), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(RenderScene.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSceneChanged));

    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(
        nameof(Camera), typeof(CameraState), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(CameraState.Default,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnCameraChanged));

    public static readonly DependencyProperty IsReadyProperty = DependencyProperty.Register(
        nameof(IsReady), typeof(bool), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BackgroundModeProperty = DependencyProperty.Register(
        nameof(BackgroundMode), typeof(CanvasBackgroundMode), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(CanvasBackgroundMode.Dots,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => (d as CanvasViewport)?.RenderNative()));

    public static readonly DependencyProperty SnapToGridProperty = DependencyProperty.Register(
        nameof(SnapToGrid), typeof(bool), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty GridSizeProperty = DependencyProperty.Register(
        nameof(GridSize), typeof(double), typeof(CanvasViewport),
        new FrameworkPropertyMetadata(24d));

    // Routed commands / callbacks
    public ICommand? BlockActivatedCommand { get; set; }
    public ICommand? ExtractRequestedCommand { get; set; }
    public ICommand? FocusRequestedCommand { get; set; }
    public ICommand? RestoreRequestedCommand { get; set; }
    public ICommand? ConnectionDrawnCommand { get; set; }
    public ICommand? AnnotationRequestedCommand { get; set; }
    public ICommand? BlockMovedCommand { get; set; }
    public ICommand? PasteRequestedCommand { get; set; }

    // D2D resources
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, IDWriteTextFormat> _textFormats = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ID2D1PathGeometry> _connectionGeoms = new();
    private readonly Dictionary<string, ImageBitmapResource> _imageBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hwnd;
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _rt;
    private IDWriteFactory? _dwrite;
    private bool _disposed;

    // Scene
    private SceneSnapshot _snapshot = SceneSnapshot.Empty;
    private IReadOnlyList<SceneBlockVisual> _visibleBlocks = Array.Empty<SceneBlockVisual>();
    private IReadOnlyList<SceneConnectionVisual> _visibleConnections = Array.Empty<SceneConnectionVisual>();
    private CameraState _camera = CameraState.Default;
    private CameraState _lastVisCamera = CameraState.Default;
    private Size _lastVisSize = Size.Empty;
    private bool _visDirty = true;

    // Interaction state
    private Point? _panPoint;
    private Point? _dragWorldPoint;
    private Point? _dragStartScreen;
    private string? _primaryDrag;
    private List<string> _draggedKeys = new();
    private string? _resizeKey;
    private Point? _resizeWorldPoint;
    private bool _resizeWidthOnly;
    private string? _resizeSwimLaneKey;
    private Point? _resizeSwimLaneWorldPoint;
    private Point? _marqueeStart, _marqueeEnd;
    private bool _isMarquee, _appendMarquee, _didMove, _isMinimapDrag;

    // Draw-connection state
    private bool _isDrawingConnection;
    private string? _connectionSourceKey;
    private int? _connectionSourceAnchorIndex;
    private Point _connectionSourceWorld;
    private Point _connectionCurrentWorld;
    private string? _connectionHoverTargetKey;
    private int? _connectionHoverTargetAnchorIndex;
    private Point? _connectionHoverTargetWorld;
    private Point? _connectionDraftMidPoint;
    private bool _connectionDraftMidPointBends;
    private Guid? _dragArrowConnectionId;
    private Guid? _dragConnectionControlId;
    private ConnectionControlNodeKind _dragConnectionControlKind = ConnectionControlNodeKind.None;
    private Guid? _rewireConnectionId;
    private ConnectionEndpointKind _rewireEndpointKind = ConnectionEndpointKind.None;
    private Point _rewireFixedWorld;
    private int? _rewireFixedAnchorIndex;
    private Guid? _selectedConnectionId;
    private ConnectionControlNodeKind _selectedConnectionControlKind = ConnectionControlNodeKind.None;
    private string? _hoverAnchorBlockKey;
    private int? _hoverAnchorIndex;

    // Modifier highlight state (Ctrl=focus, Alt=extract)
    private bool _isFocusMode;            // Ctrl held - collapse to function
    private bool _isExtractMode;          // Alt held - extract function to new block
    private SceneBlockVisual? _extractHoverBlock;
    private int _extractHoverLine;
    private int _extractHoverStartLine, _extractHoverEndLine;

    // Click tracking
    private long _lastClickTick = -1;
    private string? _lastClickKey;
    private Point _lastClickScreen = new(double.NaN, double.NaN);
    private readonly Dictionary<string, int> _codeScrollLines = new(StringComparer.OrdinalIgnoreCase);
    private Point _lastMouseScreenPoint;
    private string? _noteResizeKey;
    private NoteResizeCorner _noteResizeCorner = NoteResizeCorner.None;
    private Point? _noteResizeWorldPoint;

    // In-canvas note editing
    private string? _editingNoteKey;
    private string? _editingGroupKey;
    private string _editTitle = string.Empty;
    private string _editBody = string.Empty;
    private bool _editingTitle = true;
    private int _editCursorPos;
    private int _editSelectionAnchor = -1; // -1 = no selection, else anchor position in current field
    private bool _editMouseSelecting;
    private bool _editCursorVisible = true;
    private System.Threading.Timer? _cursorBlinkTimer;

    public CanvasViewport()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        SetCurrentValue(CameraProperty, _camera);
    }

    public RenderScene Scene
    {
        get => (RenderScene)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public CameraState Camera
    {
        get => (CameraState)GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public bool IsReady
    {
        get => (bool)GetValue(IsReadyProperty);
        set => SetValue(IsReadyProperty, value);
    }

    public CanvasBackgroundMode BackgroundMode
    {
        get => (CanvasBackgroundMode)GetValue(BackgroundModeProperty);
        set => SetValue(BackgroundModeProperty, value);
    }

    public bool SnapToGrid
    {
        get => (bool)GetValue(SnapToGridProperty);
        set => SetValue(SnapToGridProperty, value);
    }

    public double GridSize
    {
        get => (double)GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    public void ToggleBackground()
    {
        BackgroundMode = BackgroundMode == CanvasBackgroundMode.Dots
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
    }

    public void BeginEditNewNote()
    {
        var note = Scene.Blocks.LastOrDefault(b => b.Kind == BlockKind.Note);
        if (note is not null) BeginNoteEdit(note);
    }

    public void FrameAll()
    {
        var nodes = _snapshot.Blocks;
        if (nodes.Count == 0 || ActualWidth <= 0) return;
        var bounds = nodes[0].Bounds;
        foreach (var n in nodes.Skip(1)) bounds.Union(n.Bounds);
        const double pad = 72;
        double zx = (ActualWidth - pad * 2) / Math.Max(1, bounds.Width);
        double zy = (ActualHeight - pad * 2) / Math.Max(1, bounds.Height);
        double z = Math.Clamp(Math.Min(zx, zy), 0.02, 4.0);
        _camera = new CameraState(z,
            ActualWidth / 2 - (bounds.X + bounds.Width / 2) * z,
            ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * z);
        SetCurrentValue(CameraProperty, _camera);
        _visDirty = true;
        RenderNative();
    }

    public void FrameSelection()
    {
        var selected = _snapshot.Blocks.Where(b => b.Block.IsSelected).ToList();
        if (selected.Count == 0)
        {
            if (_snapshot.Connections.FirstOrDefault(c => c.Connection.IsSelected) is { } connection)
                FrameWorldBounds(connection.Bounds);
            return;
        }

        Rect bounds = selected[0].Bounds;
        foreach (var block in selected.Skip(1))
            bounds.Union(block.Bounds);
        FrameWorldBounds(bounds);
    }

    private void FrameWorldBounds(Rect bounds)
    {
        if (bounds.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0) return;
        const double pad = 160;
        double zx = (ActualWidth - pad * 2) / Math.Max(1, bounds.Width);
        double zy = (ActualHeight - pad * 2) / Math.Max(1, bounds.Height);
        double z = Math.Clamp(Math.Min(zx, zy), 0.08, 3.0);
        _camera = new CameraState(z,
            ActualWidth / 2 - (bounds.X + bounds.Width / 2) * z,
            ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * z);
        SetCurrentValue(CameraProperty, _camera);
        _visDirty = true;
        RenderNative();
    }

    public Point GetLastMouseWorldPoint()
    {
        if (_lastMouseScreenPoint.X == 0 && _lastMouseScreenPoint.Y == 0 && ActualWidth > 0 && ActualHeight > 0)
            return ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));
        return ToWorld(_lastMouseScreenPoint);
    }

    public Point ScreenPointToWorld(Point screen) => ToWorld(screen);

    // -----------------------------------------------------------------------
    // HwndHost overrides
    // -----------------------------------------------------------------------
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings | WsTabStop | SsNotify,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        RebuildSnapshot();
        EnsureRT();
        RenderNative();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DisposeRenderTarget();
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEraseBkgnd: handled = true; return new IntPtr(1);
            case WmSize: ResizeRT(); RenderNative(); break;
            case WmPaint: RenderNative(); break;
            case WmLButtonDown: HandleLDown(GetClientPt(lParam)); handled = true; break;
            case WmLButtonUp: HandleLUp(GetClientPt(lParam)); handled = true; break;
            case WmRButtonDown: HandleRDown(GetClientPt(lParam)); handled = true; break;
            case WmRButtonUp: HandleRUp(GetClientPt(lParam)); handled = true; break;
            case WmMButtonDown: _panPoint = GetClientPt(lParam); Cursor = Cursors.Hand; SetCapture(_hwnd); break;
            case WmMButtonUp: ResetInteraction(); UpdateHoverCursor(GetClientPt(lParam)); ReleaseCapture(); break;
            case WmMouseMove: HandleMove(GetClientPt(lParam)); handled = true; break;
            case WmMouseWheel: HandleWheel(GetClientPtScreen(lParam), GetWheelDelta(wParam)); handled = true; break;
            case WmChar: if (_editingNoteKey is not null || _editingGroupKey is not null) { HandleChar((char)wParam.ToInt32()); handled = true; } break;
            case WmKeyDown: HandleKeyDown(wParam); if (_editingNoteKey is not null || _editingGroupKey is not null) handled = true; break;
            case WmKeyUp: HandleKeyUp(wParam); break;
            case WmSysKeyDown: HandleKeyDown(wParam); handled = true; break;
            case WmSysKeyUp: HandleKeyUp(wParam); handled = true; break;
            case WmSysChar: handled = true; break;
            case WmKillFocus:
                _isFocusMode = false;
                _isExtractMode = false;
                if (_editingNoteKey is not null) CommitNoteEdit(save: true);
                else if (_editingGroupKey is not null) CommitGroupTitleEdit(save: true);
                else RenderNative();
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        ResizeRT();
        RenderNative();
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeRenderTarget();
        _dwrite?.Dispose(); _dwrite = null;
        _factory?.Dispose(); _factory = null;
        GC.SuppressFinalize(this);
    }

    // -----------------------------------------------------------------------
    // Input handlers

    // -----------------------------------------------------------------------
    // Dependency property callbacks
    // -----------------------------------------------------------------------
    private static void OnSceneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CanvasViewport v) { v.RebuildSnapshot(); v._visDirty = true; v.RenderNative(); }
    }

    private static void OnCameraChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CanvasViewport v && e.NewValue is CameraState cam) { v._camera = cam; v._visDirty = true; v.RenderNative(); }
    }
}
