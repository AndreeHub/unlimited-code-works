using ReviewScope.Domain;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using WpfColor = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.cs
 * Purpose: The main WPF host control for the high-performance Direct2D canvas.
 * Functions:
 * - Direct2D/DirectWrite resource lifecycle management.
 * - Integration of modular renderers (Block, Connection, etc.) and tools.
 * - Viewport coordination (Zoom, Pan, Frame All).
 * - Win32 window hosting for low-latency rendering.
 * Please read the first 15 lines of this file for a summary before reading the entire file to save tokens.
 */

public sealed record RestoreRequestedArgs(RenderBlock Block);
public sealed record ExtractRequestedArgs(RenderBlock SourceBlock, int Line, int Column);
public sealed record FocusRequestedArgs(RenderBlock SourceBlock, int Line, int Column);
public sealed record BlockActivatedArgs(RenderBlock Block);
public sealed record PasteRequestedArgs(double WorldX, double WorldY);
public sealed record ConnectionDrawnArgs(string SourceKey, string TargetKey, int? SourceAnchorIndex, int? TargetAnchorIndex, double? MidControlX, double? MidControlY, bool MidControlBends);
public sealed record AnnotationRequestedArgs(double WorldX, double WorldY, string? AttachedBlockKey = null);
public sealed record ItemPlacementArgs(string Kind, double WorldX, double WorldY);
public sealed record CanvasSceneChangedArgs(RenderScene Before, RenderScene After, bool IsContentChange);

public sealed partial class CanvasViewport : HwndHost
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    internal const double HeaderH = 56;
    internal const double FooterH = 28;
    internal const double CodeGutterW = 54;
    internal const double CodeTextPadX = 12;
    internal const double CodeScrollbarReserveW = 34;
    internal const double CodeLineH = 18;
    internal const double CodeCharW = 7.2;
    internal const int FocusedCodeTopPaddingLines = 2;
    
    internal const double MinimapW = 180;
    internal const double MinimapH = 135;
    internal const double MinimapMargin = 16;
    
    internal const float ShapeToolPaletteMargin = 16;
    internal const float ShapeToolPalettePadding = 12;
    internal const float ShapeToolButtonSize = 32;
    internal const float ShapeToolButtonGap = 6;

    private const double MinBlockW = 120;
    private const double MinBlockH = 40;
    private const double GroupPadX = 32;
    private const double GroupPadTop = 52;
    private const double GroupPadBottom = 24;
    private const double CollapsedGroupW = 220;
    private const double CollapsedGroupH = 80;
    private const double CullPadding = 200;
    private const double ConnectionDragThreshold = 5.0;
    private const double ResizeHandleSize = 22;

    public static readonly DependencyProperty SceneProperty =
        DependencyProperty.Register(nameof(Scene), typeof(RenderScene), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(new RenderScene(new List<RenderBlock>(), new List<RenderConnection>(), new List<RenderSwimLane>(), new List<RenderAnnotation>()), OnSceneChanged));

    public static readonly DependencyProperty CameraProperty =
        DependencyProperty.Register(nameof(Camera), typeof(CameraState), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(CameraState.Default,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnCameraChanged));

    public static readonly DependencyProperty ConnectorsEnabledProperty =
        DependencyProperty.Register(nameof(ConnectorsEnabled), typeof(bool), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundModeProperty =
        DependencyProperty.Register(nameof(BackgroundMode), typeof(CanvasBackgroundMode), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(CanvasBackgroundMode.Dots, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapToGridProperty =
        DependencyProperty.Register(nameof(SnapToGrid), typeof(bool), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty GridSizeProperty =
        DependencyProperty.Register(nameof(GridSize), typeof(double), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(24.0));

    public static readonly DependencyProperty ActiveShapeToolProperty =
        DependencyProperty.Register(nameof(ActiveShapeTool), typeof(string), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnActiveShapeToolChanged));

    public static readonly DependencyProperty ShowShapeToolPaletteProperty =
        DependencyProperty.Register(nameof(ShowShapeToolPalette), typeof(bool), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PendingItemPlacementProperty =
        DependencyProperty.Register(nameof(PendingItemPlacement), typeof(string), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPendingItemPlacementChanged));

    public static readonly DependencyProperty ItemPlacementRequestedCommandProperty =
        DependencyProperty.Register(nameof(ItemPlacementRequestedCommand), typeof(ICommand), typeof(CanvasViewport),
            new FrameworkPropertyMetadata(null));

    public RenderScene Scene { get => (RenderScene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public CameraState Camera { get => (CameraState)GetValue(CameraProperty); set => SetValue(CameraProperty, value); }
    public bool ConnectorsEnabled { get => (bool)GetValue(ConnectorsEnabledProperty); set => SetValue(ConnectorsEnabledProperty, value); }
    public CanvasBackgroundMode BackgroundMode { get => (CanvasBackgroundMode)GetValue(BackgroundModeProperty); set => SetValue(BackgroundModeProperty, value); }
    public bool SnapToGrid { get => (bool)GetValue(SnapToGridProperty); set => SetValue(SnapToGridProperty, value); }
    public double GridSize { get => (double)GetValue(GridSizeProperty); set => SetValue(GridSizeProperty, value); }
    public string? ActiveShapeTool { get => (string?)GetValue(ActiveShapeToolProperty); set => SetValue(ActiveShapeToolProperty, value); }
    public bool ShowShapeToolPalette { get => (bool)GetValue(ShowShapeToolPaletteProperty); set => SetValue(ShowShapeToolPaletteProperty, value); }
    public string? PendingItemPlacement { get => (string?)GetValue(PendingItemPlacementProperty); set => SetValue(PendingItemPlacementProperty, value); }
    public ICommand? ItemPlacementRequestedCommand { get => (ICommand?)GetValue(ItemPlacementRequestedCommandProperty); set => SetValue(ItemPlacementRequestedCommandProperty, value); }

    private static void OnPendingItemPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CanvasViewport vp)
            vp.Cursor = string.IsNullOrEmpty(e.NewValue as string) ? Cursors.Arrow : Cursors.Cross;
    }

    internal void SyncActiveShapeToolDp()
    {
        var current = (string?)GetValue(ActiveShapeToolProperty);
        if (!string.Equals(current, _activeShapeTool, StringComparison.OrdinalIgnoreCase))
            SetValue(ActiveShapeToolProperty, _activeShapeTool);
    }

    private static void OnActiveShapeToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CanvasViewport vp) return;
        var requested = e.NewValue as string;
        if (string.Equals(vp._activeShapeTool, requested, StringComparison.OrdinalIgnoreCase)) return;
        vp._activeShapeTool = string.IsNullOrEmpty(requested) ? null : requested;
        vp._shapeDraftStartWorld = null;
        vp._shapeDraftCurrentWorld = null;
        vp._shapeDraftPolyline = null;
        vp._shapeDraftAttachStartKey = null;
        vp._shapeDraftAttachEndKey = null;
        vp._shapeDraftStartOffset = null;
        vp._shapeDraftEndOffset = null;
        vp._shapeDraftCurvedFlags = null;
        vp.Cursor = vp._activeShapeTool is null ? Cursors.Arrow : Cursors.Cross;
        vp.RenderNative();
    }

    public bool IsReady { get; internal set; }

    public ICommand? RestoreRequestedCommand { get; set; }
    public ICommand? ExtractRequestedCommand { get; set; }
    public ICommand? FocusRequestedCommand { get; set; }
    public ICommand? BlockMovedCommand { get; set; }
    public ICommand? BlockActivatedCommand { get; set; }
    public ICommand? CopyRequestedCommand { get; set; }
    public ICommand? PasteRequestedCommand { get; set; }
    public ICommand? ConnectionDrawnCommand { get; set; }
    public ICommand? AnnotationRequestedCommand { get; set; }

    internal static readonly string[] ShapeToolIds = { "rectangle", "square", "oval", "circle", "triangle", "diamond", "hexagon", "star", "line", "arrow", "polyline" };
    private static readonly string[] ShapeShortcutToolIds = { "rectangle", "square", "oval", "circle", "triangle", "diamond", "hexagon", "star", "polyline" };

    // Logic-only snapshot of the scene, rebuilt on changes
    internal SceneSnapshot _snapshot = SceneSnapshot.Empty;
    internal bool _visDirty = true;
    internal CameraState _lastVisCamera = CameraState.Default;
    internal Size _lastVisSize = Size.Empty;

    // View-ready lists for the current frame
    internal IReadOnlyList<SceneBlockVisual> _visibleBlocks = Array.Empty<SceneBlockVisual>();
    internal IReadOnlyList<SceneConnectionVisual> _visibleConnections = Array.Empty<SceneConnectionVisual>();

    // D2D Resources
    internal IntPtr _hwnd;
    internal ID2D1Factory? _factory;
    internal ID2D1HwndRenderTarget? _rt;
    internal IDWriteFactory? _dwrite;
    internal bool _disposed;
    internal bool _isCoalescingSceneChanges;
    internal RenderScene? _coalescedSceneBefore;

    // Decomposed renderers
    internal DrawingContext? _drawingContext;
    internal BlockRenderer? _blockRenderer;
    internal ConnectionRenderer? _connectionRenderer;
    internal SwimLaneRenderer? _swimLaneRenderer;
    internal BackgroundRenderer? _backgroundRenderer;
    internal UIComponentRenderer? _uiComponentRenderer;

    // Resource Caches
    internal readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    internal readonly Dictionary<string, IDWriteTextFormat> _textFormats = new();
    internal readonly Dictionary<Guid, ID2D1PathGeometry> _connectionGeoms = new();
    internal readonly Dictionary<string, ImageBitmapResource> _imageBitmaps = new();
    internal ID2D1StrokeStyle? _dashedStrokeStyle;

    // Interaction state
    internal WpfPoint? _panPoint;
    internal WpfPoint? _dragWorldPoint;
    internal WpfPoint? _dragAnchorOffset; // cursor-world minus primary-drag block top-left at drag start
    internal WpfPoint? _dragStartScreen;
    internal string? _primaryDrag;
    internal List<string> _draggedKeys = new();
    internal string? _resizeKey;
    internal WpfPoint? _resizeWorldPoint;
    internal bool _resizeWidthOnly;
    internal string? _linearShapeVertexDragKey;
    internal int _linearShapeVertexDragIndex = -1;
    internal string? _resizeSwimLaneKey;
    internal WpfPoint? _resizeSwimLaneWorldPoint;
    internal WpfPoint? _marqueeStart, _marqueeEnd;
    internal bool _isMarquee, _appendMarquee, _didMove, _isMinimapDrag;
    internal bool _clickedAlreadySelectedTextEditable;
    internal string? _activeShapeTool;
    internal WpfPoint? _shapeDraftStartWorld;
    internal WpfPoint? _shapeDraftCurrentWorld;
    internal List<WpfPoint>? _shapeDraftPolyline; // confirmed vertices in click-mode (≥1 = in polyline mode)
    internal string? _shapeDraftAttachStartKey;
    internal string? _shapeDraftAttachEndKey; // updated live as cursor hovers blocks during draft
    internal WpfPoint? _shapeDraftStartOffset;
    internal WpfPoint? _shapeDraftEndOffset;
    internal List<bool>? _shapeDraftCurvedFlags;


    // Draw-connection state
    internal bool _isDrawingConnection;
    internal string? _connectionSourceKey;
    internal int? _connectionSourceAnchorIndex;
    internal WpfPoint _connectionSourceWorld;
    internal WpfPoint _connectionCurrentWorld;
    internal string? _connectionHoverTargetKey;
    internal int? _connectionHoverTargetAnchorIndex;
    internal WpfPoint? _connectionHoverTargetWorld;
    internal WpfPoint? _connectionDraftMidPoint;
    internal bool _connectionDraftMidPointBends;
    internal Guid? _dragArrowConnectionId;
    internal Guid? _dragConnectionControlId;
    internal ConnectionControlNodeKind _dragConnectionControlKind = ConnectionControlNodeKind.None;
    internal Guid? _rewireConnectionId;
    internal ConnectionEndpointKind _rewireEndpointKind = ConnectionEndpointKind.None;
    internal WpfPoint _rewireFixedWorld;
    internal int? _rewireFixedAnchorIndex;
    internal Guid? _selectedConnectionId;
    internal ConnectionControlNodeKind _selectedConnectionControlKind = ConnectionControlNodeKind.None;
    internal string? _hoverAnchorBlockKey;
    internal int? _hoverAnchorIndex;

    // Modifier highlight state (Alt=extract function to new block)
    internal bool _isExtractMode;

    // Click tracking
    internal long _lastClickTick = -1;
    internal string? _lastClickKey;
    internal WpfPoint _lastClickScreen = new(double.NaN, double.NaN);
    internal readonly Dictionary<string, int> _codeScrollLines = new(StringComparer.OrdinalIgnoreCase);
    internal WpfPoint _lastMouseScreenPoint;
    internal string? _hoverShapeTool;
    internal string? _noteResizeKey;
    internal NoteResizeCorner _noteResizeCorner = NoteResizeCorner.None;
    internal WpfPoint? _noteResizeWorldPoint;

    // In-canvas note editing
    internal string? _editingNoteKey;
    internal string? _editingGroupKey;
    internal string _editTitle = string.Empty;
    internal string _editBody = string.Empty;
    internal bool _editingTitle = true;
    internal int _editCursorPos;
    internal int _editSelectionAnchor = -1;
    internal bool _editMouseSelecting;
    internal bool _editCursorVisible = true;
    internal System.Threading.Timer? _cursorBlinkTimer;

    // Tool architecture
    private readonly Dictionary<string, ICanvasTool> _tools = new();
    private ICanvasTool? _currentTool;

    public CanvasViewport()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        SetCurrentValue(CameraProperty, _camera);

        // Initialize tools
        _tools["Selection"] = new SelectionTool(this);
        _tools["Connection"] = new ConnectionTool(this);
        _tools["Shape"] = new ShapeTool(this);
        _tools["Pan"] = new PanTool(this);
        _tools["Marquee"] = new MarqueeTool(this);
        _currentTool = _tools["Selection"];
        
        _hwndMap[IntPtr.Zero] = new WeakReference<CanvasViewport>(this);
    }
    
    internal void SetTool(string name)
    {
        if (_tools.TryGetValue(name, out var tool))
        {
            _currentTool?.Deactivate();
            _currentTool = tool;
        }
    }

    public bool ActivateBottomToolShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None)
            return false;

        if (TryGetShapeShortcut(key, out string? shapeTool))
        {
            ActivateShapeTool(shapeTool);
            return true;
        }

        switch (key)
        {
            case Key.Q:
                ActivateShapeTool("line");
                return true;
            case Key.E:
                ActivateShapeTool("arrow");
                return true;
            case Key.T:
                ActivatePendingItemPlacement("text");
                return true;
            case Key.W:
                ActivatePendingItemPlacement("note");
                return true;
            case Key.V:
                ActivateSelectionTool();
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetShapeShortcut(Key key, out string shapeTool)
    {
        int index = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1
        };

        if (index >= 0 && index < ShapeShortcutToolIds.Length)
        {
            shapeTool = ShapeShortcutToolIds[index];
            return true;
        }

        shapeTool = string.Empty;
        return false;
    }

    private void ActivateShapeTool(string shapeTool)
    {
        PendingItemPlacement = null;
        ClearConnectionDrawingState();
        ClearShapeDraftState();
        _activeShapeTool = shapeTool;
        SyncActiveShapeToolDp();
        SetTool("Shape");
        Cursor = Cursors.Cross;
        RenderNative();
    }

    private void ActivatePendingItemPlacement(string kind)
    {
        ClearConnectionDrawingState();
        ClearShapeDraftState();
        _activeShapeTool = null;
        SyncActiveShapeToolDp();
        PendingItemPlacement = kind;
        SetTool("Selection");
        Cursor = Cursors.Cross;
        RenderNative();
    }

    private void ActivateSelectionTool()
    {
        ClearConnectionDrawingState();
        ClearShapeDraftState();
        _activeShapeTool = null;
        SyncActiveShapeToolDp();
        PendingItemPlacement = null;
        SetTool("Selection");
        UpdateHoverCursor(_lastMouseScreenPoint);
        RenderNative();
    }

    private void ClearShapeDraftState()
    {
        _shapeDraftStartWorld = null;
        _shapeDraftCurrentWorld = null;
        _shapeDraftPolyline = null;
        _shapeDraftAttachStartKey = null;
        _shapeDraftAttachEndKey = null;
        _shapeDraftStartOffset = null;
        _shapeDraftEndOffset = null;
        _shapeDraftCurvedFlags = null;
    }

    internal void ResetInteraction()
    {
        _panPoint = null;
        _dragWorldPoint = null;
        _dragAnchorOffset = null;
        _primaryDrag = null;
        _didMove = false;
        _isMarquee = false;
        _isMinimapDrag = false;
        _noteResizeKey = null;
        _resizeKey = null;
        _resizeWidthOnly = false;
        _linearShapeVertexDragKey = null;
        _linearShapeVertexDragIndex = -1;
        _resizeSwimLaneKey = null;
        _dragArrowConnectionId = null;
        _dragConnectionControlId = null;
        _dragConnectionControlKind = ConnectionControlNodeKind.None;
    }

    public void FrameAll()
    {
        var nodes = _snapshot.Blocks;
        if (nodes.Count == 0 || ActualWidth <= 0) return;
        var bounds = nodes[0].Bounds;
        foreach (var n in nodes.Skip(1)) bounds.Union(n.Bounds);
        bounds.Inflate(40, 40);

        double zx = ActualWidth / bounds.Width;
        double zy = ActualHeight / bounds.Height;
        double z = Math.Clamp(Math.Min(zx, zy), 0.02, 4.0);
        _camera = new CameraState(z,
            ActualWidth / 2 - (bounds.X + bounds.Width / 2) * z,
            ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * z);
        SetCurrentValue(CameraProperty, _camera);
    }

    public void FrameSelection()
    {
        var selected = Scene.Blocks.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) return;
        var bounds = new Rect(selected[0].X, selected[0].Y, selected[0].Width, selected[0].Height);
        foreach (var b in selected.Skip(1)) bounds.Union(new Rect(b.X, b.Y, b.Width, b.Height));
        bounds.Inflate(60, 60);

        double zx = ActualWidth / bounds.Width;
        double zy = ActualHeight / bounds.Height;
        double z = Math.Clamp(Math.Min(zx, zy), 0.02, 4.0);
        _camera = new CameraState(z,
            ActualWidth / 2 - (bounds.X + bounds.Width / 2) * z,
            ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * z);
        SetCurrentValue(CameraProperty, _camera);
    }

    internal void ToggleBackground()
    {
        BackgroundMode = BackgroundMode == CanvasBackgroundMode.Dots
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
    }

    internal bool IsDoubleClick(string key, WpfPoint screen)
    {
        long now = DateTime.UtcNow.Ticks / 10000;
        bool match = _lastClickKey == key && (now - _lastClickTick) < 500 && Math.Abs(screen.X - _lastClickScreen.X) < 5 && Math.Abs(screen.Y - _lastClickScreen.Y) < 5;
        _lastClickKey = key; _lastClickTick = now; _lastClickScreen = screen;
        return match;
    }

    internal void TrackClick(string key, WpfPoint screen)
    {
        _lastClickKey = key;
        _lastClickTick = DateTime.UtcNow.Ticks / 10000;
        _lastClickScreen = screen;
    }

    internal int GetMaxCodeScrollLines(RenderBlock block, Rect bodyRect)
    {
        if (block.Body is null) return 0;
        string[] allLines = block.Body.Replace("\r", "").Split('\n');
        int total = allLines.Length;
        int topPadding = block.Focused is not null ? FocusedCodeTopPaddingLines : 0;
        int visible = Math.Max(0, (int)Math.Floor(bodyRect.Height / CodeLineH) - topPadding);
        return Math.Max(0, total - visible);
    }

    public WpfPoint GetLastMouseWorldPoint() => ToWorld(_lastMouseScreenPoint);
    public Point ScreenPointToWorld(Point screen) => ToWorld(screen);

    internal bool TryCompleteConnectionToAnchor(ConnectionAnchorHit anchor)
    {
        if (anchor.Block.Block.Key.Equals(_connectionSourceKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (_rewireConnectionId is Guid id) { CompleteConnectionRewire(anchor); return true; }
        if (_connectionSourceKey is null || ConnectionDrawnCommand?.CanExecute(null) != true) return false;
        ConnectionDrawnCommand.Execute(new ConnectionDrawnArgs(_connectionSourceKey, anchor.Block.Block.Key, _connectionSourceAnchorIndex, anchor.AnchorIndex, _connectionDraftMidPoint?.X, _connectionDraftMidPoint?.Y, _connectionDraftMidPointBends));
        return true;
    }

    internal float InvStroke(float strokeWidth) => (float)(strokeWidth / _camera.Zoom);

    internal void RenderNative() => RenderNativeInternal();
    internal void RebuildSnapshot() => RebuildSnapshotInternal();
    internal void ApplySceneChange(RenderScene scene) => ApplySceneChangeInternal(scene);

    internal int WorldToCodeLine(SceneBlockVisual block, Point world)
    {
        Rect bodyRect = CanvasDrawingUtils.GetBodyRect(block.Bounds);
        double topPadding = block.Block.Focused is not null ? FocusedCodeTopPaddingLines * CodeLineH : 0;
        double relY = world.Y - bodyRect.Y - topPadding;
        int lineIndex = Math.Max(0, (int)(relY / CodeLineH));
        int startLine = block.Block.Focused?.StartLine ?? block.Block.StartLine ?? 1;
        _codeScrollLines.TryGetValue(block.Block.Key, out int scrollLines);
        return startLine + scrollLines + lineIndex;
    }

    internal static RenderScene ClearSelection(RenderScene scene) =>
        scene with { Blocks = scene.Blocks.Select(b => b with { IsSelected = false }).ToList(), SwimLanes = scene.SwimLanes.Select(l => l with { IsSelected = false }).ToList() };

    internal static RenderScene ToggleSelection(RenderScene scene, string key) =>
        scene with { Blocks = scene.Blocks.Select(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? b with { IsSelected = !b.IsSelected } : b).ToList() };

    internal static RenderScene SetSelection(RenderScene scene, IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return scene with { Blocks = scene.Blocks.Select(b => b with { IsSelected = set.Contains(b.Key) }).ToList() };
    }

    internal static RenderScene SelectSwimLane(RenderScene scene, string key) =>
        scene with { Blocks = scene.Blocks.Select(b => b with { IsSelected = false }).ToList(), SwimLanes = scene.SwimLanes.Select(l => l with { IsSelected = l.Key.Equals(key, StringComparison.OrdinalIgnoreCase) }).ToList() };

    internal void CompleteMarqueeSelection(WpfPoint start, WpfPoint end, bool append)
    {
        Rect screenRect = new(start, end);
        if (screenRect.Width < 4 && screenRect.Height < 4) return;
        Point topLeft = ToWorld(new Point(screenRect.Left, screenRect.Top));
        Point bottomRight = ToWorld(new Point(screenRect.Right, screenRect.Bottom));
        Rect worldRect = new(topLeft, bottomRight);
        var hit = _snapshot.QueryBlocks(worldRect).Select(v => v.Block.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocks = Scene.Blocks.Select(b => b with { IsSelected = append ? b.IsSelected || hit.Contains(b.Key) : hit.Contains(b.Key) }).ToList();
        ApplySceneChange(ClearConnectionSelection(Scene with { Blocks = blocks }));
        RebuildSnapshot();
    }

    internal void CompleteMarquee()
    {
        if (_marqueeStart is WpfPoint s && _marqueeEnd is WpfPoint e)
            CompleteMarqueeSelection(s, e, _appendMarquee);
    }

    public void DeleteSelection() => DeleteSelected();

    /// <summary>
    /// True when a Text/Note/Group title is currently being edited in-canvas. The host
    /// window uses this to suppress single-letter tool shortcuts (T, W, V, B, ...) so
    /// they get typed into the card instead of activating tools.
    /// </summary>
    public bool IsEditingInCanvas => _editingNoteKey is not null || _editingGroupKey is not null;

    /// <summary>
    /// Raised when an in-canvas edit begins on a Text or Note block. The host window
    /// uses this to flip the right-side inspector to the most useful tab for the
    /// block kind (Text → Text tab; Note → Inspector tab).
    /// </summary>
    public event Action<BlockKind>? EditStarted;

    public void BeginEditNewNote()
    {
        var note = Scene.Blocks.LastOrDefault(b => b.Kind == BlockKind.Note);
        if (note is not null) BeginNoteEdit(note);
    }

    public void BeginEditLastBlockOfKind(BlockKind kind)
    {
        var block = Scene.Blocks.LastOrDefault(b => b.Kind == kind);
        if (block is null) return;
        Focus();
        SetFocus(_hwnd);
        BeginNoteEdit(block);
    }

    private CameraState _camera = CameraState.Default;

    // -----------------------------------------------------------------------
    // Core HWND / HwndHost
    // -----------------------------------------------------------------------
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        string cls = "ReviewScopeCanvas";
        NativeMethods.WNDCLASSEX wc = new() { cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate), hInstance = NativeMethods.GetModuleHandle(null), lpszClassName = cls, hCursor = NativeMethods.LoadCursor(IntPtr.Zero, 32512) };
        NativeMethods.RegisterClassEx(ref wc);

        _hwnd = NativeMethods.CreateWindowEx(0, cls, "Canvas", 0x40000000 | 0x10000000, 0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _hwndMap[_hwnd] = new WeakReference<CanvasViewport>(this);
        return new HandleRef(this, _hwnd);
    }

    private static readonly NativeMethods.WndProc WndProcDelegate = WndProc;

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposed = true;
        _cursorBlinkTimer?.Dispose();
        _cursorBlinkTimer = null;
        DisposeRenderTarget();
        _dashedStrokeStyle?.Dispose();
        _dashedStrokeStyle = null;
        _dwrite?.Dispose();
        _dwrite = null;
        _factory?.Dispose();
        _factory = null;
        _hwndMap.Remove(hwnd.Handle);
        NativeMethods.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var viewport = FromHwnd(hwnd);
        if (viewport == null) return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case 0x000F: viewport.RenderNative(); return IntPtr.Zero;
            case 0x0005: viewport.ResizeRT(); viewport.RenderNative(); return IntPtr.Zero;
            case 0x0201: viewport.HandleLDown(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0202: viewport.HandleLUp(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0204: viewport.HandleRDown(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0205: viewport.HandleRUp(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0207: viewport.HandleMDown(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0208: viewport.HandleMUp(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x0200: viewport.HandleMove(GetMousePoint(lParam)); return IntPtr.Zero;
            case 0x020A: viewport.HandleWheel(GetScreenPtAsClient(hwnd, lParam), GetWheelDelta(wParam)); return IntPtr.Zero;
            case 0x0100: viewport.HandleKeyDown(wParam); return IntPtr.Zero;
            case 0x0101: viewport.HandleKeyUp(wParam); return IntPtr.Zero;
            case 0x0102: viewport.HandleChar((char)wParam.ToInt64()); return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static readonly Dictionary<IntPtr, WeakReference<CanvasViewport>> _hwndMap = new();
    private static CanvasViewport? FromHwnd(IntPtr hwnd) => _hwndMap.TryGetValue(hwnd, out var wr) && wr.TryGetTarget(out var v) ? v : null;

    private static WpfPoint GetMousePoint(IntPtr lParam) => GetClientPt(lParam);

    private static void OnSceneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CanvasViewport v) { v.RebuildSnapshot(); v.RenderNative(); }
    }

    private static void OnCameraChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CanvasViewport v && e.NewValue is CameraState cam) { v._camera = cam; v._visDirty = true; v.RenderNative(); }
    }

    internal static void SetCapture(IntPtr hwnd) => NativeMethods.SetCapture(hwnd);
    internal static void ReleaseCapture() => NativeMethods.ReleaseCapture();
    internal static void SetFocus(IntPtr hwnd) => NativeMethods.SetFocus(hwnd);
    internal static bool GetClientRect(IntPtr hwnd, out RECT rect) => NativeMethods.GetClientRect(hwnd, out rect);
}
