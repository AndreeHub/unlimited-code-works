using ReviewScope.Domain;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Vortice.Direct2D1;
using D2DRect = Vortice.Mathematics.Rect;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

/*
 * File: CanvasViewport.Autocomplete.cs
 * Purpose: Inline #tag / [[wiki link]] autocomplete popup shown while editing
 *   outline blocks. State here is read by the rendering loop (RenderNativeInternal)
 *   and driven by HandleEditModeKey + HandleChar in CanvasViewport.Input.cs.
 */

public sealed partial class CanvasViewport
{
    private const float AutocompleteRowHeight = 22f;
    private const float AutocompletePadding = 6f;
    private const float AutocompleteWidth = 220f;

    // Autocomplete state. Kept package-private so RenderNative / Input partials can read it.
    internal bool _autocompleteVisible;
    internal TagTokenKind _autocompleteKind;
    internal IReadOnlyList<string> _autocompleteItems = Array.Empty<string>();
    internal int _autocompleteSelectedIndex;
    internal int _autocompleteTriggerIndex;

    /// <summary>
    /// Set by MainWindow (or any host) to feed candidate suggestions. The canvas only
    /// knows about its own edit state; the host knows the workspace's tag vocabulary.
    /// </summary>
    public Func<TagTokenKind, string, IReadOnlyList<string>>? AutocompleteSuggestionsProvider { get; set; }

    /// <summary>Recompute popup state from <c>_editBody</c> / <c>_editCursorPos</c>. Call after any text mutation.</summary>
    internal void RefreshAutocompleteState()
    {
        if (_editingNoteKey is null || _editingTitle) { HideAutocomplete(); return; }
        var block = CurrentEditingBlock();
        if (block is null || !OutlineDocument.IsOutlineBlock(block)) { HideAutocomplete(); return; }

        var ctx = TagTokens.DetectAutocomplete(_editBody, _editCursorPos);
        if (ctx is null) { HideAutocomplete(); return; }

        var items = AutocompleteSuggestionsProvider?.Invoke(ctx.Value.Kind, ctx.Value.Prefix) ?? Array.Empty<string>();
        if (items.Count == 0) { HideAutocomplete(); return; }

        _autocompleteVisible = true;
        _autocompleteKind = ctx.Value.Kind;
        _autocompleteItems = items;
        _autocompleteTriggerIndex = ctx.Value.TriggerIndex;
        if (_autocompleteSelectedIndex >= items.Count) _autocompleteSelectedIndex = 0;
        if (_autocompleteSelectedIndex < 0) _autocompleteSelectedIndex = 0;
    }

    internal void HideAutocomplete()
    {
        if (!_autocompleteVisible && _autocompleteItems.Count == 0) return;
        _autocompleteVisible = false;
        _autocompleteItems = Array.Empty<string>();
        _autocompleteSelectedIndex = 0;
    }

    /// <summary>
    /// Returns true when the key was consumed by the popup (navigation, accept, dismiss).
    /// Caller should skip normal editing handling in that case.
    /// </summary>
    internal bool TryHandleAutocompleteKey(Key key)
    {
        if (!_autocompleteVisible || _autocompleteItems.Count == 0) return false;
        switch (key)
        {
            case Key.Up:
                _autocompleteSelectedIndex = (_autocompleteSelectedIndex - 1 + _autocompleteItems.Count) % _autocompleteItems.Count;
                RenderNative();
                return true;
            case Key.Down:
                _autocompleteSelectedIndex = (_autocompleteSelectedIndex + 1) % _autocompleteItems.Count;
                RenderNative();
                return true;
            case Key.Tab:
            case Key.Return:
                AcceptAutocomplete();
                return true;
            case Key.Escape:
                HideAutocomplete();
                RenderNative();
                return true;
        }
        return false;
    }

    private void AcceptAutocomplete()
    {
        if (!_autocompleteVisible || _autocompleteSelectedIndex < 0 || _autocompleteSelectedIndex >= _autocompleteItems.Count) return;
        string value = _autocompleteItems[_autocompleteSelectedIndex];
        int triggerLen = _autocompleteKind == TagTokenKind.Tag ? 1 : 2; // '#' vs '[['
        int replaceStart = _autocompleteTriggerIndex + triggerLen;
        if (replaceStart < 0 || replaceStart > _editBody.Length) { HideAutocomplete(); RenderNative(); return; }
        int replaceEnd = Math.Min(_editCursorPos, _editBody.Length);
        if (replaceEnd < replaceStart) replaceEnd = replaceStart;

        string suffix = _autocompleteKind == TagTokenKind.WikiLink ? "]] " : " ";
        string replacement = value + suffix;
        _editBody = _editBody.Substring(0, replaceStart) + replacement + _editBody.Substring(replaceEnd);
        _editCursorPos = replaceStart + replacement.Length;
        _editSelectionAnchor = -1;
        HideAutocomplete();
        RenderNative();
    }

    /// <summary>
    /// Draw the popup in screen coordinates. Anchored just below the currently
    /// editing block; caller invokes after resetting the world transform.
    /// </summary>
    internal void DrawAutocompletePopup()
    {
        if (!_autocompleteVisible || _rt is null || _drawingContext is null || _autocompleteItems.Count == 0) return;
        if (_editingNoteKey is null) return;

        var vis = _snapshot.Blocks.FirstOrDefault(b => b.Block.Key.Equals(_editingNoteKey, StringComparison.OrdinalIgnoreCase));
        if (vis is null) return;

        // World → screen for the bottom-left of the editing block.
        double sx = vis.Bounds.X * _camera.Zoom + _camera.OffsetX;
        double sy = (vis.Bounds.Y + vis.Bounds.Height) * _camera.Zoom + _camera.OffsetY;

        float panelW = AutocompleteWidth;
        float panelH = AutocompletePadding * 2 + _autocompleteItems.Count * AutocompleteRowHeight + 18f; // +18 for header
        float panelX = (float)sx + 8;
        float panelY = (float)sy + 6;

        // Keep on-screen.
        float maxX = (float)ActualWidth - panelW - 6;
        float maxY = (float)ActualHeight - panelH - 6;
        if (panelX > maxX) panelX = Math.Max(6, maxX);
        if (panelY > maxY) panelY = Math.Max(6, maxY);

        var bounds = new RectangleF(panelX, panelY, panelW, panelH);
        _rt.FillRoundedRectangle(new RoundedRectangle(bounds, 6, 6), GetBrush(WpfColor.FromArgb(244, 255, 255, 255)));
        _rt.DrawRoundedRectangle(new RoundedRectangle(bounds, 6, 6), GetBrush(WpfColor.FromArgb(230, 207, 215, 226)), 1f);

        // Header
        string headerLabel = _autocompleteKind == TagTokenKind.Tag ? "Tags  (#)" : "Links  ([[)";
        var headerFmt = GetTextFormat(10.5f);
        var headerOldAlign = headerFmt.TextAlignment;
        headerFmt.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        _rt.DrawText(headerLabel, headerFmt,
            new D2DRect(panelX + AutocompletePadding, panelY + 3, panelW - AutocompletePadding * 2, 14),
            GetBrush(WpfColor.FromRgb(112, 122, 138)));
        headerFmt.TextAlignment = headerOldAlign;

        // Items
        var itemFmt = GetTextFormat(12f);
        var oldAlign = itemFmt.TextAlignment;
        itemFmt.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        for (int i = 0; i < _autocompleteItems.Count; i++)
        {
            float rowY = panelY + 18f + AutocompletePadding + i * AutocompleteRowHeight;
            var rowRect = new RectangleF(panelX + 3, rowY, panelW - 6, AutocompleteRowHeight);
            if (i == _autocompleteSelectedIndex)
            {
                _rt.FillRoundedRectangle(new RoundedRectangle(rowRect, 4, 4), GetBrush(WpfColor.FromArgb(255, 232, 242, 255)));
            }
            string prefix = _autocompleteKind == TagTokenKind.Tag ? "#" : "[[";
            string label = prefix + _autocompleteItems[i];
            _rt.DrawText(label, itemFmt,
                new D2DRect(panelX + AutocompletePadding, rowY + 3, panelW - AutocompletePadding * 2, AutocompleteRowHeight - 4),
                GetBrush(WpfColor.FromRgb(38, 47, 64)));
        }
        itemFmt.TextAlignment = oldAlign;
    }
}
