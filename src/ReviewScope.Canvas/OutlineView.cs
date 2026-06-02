using ReviewScope.Domain;
using ReviewScope.Domain.Outline;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DirectWrite;
using Vortice.DXGI;
using FactoryType = Vortice.DirectWrite.FactoryType;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using Color4 = Vortice.Mathematics.Color4;
using RectangleF = System.Drawing.RectangleF;

namespace ReviewScope.Canvas;

/*
 * File: OutlineView.cs
 * Purpose: A stripped, dedicated outline editor host — a sibling of CanvasViewport with no
 *          camera/zoom/pan, connections, or tools. It lays one document out top-to-bottom with
 *          vertical scrolling and reuses OutlineDocument (draw/hit-test) plus the shared
 *          OutlineEditController so editing fidelity matches the in-canvas block editor exactly.
 * Notes:
 * - Owns its own ID2D1Factory + ID2D1HwndRenderTarget; shares the global DirectWrite factory.
 * - All structural/text editing goes through _edit (OutlineEditController). This view only adds
 *   rendering, scrolling, mouse caret/toggle hit-testing, and keyboard plumbing.
 * Please read the first ~20 lines of this file for a summary before reading the whole file.
 */

public sealed partial class OutlineView : HwndHost
{
    // --- layout constants ----------------------------------------------------------
    private const float PadLeft = 18f;
    private const float PadTop = 14f;
    private const float ScrollbarW = 10f;
    // Logseq-style centered writing column: cap the text width and center it in the window
    // rather than letting lines run the full width.
    private const float MaxContentWidth = 720f;
    private const float FontSize = 15.5f;
    private const string FontFamily = "Segoe UI";
    // Body text + page background are theme-driven (see CanvasTheme) so the outliner
    // tracks the rest of the chrome between light and dark.
    private static WpfColor TextColor => CanvasTheme.OutlineText;
    private static WpfColor BgColor => CanvasTheme.OutlineBg;

    // --- native window / D2D state -------------------------------------------------
    private IntPtr _hwnd;
    private bool _disposed;
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _rt;
    private IDWriteFactory? _dwrite;
    private DrawingContext? _ctx;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, IDWriteTextFormat> _textFormats = new();
    private ID2D1StrokeStyle? _dashedStroke;

    // --- editing / document state --------------------------------------------------
    private readonly OutlineEditController _edit = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private float _scrollY;
    private bool _mouseSelecting;
    private DispatcherTimer? _blinkTimer;

    /// <summary>Raised after any edit changes the document body, so the host can persist it.</summary>
    public event Action<string>? DocumentChanged;

    /// <summary>Raised after the collapsed-anchor set changes (expand/collapse toggle).</summary>
    public event Action<IReadOnlyCollection<string>>? CollapsedChanged;

    /// <summary>Raised when the user Ctrl+Clicks a <c>[[Page]]</c> link; carries the page name.</summary>
    public event Action<string>? PageLinkActivated;

    public OutlineView()
    {
        Focusable = true;
        _edit.CollapsedProvider = () => _collapsed;
        CanvasTheme.Changed += OnCanvasThemeChanged;
    }

    private void OnCanvasThemeChanged()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderNative)); return; }
        RenderNative();
    }

    /// <summary>The full markdown document being edited. Setting it resets the caret/scroll.</summary>
    public string Document
    {
        get => _edit.Body;
        set
        {
            _edit.Body = value ?? string.Empty;
            _edit.CursorPos = 0;
            _edit.SelectionAnchor = -1;
            _scrollY = 0;
            HideSlashMenu();
            RenderNative();
        }
    }

    /// <summary>Replace the set of collapsed anchor ids (keyed on each line's ^anchor).</summary>
    public void SetCollapsed(IEnumerable<string> ids)
    {
        _collapsed.Clear();
        foreach (var id in ids) _collapsed.Add(id);
        RenderNative();
    }

    public IReadOnlyCollection<string> CollapsedIds => _collapsed;

    /// <summary>
    /// Move keyboard focus into the outline body and park the caret on the first editable
    /// position. Used when the user presses Enter in the page-title box to "drop into" the
    /// body, Logseq-style. WPF's <see cref="UIElement.Focus"/> alone only focuses the host
    /// element; the hosted native window needs an explicit Win32 SetFocus to receive keys.
    /// </summary>
    public void FocusEditor()
    {
        Focus();
        if (_hwnd != IntPtr.Zero) NativeMethods.SetFocus(_hwnd);
        _edit.CursorPos = OutlineDocument.SnapToVisible(_edit.Body, BuildDocBlock().Style, 0, +1);
        _edit.SelectionAnchor = -1;
        _edit.CursorVisible = true;
        HideSlashMenu();
        RenderNative();
    }

    // -----------------------------------------------------------------------
    // HWND lifecycle
    // -----------------------------------------------------------------------
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const string cls = "ReviewScopeOutline";
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = cls,
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, 32513), // IDC_IBEAM
        };
        NativeMethods.RegisterClassEx(ref wc);

        _hwnd = NativeMethods.CreateWindowEx(0, cls, "Outline", 0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _hwndMap[_hwnd] = new WeakReference<OutlineView>(this);

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) => { _edit.CursorVisible = !_edit.CursorVisible; RenderNative(); };
        _blinkTimer.Start();

        return new HandleRef(this, _hwnd);
    }

    // Tab/Shift+Tab are "dialog navigation" keys: WPF's input system intercepts them on the
    // top-level HwndSource and moves focus to the next control *before* the WM_KEYDOWN can reach
    // our child HWND's WndProc (which is why typing/Enter work but Tab "jumped" to another tab).
    // TranslateAcceleratorCore is the hook WPF calls for the focused sink first — handle Tab here
    // and return true so WPF stops, performing the indent/outdent instead of navigating away.
    protected override bool TranslateAcceleratorCore(ref System.Windows.Interop.MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (msg.message == 0x0100 || msg.message == 0x0104) // WM_KEYDOWN / WM_SYSKEYDOWN
        {
            int vk = (int)msg.wParam;

            // Tab/Shift+Tab are dialog-navigation keys WPF moves focus on before our WndProc sees
            // them; intercept here so they indent/outdent instead.
            if (vk == 0x09)
            {
                OnKeyDown(0x09);
                return true;
            }

            // Ctrl-clipboard / select-all / inline-formatting combos. The host Window registers
            // Ctrl+C/V (board copy/paste) as InputBindings; if we let WPF process them they fire
            // the board commands and the editor never gets the keystroke. Claiming them here (the
            // focused sink is asked first) routes them straight to the editor and stops WPF — a
            // single, unambiguous execution, exactly like the Tab path above.
            bool ctrl = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            bool alt = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
            if (ctrl && !alt && vk is 0x43 or 0x58 or 0x56 or 0x41 or 0x42 or 0x49 or 0x45) // C X V A B I E
            {
                OnKeyDown(vk);
                return true;
            }
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposed = true;
        CanvasTheme.Changed -= OnCanvasThemeChanged;
        _blinkTimer?.Stop();
        _blinkTimer = null;
        DisposeRenderTarget();
        _dashedStroke?.Dispose(); _dashedStroke = null;
        _dwrite?.Dispose(); _dwrite = null;
        _factory?.Dispose(); _factory = null;
        _hwndMap.Remove(hwnd.Handle);
        NativeMethods.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private static readonly NativeMethods.WndProc WndProcDelegate = WndProc;
    private static readonly Dictionary<IntPtr, WeakReference<OutlineView>> _hwndMap = new();
    private static OutlineView? FromHwnd(IntPtr h) => _hwndMap.TryGetValue(h, out var wr) && wr.TryGetTarget(out var v) ? v : null;

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var view = FromHwnd(hwnd);
        if (view is null) return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case 0x000F: view.RenderNative(); return IntPtr.Zero;                       // WM_PAINT
            case 0x0005: view.ResizeRT(); view.RenderNative(); return IntPtr.Zero;       // WM_SIZE
            case 0x0201: view.OnLDown(ClientPt(lParam)); return IntPtr.Zero;             // WM_LBUTTONDOWN
            case 0x0202: view.OnLUp(); return IntPtr.Zero;                               // WM_LBUTTONUP
            case 0x0200: view.OnMove(ClientPt(lParam)); return IntPtr.Zero;              // WM_MOUSEMOVE
            case 0x020A: view.OnWheel(WheelDelta(wParam)); return IntPtr.Zero;           // WM_MOUSEWHEEL
            case 0x0100: view.OnKeyDown((int)wParam.ToInt64()); return IntPtr.Zero;      // WM_KEYDOWN
            case 0x0104:                                                                 // WM_SYSKEYDOWN (Alt held)
            {
                int vk = (int)wParam.ToInt64();
                if (vk is 0x25 or 0x26 or 0x27 or 0x28) { view.OnKeyDown(vk); return IntPtr.Zero; }
                break;
            }
            case 0x0102: view.OnChar((char)wParam.ToInt64()); return IntPtr.Zero;        // WM_CHAR
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // -----------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------
    public void RenderNative()
    {
        try { RenderNativeInternal(); }
        catch { DisposeRenderTarget(); }
    }

    private void RenderNativeInternal()
    {
        if (!EnsureRT() || _rt is null || _ctx is null) return;

        ClientSize(out int viewW, out int viewH);
        ComputeContentLayout(out float contentX, out float contentW);

        var block = BuildDocBlock();
        float docHeight = MeasureDocHeight(block, contentW);
        ClampScroll(docHeight, viewH);

        // Draw lays rows from content.Y downward and stops at content.Bottom; offsetting Y by
        // -scroll scrolls the whole document while the bottom stays pinned to the window edge.
        float contentY = PadTop - _scrollY;
        var content = new Rect(contentX, contentY, contentW, Math.Max(4, viewH - contentY));

        _rt.BeginDraw();
        _rt.Transform = Matrix3x2.Identity;
        _rt.Clear(_ctx.ToColor4(BgColor));

        OutlineDocument.Draw(_ctx, block, content, _edit.Body, TextColor, FontSize, FontFamily,
            bold: false, italic: false,
            editCursorPos: _edit.CursorPos,
            editSelectionAnchor: _edit.SelectionAnchor,
            editCursorVisible: _edit.CursorVisible);

        DrawScrollbar(viewW, viewH, docHeight);
        DrawSlashMenu();

        _rt.EndDraw();
    }

    private void DrawScrollbar(int viewW, int viewH, float docHeight)
    {
        if (_rt is null || _ctx is null) return;
        float visible = viewH;
        float total = docHeight + PadTop * 2;
        if (total <= visible) return;

        float trackX = viewW - ScrollbarW + 2f;
        float thumbH = Math.Max(24f, visible * (visible / total));
        float maxScroll = Math.Max(1f, total - visible);
        float thumbY = (_scrollY / maxScroll) * (visible - thumbH);
        var thumb = new RoundedRectangle(new RectangleF(trackX, thumbY + 2, ScrollbarW - 4, thumbH - 4), 3, 3);
        var t = CanvasTheme.OutlineText;
        _rt.FillRoundedRectangle(thumb, _ctx.GetBrush(WpfColor.FromArgb(70, t.R, t.G, t.B)));
    }

    private RenderBlock BuildDocBlock()
    {
        // A synthetic, position-less Text block: Text is an "outline block" (IsOutlineBlock),
        // and zeroing the spacing makes GetContentRect(block, bounds) == bounds, so the rect we
        // pass to Draw and the rect we measure/hit-test with stay identical.
        var style = new BoardItemStyle() with
        {
            OutlineEnabled = true,
            FontSize = FontSize,
            FontFamily = FontFamily,
            SpacingLeft = 0,
            SpacingRight = 0,
            SpacingTop = 0,
            SpacingBottom = 0,
            OutlineCollapsedItems = OutlineDocument.FormatCollapsedSet(_collapsed),
        };
        return new RenderBlock(Guid.Empty, "__outline", BlockKind.Text, string.Empty, string.Empty,
            0, 0, 0, 0, Body: _edit.Body, Style: style);
    }

    private float MeasureDocHeight(RenderBlock block, float contentW)
    {
        if (_dwrite is null) return 0;
        var bounds = new Rect(PadLeft, 0, contentW, 1_000_000);
        var rows = OutlineDocument.LayoutBulletRows(block, bounds, hideMarkers: false, _dwrite);
        if (rows.Count == 0) return 0;
        var last = rows[^1];
        return last.RowTop + last.RowHeight;
    }

    private void ClampScroll(float docHeight, int viewH)
    {
        float total = docHeight + PadTop * 2;
        float max = Math.Max(0, total - viewH);
        _scrollY = Math.Clamp(_scrollY, 0, max);
    }

    private bool EnsureRT()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return false;
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(FactoryType.Shared);
        if (_rt is not null) return true;

        ClientSize(out int pw, out int ph);
        var rtProps = new RenderTargetProperties(
            RenderTargetType.Hardware,
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 0, 0,
            RenderTargetUsage.None, FeatureLevel.Default);
        var hwndProps = new HwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new Vortice.Mathematics.SizeI(pw, ph),
            PresentOptions = PresentOptions.Immediately,
        };
        _rt = _factory.CreateHwndRenderTarget(rtProps, hwndProps);
        if (_rt is null) return false;

        _ctx = new DrawingContext(_rt, _factory, _dwrite, CameraState.Default, GetBrush, GetTextFormat, GetDashedStroke());
        return true;
    }

    private void ResizeRT()
    {
        if (_rt is null || _hwnd == IntPtr.Zero) return;
        ClientSize(out int pw, out int ph);
        try { _rt.Resize(new Vortice.Mathematics.SizeI(pw, ph)); }
        catch { DisposeRenderTarget(); EnsureRT(); }
    }

    private void DisposeRenderTarget()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
        foreach (var f in _textFormats.Values) f.Dispose();
        _textFormats.Clear();
        _rt?.Dispose(); _rt = null;
        _ctx = null;
    }

    private ID2D1SolidColorBrush GetBrush(WpfColor color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = _rt!.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
        _brushes[key] = b;
        return b;
    }

    private IDWriteTextFormat GetTextFormat(float size, bool sketchy)
    {
        string key = $"{size:F1}";
        if (_textFormats.TryGetValue(key, out var fmt)) return fmt;
        fmt = _dwrite!.CreateTextFormat(FontFamily, Vortice.DirectWrite.FontWeight.Normal,
            Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, size);
        _textFormats[key] = fmt;
        return fmt;
    }

    private ID2D1StrokeStyle GetDashedStroke()
    {
        if (_dashedStroke is not null) return _dashedStroke;
        _dashedStroke = _factory!.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash });
        return _dashedStroke;
    }

    // -----------------------------------------------------------------------
    // Mouse
    // -----------------------------------------------------------------------
    private void OnLDown(WpfPoint client)
    {
        NativeMethods.SetFocus(_hwnd);
        NativeMethods.SetCapture(_hwnd);
        HideSlashMenu();

        var block = BuildDocBlock();
        var content = CurrentContentRect();

        // Ctrl+Click on a [[Page]] link follows it instead of placing the caret.
        bool ctrl = (NativeMethods.GetKeyState(0x11) & 0x8000) != 0;
        if (ctrl && TryHitPageLink(block, content, client, out string page))
        {
            PageLinkActivated?.Invoke(page);
            return;
        }

        // Toggle (collapse/expand) takes priority over caret placement.
        if (_dwrite is not null && OutlineDocument.TryHitToggle(block, content, client, out int lineIndex, _dwrite)
            && lineIndex >= 0)
        {
            ToggleCollapseAt(block, lineIndex);
            RenderNative();
            CollapsedChanged?.Invoke(_collapsed);
            return;
        }

        int pos = HitTestCaret(block, content, client);
        _edit.CursorPos = pos;
        _edit.SelectionAnchor = pos;
        _mouseSelecting = true;
        _edit.CursorVisible = true;
        RenderNative();
    }

    private void OnMove(WpfPoint client)
    {
        if (!_mouseSelecting) return;
        var block = BuildDocBlock();
        _edit.CursorPos = HitTestCaret(block, CurrentContentRect(), client);
        RenderNative();
    }

    private void OnLUp()
    {
        if (!_mouseSelecting) return;
        _mouseSelecting = false;
        NativeMethods.ReleaseCapture();
        if (_edit.SelectionAnchor == _edit.CursorPos) _edit.SelectionAnchor = -1;
        RenderNative();
    }

    private void OnWheel(int delta)
    {
        _scrollY -= delta / 120f * 3f * FontSize;
        if (_scrollY < 0) _scrollY = 0;
        RenderNative();
    }

    private int HitTestCaret(RenderBlock block, Rect content, WpfPoint client)
    {
        if (_dwrite is null) return _edit.CursorPos;
        int raw = OutlineDocument.HitTestPoint(block, content, _edit.Body, FontSize, FontFamily, false, false, client, _dwrite);
        return OutlineDocument.SnapToVisible(_edit.Body, block.Style, raw, +1);
    }

    /// <summary>True when <paramref name="client"/> falls on a <c>[[Page]]</c> link; outputs its name.</summary>
    private bool TryHitPageLink(RenderBlock block, Rect content, WpfPoint client, out string page)
    {
        page = string.Empty;
        if (_dwrite is null) return false;
        int offset = OutlineDocument.HitTestPoint(block, content, _edit.Body, FontSize, FontFamily, false, false, client, _dwrite);
        foreach (var (rawStart, rawLength, inner, isRef) in OutlineDocument.ScanTagsAndRefs(_edit.Body))
        {
            if (isRef && offset >= rawStart && offset < rawStart + rawLength)
            {
                page = inner.Trim();
                return page.Length > 0;
            }
        }
        return false;
    }

    private void ToggleCollapseAt(RenderBlock block, int lineIndex)
    {
        var doc = OutlineDocument.Parse(_edit.Body, block.Style);
        if (lineIndex < 0 || lineIndex >= doc.Lines.Count) return;
        string? id = doc.Lines[lineIndex].AnchorId;
        if (string.IsNullOrEmpty(id)) return;
        if (!_collapsed.Remove(id)) _collapsed.Add(id);
    }

    private Rect CurrentContentRect()
    {
        ClientSize(out _, out int viewH);
        ComputeContentLayout(out float contentX, out float contentW);
        float contentY = PadTop - _scrollY;
        return new Rect(contentX, contentY, contentW, Math.Max(4, viewH - contentY));
    }

    /// <summary>Width + left offset of the centered writing column. Shared by render and hit-test
    /// so the caret lands exactly where text is drawn.</summary>
    private void ComputeContentLayout(out float contentX, out float contentW)
    {
        ClientSize(out int viewW, out _);
        float usable = Math.Max(8f, viewW - ScrollbarW - PadLeft * 2);
        contentW = Math.Min(MaxContentWidth, usable);
        contentX = Math.Max(PadLeft, (viewW - ScrollbarW - contentW) / 2f);
    }

    // -----------------------------------------------------------------------
    // Keyboard
    // -----------------------------------------------------------------------
    private void OnKeyDown(int vk)
    {
        bool shift = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
        bool ctrl = (NativeMethods.GetKeyState(0x11) & 0x8000) != 0;
        bool alt = (NativeMethods.GetKeyState(0x12) & 0x8000) != 0;
        string before = _edit.Body;

        // The slash command menu, when open, claims navigation/accept/dismiss keys before the
        // editor sees them (Up/Down move the selection, Enter/Tab accept, Escape closes).
        if (_slashVisible && TryHandleSlashKey(vk)) return;

        if (ctrl && !alt)
        {
            switch (vk)
            {
                case 0x41: _edit.SelectAll(); break;                    // A
                case 0x42: _edit.WrapSelection("**", "**"); break;      // B
                case 0x49: _edit.WrapSelection("*", "*"); break;        // I
                case 0x45: _edit.WrapSelection("`", "`"); break;        // E
                case 0x53 when shift: _edit.WrapSelection("~~", "~~"); break; // Shift+S
                case 0x43: CopySelection(); break;                      // C
                case 0x58: CutSelection(); break;                       // X
                case 0x56: Paste(); break;                              // V
                default: return;
            }
            AfterEdit(before, snap: false);
            return;
        }

        switch (vk)
        {
            case 0x0D: _edit.Enter(); break;                                  // Enter
            case 0x09: if (shift) _edit.Outdent(); else _edit.Indent(); break; // Tab
            case 0x08: _edit.Backspace(); break;                              // Backspace
            case 0x2E: _edit.Delete(); break;                                 // Delete
            case 0x25: _edit.MoveLeft(shift); break;                          // Left
            case 0x27: _edit.MoveRight(shift); break;                         // Right
            case 0x26: if (alt) _edit.MoveUp(); else _edit.MoveLineUp(shift); break;   // Up
            case 0x28: if (alt) _edit.MoveDown(); else _edit.MoveLineDown(shift); break; // Down
            case 0x24: _edit.MoveHome(shift); break;                          // Home
            case 0x23: _edit.MoveEnd(shift); break;                           // End
            default: return;
        }

        bool isCaretMove = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x23 or 0x24;
        AfterEdit(before, snap: isCaretMove && !alt);
    }

    private void OnChar(char c)
    {
        if (c < 32 || c == 127) return;                                  // control chars
        if ((NativeMethods.GetKeyState(0x11) & 0x8000) != 0) return;     // Ctrl combos handled in OnKeyDown
        string before = _edit.Body;
        _edit.InsertText(c.ToString());
        AfterEdit(before, snap: false);
    }

    /// <summary>Shared post-edit step: optional caret snap, keep caret on-screen, repaint, notify.</summary>
    private void AfterEdit(string before, bool snap)
    {
        if (snap)
            _edit.CursorPos = OutlineDocument.SnapToVisible(_edit.Body, BuildDocBlock().Style, _edit.CursorPos, +1);

        _edit.CursorVisible = true;
        EnsureCaretVisible();
        RefreshSlashMenu();
        RenderNative();
        if (!ReferenceEquals(before, _edit.Body) && before != _edit.Body)
            DocumentChanged?.Invoke(_edit.Body);
    }

    /// <summary>Scroll so the caret's row is within the viewport (caret-follows-scroll).</summary>
    private void EnsureCaretVisible()
    {
        if (_dwrite is null) return;
        ClientSize(out _, out int viewH);
        ComputeContentLayout(out float contentX, out float contentW);
        var block = BuildDocBlock();
        var bounds = new Rect(contentX, 0, contentW, 1_000_000);
        var rows = OutlineDocument.LayoutBulletRows(block, bounds, hideMarkers: false, _dwrite);
        if (rows.Count == 0) { _scrollY = 0; return; }

        int caretLine = OutlineDocument.LineIndexAt(_edit.Body, _edit.CursorPos);
        foreach (var row in rows)
        {
            if (row.Line.Index != caretLine) continue;
            float top = PadTop + row.RowTop;
            float bottom = top + row.RowHeight;
            if (top - _scrollY < PadTop) _scrollY = top - PadTop;
            else if (bottom - _scrollY > viewH - PadTop) _scrollY = bottom - viewH + PadTop;
            if (_scrollY < 0) _scrollY = 0;
            return;
        }
    }

    // -----------------------------------------------------------------------
    // Clipboard
    // -----------------------------------------------------------------------
    private void CopySelection()
    {
        if (_edit.GetSelection() is not { } sel) return;
        try { Clipboard.SetText(_edit.Body.Substring(sel.Start, sel.End - sel.Start)); } catch { }
    }

    private void CutSelection()
    {
        if (_edit.GetSelection() is not { } sel) return;
        try { Clipboard.SetText(_edit.Body.Substring(sel.Start, sel.End - sel.Start)); } catch { }
        _edit.DeleteSelection();
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (!string.IsNullOrEmpty(text)) _edit.InsertText(text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    // -----------------------------------------------------------------------
    // Helpers: client geometry + Win32 message decode
    // -----------------------------------------------------------------------
    private void ClientSize(out int w, out int h)
    {
        if (NativeMethods.GetClientRect(_hwnd, out var r)) { w = Math.Max(1, r.Right - r.Left); h = Math.Max(1, r.Bottom - r.Top); }
        else { w = 1; h = 1; }
    }

    private static WpfPoint ClientPt(IntPtr lParam)
    {
        int raw = (int)lParam.ToInt64();
        return new WpfPoint((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
    }

    private static int WheelDelta(IntPtr wParam) => (short)(((int)wParam.ToInt64() >> 16) & 0xFFFF);

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool RegisterClassEx(ref WNDCLASSEX pcx);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }
    }
}
