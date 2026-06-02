using System.Windows;
using Vortice.Direct2D1;
using D2DRect = Vortice.Mathematics.Rect;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

/*
 * File: OutlineView.SlashMenu.cs
 * Purpose: Logseq-style "/" command menu for the outline editor. Typing "/" at a word
 *   boundary opens a popup of formatting + insert commands; the prefix after the slash
 *   filters them. Selecting one removes the typed "/prefix" and applies the command.
 * Notes:
 * - Self-contained: detection reads _edit.Body/_edit.CursorPos, the popup is drawn with the
 *   same D2D render target as the document (no WPF overlay), and key handling is claimed in
 *   OutlineView.OnKeyDown before normal editing. Only commands that the outline renderer
 *   actually understands are offered, so every pick produces a visible result.
 */

public sealed partial class OutlineView
{
    // --- popup layout --------------------------------------------------------------
    private const float SlashRowHeight = 24f;
    private const float SlashPadding = 6f;
    private const float SlashWidth = 240f;
    private const float SlashHeaderH = 18f;
    private const int SlashMaxRows = 8;

    private enum SlashAction { Bold, Italic, Strike, Code, PageRef, BlockRef, Tag, Todo, Done, Divider }

    private readonly record struct SlashCommand(string Label, string Hint, string Keywords, SlashAction Action);

    // Ordered roughly like Logseq's BASIC group; only renderable commands are listed.
    private static readonly SlashCommand[] AllSlashCommands =
    {
        new("Page reference", "[[ ]]", "page reference link wiki",       SlashAction.PageRef),
        new("Block reference", "(( ))", "block reference embed",          SlashAction.BlockRef),
        new("Tag",             "#",     "tag hashtag",                    SlashAction.Tag),
        new("TODO",            "task",  "todo task checkbox later",       SlashAction.Todo),
        new("DONE",            "done",  "done complete finished",         SlashAction.Done),
        new("Bold",            "**b**", "bold strong",                    SlashAction.Bold),
        new("Italic",          "*i*",   "italic emphasis",                SlashAction.Italic),
        new("Strikethrough",   "~~s~~", "strike strikethrough",           SlashAction.Strike),
        new("Code",            "`c`",   "code inline monospace",          SlashAction.Code),
        new("Divider",         "---",   "divider horizontal rule line",   SlashAction.Divider),
    };

    // --- state ---------------------------------------------------------------------
    private bool _slashVisible;
    private int _slashTrigger;   // index of the '/' in _edit.Body
    private int _slashSelected;
    private List<SlashCommand> _slashItems = new();

    private void HideSlashMenu()
    {
        _slashVisible = false;
        _slashItems = new List<SlashCommand>();
        _slashSelected = 0;
    }

    /// <summary>Recompute popup visibility/filtering from the caret position. Call after edits.</summary>
    private void RefreshSlashMenu()
    {
        if (DetectSlash(_edit.Body, _edit.CursorPos) is not (int trigger, string prefix))
        {
            HideSlashMenu();
            return;
        }

        var items = FilterSlashCommands(prefix);
        if (items.Count == 0) { HideSlashMenu(); return; }

        _slashVisible = true;
        _slashTrigger = trigger;
        _slashItems = items;
        if (_slashSelected >= items.Count || _slashSelected < 0) _slashSelected = 0;
    }

    /// <summary>
    /// Look back from the caret for an active "/command" token: a '/' at the start of the
    /// line's content or after whitespace, followed only by letters/digits up to the caret.
    /// Returns the slash index and the typed prefix, or null.
    /// </summary>
    private static (int Trigger, string Prefix)? DetectSlash(string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '/')
            {
                bool boundary = i == 0 || char.IsWhiteSpace(text[i - 1]);
                if (!boundary) return null;
                return (i, text[(i + 1)..caret]);
            }
            // The prefix is a single contiguous run of word characters; anything else ends it.
            if (!char.IsLetterOrDigit(c)) return null;
        }
        return null;
    }

    private static List<SlashCommand> FilterSlashCommands(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return AllSlashCommands.ToList();
        string q = prefix.ToLowerInvariant();
        // Prefer label/keyword prefix matches, then substring matches, preserving menu order.
        var starts = new List<SlashCommand>();
        var contains = new List<SlashCommand>();
        foreach (var cmd in AllSlashCommands)
        {
            string label = cmd.Label.ToLowerInvariant();
            string keys = cmd.Keywords;
            bool start = label.StartsWith(q, StringComparison.Ordinal)
                || keys.Split(' ').Any(k => k.StartsWith(q, StringComparison.Ordinal));
            if (start) { starts.Add(cmd); continue; }
            if (label.Contains(q, StringComparison.Ordinal) || keys.Contains(q, StringComparison.Ordinal))
                contains.Add(cmd);
        }
        starts.AddRange(contains);
        return starts;
    }

    /// <summary>
    /// Handle a key while the menu is open. Returns true when consumed so the caller skips
    /// normal editing.
    /// </summary>
    private bool TryHandleSlashKey(int vk)
    {
        if (!_slashVisible || _slashItems.Count == 0) return false;
        switch (vk)
        {
            case 0x26: // Up
                _slashSelected = (_slashSelected - 1 + _slashItems.Count) % _slashItems.Count;
                RenderNative();
                return true;
            case 0x28: // Down
                _slashSelected = (_slashSelected + 1) % _slashItems.Count;
                RenderNative();
                return true;
            case 0x0D: // Enter
            case 0x09: // Tab
                AcceptSlashMenu();
                return true;
            case 0x1B: // Escape
                HideSlashMenu();
                RenderNative();
                return true;
        }
        return false;
    }

    private void AcceptSlashMenu()
    {
        if (!_slashVisible || _slashItems.Count == 0) { HideSlashMenu(); RenderNative(); return; }
        var cmd = _slashItems[Math.Clamp(_slashSelected, 0, _slashItems.Count - 1)];

        string before = _edit.Body;

        // Remove the typed "/prefix" first, then apply the command at that spot.
        int from = _slashTrigger;
        int to = Math.Clamp(_edit.CursorPos, 0, _edit.Body.Length);
        if (from < 0 || from > _edit.Body.Length) { HideSlashMenu(); RenderNative(); return; }
        if (to < from) to = from;
        _edit.Body = _edit.Body.Remove(from, to - from);
        _edit.CursorPos = from;
        _edit.SelectionAnchor = -1;

        ApplySlashCommand(cmd.Action);
        HideSlashMenu();
        AfterEdit(before, snap: false);
    }

    private void ApplySlashCommand(SlashAction action)
    {
        switch (action)
        {
            case SlashAction.Bold: _edit.WrapSelection("**", "**"); break;
            case SlashAction.Italic: _edit.WrapSelection("*", "*"); break;
            case SlashAction.Strike: _edit.WrapSelection("~~", "~~"); break;
            case SlashAction.Code: _edit.WrapSelection("`", "`"); break;
            case SlashAction.PageRef: _edit.WrapSelection("[[", "]]"); break;
            case SlashAction.BlockRef: _edit.WrapSelection("((", "))"); break;
            case SlashAction.Tag: _edit.InsertText("#"); break;
            case SlashAction.Todo: InsertLinePrefix("TODO "); break;
            case SlashAction.Done: InsertLinePrefix("DONE "); break;
            case SlashAction.Divider: InsertLinePrefix("---"); break;
        }
    }

    /// <summary>Insert <paramref name="prefix"/> at the start of the caret line's text content
    /// (after the bullet marker), keeping the caret in place relative to the text.</summary>
    private void InsertLinePrefix(string prefix)
    {
        var (lineStart, _, lineText) = OutlineDocument.LineAt(_edit.Body, _edit.CursorPos);
        int at = lineStart + OutlineDocument.PrefixLengthForLine(lineText);
        _edit.Body = _edit.Body.Insert(at, prefix);
        if (_edit.CursorPos >= at) _edit.CursorPos += prefix.Length;
        _edit.SelectionAnchor = -1;
    }

    // --- rendering -----------------------------------------------------------------
    private void DrawSlashMenu()
    {
        if (!_slashVisible || _rt is null || _slashItems.Count == 0) return;
        if (!TryGetCaretAnchor(out float anchorX, out float anchorTopY, out float anchorBottomY)) return;

        ClientSize(out int viewW, out int viewH);

        int shown = Math.Min(_slashItems.Count, SlashMaxRows);
        float panelW = SlashWidth;
        float panelH = SlashPadding * 2 + SlashHeaderH + shown * SlashRowHeight;

        // Prefer below the caret line; flip above if it would overflow the bottom.
        float panelX = anchorX;
        float panelY = anchorBottomY + 4f;
        if (panelY + panelH > viewH - 6f && anchorTopY - panelH - 4f > 6f)
            panelY = anchorTopY - panelH - 4f;
        panelX = Math.Clamp(panelX, 6f, Math.Max(6f, viewW - panelW - 6f));
        panelY = Math.Clamp(panelY, 6f, Math.Max(6f, viewH - panelH - 6f));

        var bounds = new RectangleF(panelX, panelY, panelW, panelH);
        _rt.FillRoundedRectangle(new RoundedRectangle(bounds, 6, 6), GetBrush(CanvasTheme.PopupBg));
        _rt.DrawRoundedRectangle(new RoundedRectangle(bounds, 6, 6), GetBrush(CanvasTheme.PopupBorder), 1f);

        var headerFmt = GetTextFormat(10f, false);
        _rt.DrawText("BASIC", headerFmt,
            new D2DRect(panelX + SlashPadding + 2, panelY + 4, panelW - SlashPadding * 2, 14),
            GetBrush(CanvasTheme.PopupHeader));

        var labelFmt = GetTextFormat(13f, false);
        var hintFmt = GetTextFormat(11f, false);

        // Keep the selected row in view when the list is longer than the visible window.
        int first = 0;
        if (_slashItems.Count > SlashMaxRows)
            first = Math.Clamp(_slashSelected - SlashMaxRows / 2, 0, _slashItems.Count - SlashMaxRows);

        for (int row = 0; row < shown; row++)
        {
            int i = first + row;
            float rowY = panelY + SlashPadding + SlashHeaderH + row * SlashRowHeight;
            var rowRect = new RectangleF(panelX + 3, rowY, panelW - 6, SlashRowHeight - 1);
            bool selected = i == _slashSelected;
            if (selected)
                _rt.FillRoundedRectangle(new RoundedRectangle(rowRect, 4, 4), GetBrush(CanvasTheme.PopupSelectedRow));

            _rt.DrawText(_slashItems[i].Label, labelFmt,
                new D2DRect(panelX + SlashPadding + 2, rowY + 4, panelW - SlashPadding * 2, SlashRowHeight - 4),
                GetBrush(CanvasTheme.PopupLabel));

            // Right-aligned dim hint (e.g. "[[ ]]").
            string hint = _slashItems[i].Hint;
            if (!string.IsNullOrEmpty(hint))
                _rt.DrawText(hint, hintFmt,
                    new D2DRect(panelX + SlashPadding, rowY + 5, panelW - SlashPadding * 2 - 4, SlashRowHeight - 4),
                    GetBrush(CanvasTheme.PopupHint));
        }
    }

    /// <summary>Screen-space X and the top/bottom Y of the caret's row, for popup placement.</summary>
    private bool TryGetCaretAnchor(out float x, out float topY, out float bottomY)
    {
        x = topY = bottomY = 0;
        if (_dwrite is null) return false;
        ComputeContentLayout(out float contentX, out float contentW);
        var block = BuildDocBlock();
        var bounds = new Rect(contentX, 0, contentW, 1_000_000);
        var rows = OutlineDocument.LayoutBulletRows(block, bounds, hideMarkers: false, _dwrite);
        if (rows.Count == 0) return false;

        int caretLine = OutlineDocument.LineIndexAt(_edit.Body, _edit.CursorPos);
        foreach (var r in rows)
        {
            if (r.Line.Index != caretLine) continue;
            x = contentX + r.Indent + 22f;
            topY = PadTop + r.RowTop - _scrollY;
            bottomY = PadTop + r.RowTop + r.RowHeight - _scrollY;
            return true;
        }
        return false;
    }
}
