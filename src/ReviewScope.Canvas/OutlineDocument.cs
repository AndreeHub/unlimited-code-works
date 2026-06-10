using ReviewScope.Domain;
using System.Drawing;
using System.Numerics;
using System.Windows;
using Vortice.Direct2D1;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ReviewScope.Canvas;

internal sealed record OutlineLine(
    int Index,
    int Start,
    int Length,
    int Level,
    int PrefixLength,
    string Text,
    string Id,
    int ParentIndex,
    bool HasChildren,
    bool IsCollapsed,
    bool IsHidden,
    string? AnchorId = null,
    int AnchorLength = 0);

internal sealed record OutlineRenderLine(
    OutlineLine Line,
    float X,
    float Y,
    float TextX,
    float ToggleX,
    float ToggleY,
    float RowHeight);

internal sealed class OutlineDocument
{
    private const float IndentWidth = 18f;
    public const float IndentWidthPublic = IndentWidth;
    private const float BulletRadius = 2.4f;
    private const float CheckboxSize = 11f;
    private const float MaxTextLayoutHeight = 4096f;

    /// <summary>State of a TODO-style line: nothing, open, or done.</summary>
    public enum TodoState { None, Todo, Done }

    /// <summary>
    /// True when a visible bullet line's text is a horizontal-divider marker. Logseq accepts
    /// a run of three or more dashes (with optional surrounding whitespace) on its own bullet.
    /// </summary>
    public static bool IsDividerText(string visibleLineText)
    {
        string t = visibleLineText.Trim();
        if (t.Length < 3) return false;
        foreach (char c in t)
            if (c != '-') return false;
        return true;
    }

    public static TodoState ClassifyTodo(string visibleLineText)
    {
        if (visibleLineText.StartsWith("TODO ", StringComparison.Ordinal)) return TodoState.Todo;
        if (visibleLineText.StartsWith("DONE ", StringComparison.Ordinal)) return TodoState.Done;
        if (string.Equals(visibleLineText, "TODO", StringComparison.Ordinal)) return TodoState.Todo;
        if (string.Equals(visibleLineText, "DONE", StringComparison.Ordinal)) return TodoState.Done;
        return TodoState.None;
    }

    public OutlineDocument(IReadOnlyList<OutlineLine> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<OutlineLine> Lines { get; }
    public IEnumerable<OutlineLine> VisibleLines => Lines.Where(l => !l.IsHidden);

    public static bool IsOutlineBlock(RenderBlock block) =>
        block.Kind is BlockKind.Note or BlockKind.Text or BlockKind.Transclusion;

    public static HashSet<string> ParseCollapsedSet(BoardItemStyle? style) =>
        (style?.OutlineCollapsedItems ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    public static string FormatCollapsedSet(IEnumerable<string> ids) =>
        string.Join(';', ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal));

    public static OutlineDocument Parse(string text, BoardItemStyle? style)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var collapsed = ParseCollapsedSet(style);
        var raw = new List<OutlineLineBuilder>();
        int start = 0;
        int index = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            string line = text[start..end];
            raw.Add(ParseLine(index++, start, line));
            if (end == text.Length) break;
            start = end + 1;
        }

        var stack = new Stack<int>();
        var parentIndexes = new int[raw.Count];
        Array.Fill(parentIndexes, -1);
        for (int i = 0; i < raw.Count; i++)
        {
            int level = raw[i].Level;
            while (stack.Count > 0 && raw[stack.Peek()].Level >= level)
                stack.Pop();
            if (stack.Count > 0)
                parentIndexes[i] = stack.Peek();
            stack.Push(i);
        }

        var hasChildren = new bool[raw.Count];
        for (int i = 0; i < parentIndexes.Length; i++)
        {
            int parent = parentIndexes[i];
            if (parent >= 0)
                hasChildren[parent] = true;
        }

        var hidden = new bool[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            int parent = parentIndexes[i];
            while (parent >= 0)
            {
                if (raw[parent].AnchorId is string pa && collapsed.Contains(pa))
                {
                    hidden[i] = true;
                    break;
                }
                parent = parentIndexes[parent];
            }
        }

        return new OutlineDocument(raw.Select((line, i) => new OutlineLine(
            line.Index,
            line.Start,
            line.Length,
            line.Level,
            line.PrefixLength,
            line.Text,
            line.Id,
            parentIndexes[i],
            hasChildren[i],
            line.AnchorId is not null && collapsed.Contains(line.AnchorId),
            hidden[i],
            line.AnchorId,
            line.AnchorLength)).ToList());
    }

    public static Rect GetContentRect(RenderBlock block, Rect bounds)
    {
        if (block.Kind == BlockKind.Note)
            return new Rect(bounds.X + 14, bounds.Y + 42, Math.Max(4, bounds.Width - 28), Math.Max(4, bounds.Height - 54));

        var style = block.Style ?? new BoardItemStyle();
        double padL = Math.Max(0, style.SpacingLeft);
        double padR = Math.Max(0, style.SpacingRight);
        double padT = Math.Max(0, style.SpacingTop);
        double padB = Math.Max(0, style.SpacingBottom);
        return new Rect(bounds.X + padL, bounds.Y + padT, Math.Max(4, bounds.Width - padL - padR), Math.Max(4, bounds.Height - padT - padB));
    }

    public static bool TryHitToggle(RenderBlock block, Rect bounds, WpfPoint world, out int lineIndex,
        Vortice.DirectWrite.IDWriteFactory? dwrite = null)
    {
        lineIndex = -1;
        if (!IsOutlineBlock(block))
            return false;

        var style = block.Style ?? new BoardItemStyle();
        var doc = Parse(block.Body ?? string.Empty, style);
        var content = GetContentRect(block, bounds);
        float fontSize = block.Kind == BlockKind.Note ? Math.Clamp((float)style.FontSize, 8f, 48f) : Math.Clamp((float)style.FontSize, 8f, 96f);
        float rowH = Math.Max(18f, fontSize * 1.5f);
        float maxW = (float)content.Width;

        Vortice.DirectWrite.IDWriteTextFormat? fmt = null;
        if (dwrite is not null)
        {
            string fontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily!;
            fmt = dwrite.CreateTextFormat(fontFamily,
                style.Bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
                style.Italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
                Vortice.DirectWrite.FontStretch.Normal, fontSize);
            fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;
        }

        float y = (float)content.Y;
        try
        {
            foreach (var line in doc.VisibleLines)
            {
                if (y > content.Bottom) break;
                float bulletX = (float)content.X + line.Level * IndentWidth + 12f;
                var hit = new Rect(bulletX - 18, y, 28, rowH);
                if (line.HasChildren && hit.Contains(world))
                {
                    lineIndex = line.Index;
                    return true;
                }
                float drawn = rowH;
                if (fmt is not null)
                {
                    float available = Math.Max(8f, maxW - line.Level * IndentWidth - 22f);
                    var (displayText, displaySpans, _, _) = PrepareDisplayLine(line.Text, hideMarkers: true);
                    string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
                    using var layout = dwrite!.CreateTextLayout(visibleText, fmt, available, MaxTextLayoutHeight);
                    ApplyInlineMarkdownSpans(null, layout, displaySpans);
                    drawn = MeasureRowHeight(layout, rowH);
                }
                y += drawn;
            }
        }
        finally
        {
            fmt?.Dispose();
        }
        return false;
    }

    public static void Draw(
        DrawingContext ctx,
        RenderBlock block,
        Rect content,
        string text,
        WpfColor color,
        float fontSize,
        string fontFamily,
        bool bold,
        bool italic,
        int editCursorPos = -1,
        int editSelectionAnchor = -1,
        bool editCursorVisible = false)
    {
        if (content.Width < 4 || content.Height < 4)
            return;

        var doc = Parse(text, block.Style);
        float x = (float)content.X;
        float y = (float)content.Y;
        float maxW = (float)content.Width;
        float rowH = Math.Max(18f, fontSize * 1.5f);
        var guideBrush = ctx.GetBrush(CanvasTheme.IsDark
            ? WpfColor.FromArgb(78, 72, 80, 94)
            : WpfColor.FromArgb(150, 225, 225, 225));
        var bulletBrush = ctx.GetBrush(CanvasTheme.IsDark
            ? WpfColor.FromArgb(170, 111, 121, 138)
            : WpfColor.FromRgb(0xD2, 0xD2, 0xD2));
        var textBrush = ctx.GetBrush(color);
        var caretBrush = ctx.GetBrush(color);
        var selBrush = ctx.GetBrush(WpfColor.FromArgb(70, 66, 153, 225));
        using var fmt = ctx.DWriteFactory.CreateTextFormat(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
            bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
            italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
            Vortice.DirectWrite.FontStretch.Normal,
            fontSize);
        fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;

        bool hasCursor = editCursorPos >= 0;
        int selStart = -1, selEnd = -1;
        if (hasCursor && editSelectionAnchor >= 0 && editSelectionAnchor != editCursorPos)
        {
            selStart = Math.Min(editCursorPos, editSelectionAnchor);
            selEnd = Math.Max(editCursorPos, editSelectionAnchor);
        }
        var activeBranch = hasCursor
            ? BuildActiveBranchIndexSet(doc, LineIndexAt(text, editCursorPos))
            : new HashSet<int>();
        var activeBranchRows = activeBranch.Count == 0
            ? Array.Empty<ActiveBranchRow>()
            : LayoutActiveBranchRows(ctx, doc, content, maxW, rowH, fmt, activeBranch, editCursorPos, hasCursor);

        var visibleLines = doc.VisibleLines.ToList();
        for (int visibleIndex = 0; visibleIndex < visibleLines.Count; visibleIndex++)
        {
            var line = visibleLines[visibleIndex];
            if (y > content.Bottom - 2) break;
            float indent = line.Level * IndentWidth;
            float bulletX = x + indent + 12f;
            float textX = x + indent + 22f;
            float available = Math.Max(8f, maxW - indent - 22f);
            int lineRawEnd = line.Start + line.Length - line.AnchorLength;
            bool caretOnLine = hasCursor && editCursorPos >= line.Start && editCursorPos <= lineRawEnd;

            if (IsTipBegin(line.Text) && !caretOnLine)
            {
                int endIndex = FindTipEnd(visibleLines, visibleIndex);
                if (!IsCaretInLineRange(visibleLines, visibleIndex, endIndex, editCursorPos))
                {
                    y += DrawTipBlock(ctx, visibleLines, visibleIndex, endIndex, x, y, maxW, rowH, fontSize, fontFamily, color);
                    visibleIndex = endIndex;
                    continue;
                }
            }

            for (int level = 1; level <= line.Level; level++)
            {
                float guideX = x + level * IndentWidth - 7f;
                ctx.RenderTarget.DrawLine(new Vector2(guideX, y - 1), new Vector2(guideX, Math.Min(y + rowH, (float)content.Bottom)), guideBrush, ctx.InvStroke(0.75f));
            }

            var todo = ClassifyTodo(line.Text);
            bool hasCheckbox = todo != TodoState.None;

            // Logseq-style horizontal divider: a bullet whose content is exactly "---" renders
            // as a full-width page rule with NO bullet glyph — it visually splits the page rather
            // than reading as a list item. The caret line still shows the raw "---" so it stays
            // editable (type a 4th char or backspace to dissolve the divider).
            if (IsDividerText(line.Text) && !caretOnLine)
            {
                float ruleY = y + rowH * 0.5f;
                var ruleBrush = ctx.GetBrush(WpfColor.FromArgb(110, color.R, color.G, color.B));
                ctx.RenderTarget.DrawLine(new Vector2(x, ruleY), new Vector2(x + maxW, ruleY), ruleBrush, ctx.InvStroke(1.4f));
                y += rowH;
                continue;
            }

            float checkboxX = bulletX - CheckboxSize * 0.5f + 1;

            if (hasCheckbox)
                DrawTodoCheckbox(ctx, checkboxX, y + (rowH - CheckboxSize) * 0.5f, todo == TodoState.Done, color);
            else
            {
                var brush = line.HasChildren && line.IsCollapsed
                    ? ctx.GetBrush(WpfColor.FromArgb(210, 94, 158, 211))
                    : bulletBrush;
                float radius = line.HasChildren && line.IsCollapsed ? BulletRadius + 0.7f : BulletRadius;
                if (line.HasChildren)
                    DrawCollapseChevron(ctx, bulletX - 13f, y + rowH * 0.5f, line.IsCollapsed, CanvasTheme.IsDark
                        ? WpfColor.FromArgb(150, 125, 135, 150)
                        : WpfColor.FromArgb(155, 115, 122, 132));
                ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2(bulletX, y + rowH * 0.5f), radius, radius), brush);
            }

            // Render raw markdown only on the active row so the rest of the page reads cleanly.
            bool hideMarkers = !caretOnLine;
            var (displayText, displaySpans, headingLevel, _) = PrepareDisplayLine(line.Text, hideMarkers);

            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var lineFmt = CreateLineTextFormat(ctx, fontFamily, fontSize, bold, italic, headingLevel);
            using var layout = ctx.DWriteFactory.CreateTextLayout(visibleText, lineFmt, available, MaxTextLayoutHeight);
            ApplyInlineMarkdownSpans(ctx, layout, displaySpans);
            DrawInlineDecorationsSpans(ctx, layout, displaySpans, textX, y, rowH);
            if (todo == TodoState.Done)
            {
                // Dim + strike the whole DONE line so completed tasks visually fade.
                layout.SetStrikethrough(true, new Vortice.DirectWrite.TextRange(0, (uint)displayText.Length));
                layout.SetDrawingEffect(ctx.GetBrush(WpfColor.FromArgb(120, color.R, color.G, color.B)),
                    new Vortice.DirectWrite.TextRange(0, (uint)displayText.Length));
            }

            // Selection highlight on this line, computed in visible-text coordinates
            if (selStart >= 0)
            {
                int lineRawStart = line.Start + line.PrefixLength;
                int selectionLineRawEnd = line.Start + line.Length - line.AnchorLength;
                int lineSelStart = Math.Max(selStart, lineRawStart);
                int lineSelEnd = Math.Min(selEnd, selectionLineRawEnd);
                if (lineSelStart < lineSelEnd)
                {
                    int visStart = Math.Max(0, lineSelStart - lineRawStart);
                    int visEnd = Math.Max(visStart, Math.Min(line.Text.Length, lineSelEnd - lineRawStart));
                    if (visEnd > visStart)
                    {
                        layout.HitTestTextPosition((uint)visStart, false, out float sx, out _, out _);
                        layout.HitTestTextPosition((uint)visEnd, false, out float ex, out _, out _);
                        ctx.RenderTarget.FillRectangle(new RectangleF(textX + sx, y, Math.Max(1, ex - sx), rowH), selBrush);
                    }
                }
            }

            ctx.RenderTarget.DrawTextLayout(new Vector2(textX, y), layout, textBrush, DrawTextOptions.Clip);

            // Caret on the line containing _editCursorPos. We accept any raw position
            // between line.Start and lineRawEnd inclusive so cursor positions inside
            // the prefix (e.g. arrow-keyed past column 0) still render at the start of
            // the visible text instead of disappearing.
            if (hasCursor && editCursorVisible)
            {
                int lineRawStart = line.Start + line.PrefixLength;
                int caretLineRawEnd = line.Start + line.Length - line.AnchorLength;
                if (editCursorPos >= line.Start && editCursorPos <= caretLineRawEnd)
                {
                    int visCol = Math.Max(0, Math.Min(line.Text.Length, editCursorPos - lineRawStart));
                    layout.HitTestTextPosition((uint)visCol, false, out float cxx, out float cyy, out _);
                    float cx = textX + cxx;
                    float cy = y + cyy;
                    float ch = Math.Max(fontSize, rowH * 0.8f);
                    ctx.RenderTarget.DrawLine(new Vector2(cx, cy), new Vector2(cx, cy + ch), caretBrush, ctx.InvStroke(1.2f));
                }
            }

            y += MeasureRowHeight(layout, rowH);
        }

        DrawActiveBranchRail(ctx, activeBranchRows);
    }

    private static HashSet<int> BuildActiveBranchIndexSet(OutlineDocument doc, int currentLineIndex)
    {
        var active = new HashSet<int>();
        if (currentLineIndex < 0 || currentLineIndex >= doc.Lines.Count)
            return active;

        int index = currentLineIndex;
        while (index >= 0 && index < doc.Lines.Count)
        {
            var line = doc.Lines[index];
            if (line.IsHidden) break;
            active.Add(line.Index);
            index = line.ParentIndex;
        }
        return active;
    }

    private static IReadOnlyList<ActiveBranchRow> LayoutActiveBranchRows(
        DrawingContext ctx,
        OutlineDocument doc,
        Rect content,
        float maxW,
        float rowH,
        Vortice.DirectWrite.IDWriteTextFormat fmt,
        HashSet<int> activeBranch,
        int editCursorPos,
        bool hasCursor)
    {
        var rows = new List<ActiveBranchRow>();
        float x = (float)content.X;
        float y = (float)content.Y;

        foreach (var line in doc.VisibleLines)
        {
            if (y > content.Bottom - 2) break;

            float indent = line.Level * IndentWidth;
            float bulletX = x + indent + 12f;
            float textX = x + indent + 22f;
            float available = Math.Max(8f, maxW - indent - 22f);
            bool isCurrent = editCursorPos >= line.Start && editCursorPos <= line.Start + line.Length - line.AnchorLength;

            if (activeBranch.Contains(line.Index))
                rows.Add(new ActiveBranchRow(bulletX, y + rowH * 0.5f, isCurrent));

            int lineRawEnd = line.Start + line.Length - line.AnchorLength;
            bool caretOnLine = hasCursor && editCursorPos >= line.Start && editCursorPos <= lineRawEnd;
            if (IsDividerText(line.Text) && !caretOnLine)
            {
                y += rowH;
                continue;
            }

            bool hideMarkers = !caretOnLine;
            var (displayText, displaySpans, _, _) = PrepareDisplayLine(line.Text, hideMarkers);
            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var layout = ctx.DWriteFactory.CreateTextLayout(visibleText, fmt, available, MaxTextLayoutHeight);
            ApplyInlineMarkdownSpans(ctx, layout, displaySpans);
            y += MeasureRowHeight(layout, rowH);
        }

        return rows;
    }

    private static void DrawActiveBranchRail(DrawingContext ctx, IReadOnlyList<ActiveBranchRow> rows)
    {
        if (rows.Count == 0) return;

        var accent = CanvasTheme.Accent;
        var stroke = ctx.GetBrush(WpfColor.FromArgb(220, accent.R, accent.G, accent.B));
        var nodeFill = ctx.GetBrush(CanvasTheme.OutlineBg);
        var currentFill = ctx.GetBrush(WpfColor.FromArgb(48, accent.R, accent.G, accent.B));
        float strokeW = ctx.InvStroke(1.35f);

        for (int i = 0; i < rows.Count - 1; i++)
        {
            var from = rows[i];
            var to = rows[i + 1];
            ctx.RenderTarget.DrawLine(new Vector2(from.BulletX, from.BulletY), new Vector2(from.BulletX, to.BulletY), stroke, strokeW);
            if (Math.Abs(to.BulletX - from.BulletX) > 0.1f)
                ctx.RenderTarget.DrawLine(new Vector2(from.BulletX, to.BulletY), new Vector2(to.BulletX, to.BulletY), stroke, strokeW);
        }

        foreach (var row in rows)
        {
            float r = row.IsCurrent ? 3.25f : 2.9f;
            var ellipse = new Ellipse(new Vector2(row.BulletX, row.BulletY), r, r);
            ctx.RenderTarget.FillEllipse(ellipse, row.IsCurrent ? currentFill : nodeFill);
            ctx.RenderTarget.DrawEllipse(ellipse, stroke, ctx.InvStroke(1.15f));
        }
    }

    private static Vortice.DirectWrite.IDWriteTextFormat CreateLineTextFormat(
        DrawingContext ctx,
        string fontFamily,
        float baseFontSize,
        bool bold,
        bool italic,
        int headingLevel)
    {
        float size = headingLevel switch
        {
            1 => baseFontSize * 1.42f,
            2 => baseFontSize * 1.25f,
            3 => baseFontSize * 1.12f,
            4 => baseFontSize * 1.04f,
            _ => baseFontSize
        };

        var weight = headingLevel > 0 || bold
            ? Vortice.DirectWrite.FontWeight.Bold
            : Vortice.DirectWrite.FontWeight.Normal;
        var style = italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal;
        var fmt = ctx.DWriteFactory.CreateTextFormat(
            string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
            weight,
            style,
            Vortice.DirectWrite.FontStretch.Normal,
            size);
        fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;
        return fmt;
    }

    private static float DrawTipBlock(
        DrawingContext ctx,
        IReadOnlyList<OutlineLine> lines,
        int beginIndex,
        int endIndex,
        float x,
        float y,
        float maxW,
        float rowH,
        float fontSize,
        string fontFamily,
        WpfColor color)
    {
        var begin = lines[beginIndex];
        float indent = begin.Level * IndentWidth;
        float iconX = x + indent + 28f;
        float dividerX = x + indent + 68f;
        float textX = x + indent + 84f;
        float available = Math.Max(32f, maxW - indent - 92f);
        float top = y + 7f;
        float textY = y + 6f;
        var muted = CanvasTheme.IsDark
            ? WpfColor.FromRgb(0x86, 0xB5, 0xDD)
            : WpfColor.FromRgb(0x7A, 0xB0, 0xDB);
        var divider = CanvasTheme.IsDark
            ? WpfColor.FromArgb(120, 74, 83, 96)
            : WpfColor.FromArgb(160, 224, 224, 224);

        using var fmt = CreateLineTextFormat(ctx, fontFamily, fontSize, bold: false, italic: false, headingLevel: 0);
        int firstContent = Math.Min(beginIndex + 1, lines.Count);
        int lastContentExclusive = Math.Clamp(endIndex, firstContent, lines.Count);
        if (firstContent == lastContentExclusive)
            lastContentExclusive = Math.Min(beginIndex + 1, lines.Count);

        for (int i = firstContent; i < lastContentExclusive; i++)
        {
            if (IsTipEnd(lines[i].Text)) break;
            var (displayText, spans, _, _) = PrepareDisplayLine(lines[i].Text, hideMarkers: true);
            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var layout = ctx.DWriteFactory.CreateTextLayout(visibleText, fmt, available, MaxTextLayoutHeight);
            ApplyInlineMarkdownSpans(ctx, layout, spans);
            float lineH = MeasureRowHeight(layout, rowH);
            DrawInlineDecorationsSpans(ctx, layout, spans, textX, textY, rowH);
            ctx.RenderTarget.DrawTextLayout(new Vector2(textX, textY), layout, ctx.GetBrush(color), DrawTextOptions.Clip);
            textY += lineH;
        }

        float bottom = Math.Max(textY + 3f, y + rowH * 2f);
        ctx.RenderTarget.DrawLine(new Vector2(dividerX, top), new Vector2(dividerX, bottom - 7f),
            ctx.GetBrush(divider), ctx.InvStroke(1f));
        DrawTipIcon(ctx, iconX, y + 18f, muted);
        return bottom - y + 4f;
    }

    private static void DrawTipIcon(DrawingContext ctx, float x, float y, WpfColor color)
    {
        var brush = ctx.GetBrush(color);
        ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2(x, y), 9f, 9f), brush);
        ctx.RenderTarget.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x - 4f, y + 7f, 8f, 6f), 2f, 2f), brush);
        var bg = ctx.GetBrush(CanvasTheme.OutlineBg);
        ctx.RenderTarget.DrawLine(new Vector2(x - 3f, y + 13f), new Vector2(x + 3f, y + 13f), bg, ctx.InvStroke(1f));
        ctx.RenderTarget.DrawLine(new Vector2(x - 2f, y - 1f), new Vector2(x - 4.5f, y + 2.5f), bg, ctx.InvStroke(1.5f));
        ctx.RenderTarget.DrawLine(new Vector2(x - 4.5f, y + 2.5f), new Vector2(x - 2f, y + 4.8f), bg, ctx.InvStroke(1.5f));
    }

    private static void DrawCollapseChevron(DrawingContext ctx, float x, float y, bool collapsed, WpfColor color)
    {
        var brush = ctx.GetBrush(color);
        if (collapsed)
        {
            ctx.RenderTarget.DrawLine(new Vector2(x - 2.2f, y - 4f), new Vector2(x + 2.2f, y), brush, ctx.InvStroke(1.25f));
            ctx.RenderTarget.DrawLine(new Vector2(x + 2.2f, y), new Vector2(x - 2.2f, y + 4f), brush, ctx.InvStroke(1.25f));
        }
        else
        {
            ctx.RenderTarget.DrawLine(new Vector2(x - 4f, y - 2.2f), new Vector2(x, y + 2.2f), brush, ctx.InvStroke(1.25f));
            ctx.RenderTarget.DrawLine(new Vector2(x, y + 2.2f), new Vector2(x + 4f, y - 2.2f), brush, ctx.InvStroke(1.25f));
        }
    }

    private static bool IsTipBegin(string text) =>
        string.Equals(text.Trim(), "#+BEGIN_TIP", StringComparison.OrdinalIgnoreCase);

    private static bool IsTipEnd(string text) =>
        string.Equals(text.Trim(), "#+END_TIP", StringComparison.OrdinalIgnoreCase);

    private static int FindTipEnd(IReadOnlyList<OutlineLine> lines, int beginIndex)
    {
        for (int i = beginIndex + 1; i < lines.Count; i++)
            if (IsTipEnd(lines[i].Text))
                return i;
        return beginIndex;
    }

    private static bool IsCaretInLineRange(IReadOnlyList<OutlineLine> lines, int beginIndex, int endIndex, int caret)
    {
        if (caret < 0) return false;
        int last = Math.Clamp(endIndex, beginIndex, lines.Count - 1);
        for (int i = beginIndex; i <= last; i++)
        {
            int end = lines[i].Start + lines[i].Length - lines[i].AnchorLength;
            if (caret >= lines[i].Start && caret <= end)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Translate a world-space click point inside an outline block into a raw-text
    /// cursor position. Mirrors the layout used by Draw.
    /// </summary>
    public static int HitTestPoint(
        RenderBlock block,
        Rect content,
        string text,
        float fontSize,
        string fontFamily,
        bool bold,
        bool italic,
        WpfPoint world,
        Vortice.DirectWrite.IDWriteFactory dwrite)
    {
        var doc = Parse(text, block.Style);
        float x = (float)content.X;
        float y = (float)content.Y;
        float maxW = (float)content.Width;
        float rowH = Math.Max(18f, fontSize * 1.5f);
        using var fmt = dwrite.CreateTextFormat(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
            bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
            italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
            Vortice.DirectWrite.FontStretch.Normal,
            fontSize);
        fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;

        OutlineLine? lastLine = null;

        foreach (var line in doc.VisibleLines)
        {
            float indent = line.Level * IndentWidth;
            float textX = x + indent + 22f;
            float available = Math.Max(8f, maxW - indent - 22f);

            var (displayText, displaySpans, _, rawDisplayStart) = PrepareDisplayLine(line.Text, hideMarkers: true);
            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var layout = dwrite.CreateTextLayout(visibleText, fmt, available, MaxTextLayoutHeight);
            // Apply inline markdown styling so bold/italic/code glyph widths match the rendered layout.
            ApplyInlineMarkdownSpans(null, layout, displaySpans);
            float drawn = MeasureRowHeight(layout, rowH);

            if (world.Y >= y && world.Y < y + drawn)
            {
                SharpGen.Runtime.RawBool isTrailing = false; SharpGen.Runtime.RawBool isInside = false;
                var hit = layout.HitTestPoint((float)world.X - textX, (float)world.Y - y, out isTrailing, out isInside);
                int visCol = Math.Clamp((int)hit.TextPosition + (isTrailing ? 1 : 0), 0, displayText.Length);
                return line.Start + line.PrefixLength + rawDisplayStart + visCol;
            }

            lastLine = line;
            y += drawn;
        }

        // Below the last visible line — put cursor at the end of the last line if any.
        if (lastLine is not null)
            return lastLine.Start + lastLine.Length - lastLine.AnchorLength;
        return text.Length;
    }

    /// <summary>
    /// Apply inline-markdown styling to the given DirectWrite layout. <paramref name="spans"/>
    /// must reference positions inside the text that was used to create the layout — for
    /// edit mode that's raw line.Text; for view mode that's the cleaned text returned by
    /// <see cref="BuildDisplay"/>.
    /// </summary>
    private static void ApplyInlineMarkdownSpans(DrawingContext? ctx, Vortice.DirectWrite.IDWriteTextLayout layout, IReadOnlyList<InlineSpan> spans)
    {
        if (spans.Count == 0) return;
        foreach (var span in spans)
        {
            var contentRange = new Vortice.DirectWrite.TextRange((uint)span.ContentStart, (uint)span.ContentLength);
            switch (span.Kind)
            {
                case InlineKind.Bold:
                    layout.SetFontWeight(Vortice.DirectWrite.FontWeight.Bold, contentRange);
                    break;
                case InlineKind.Italic:
                    layout.SetFontStyle(Vortice.DirectWrite.FontStyle.Italic, contentRange);
                    break;
                case InlineKind.Strike:
                    layout.SetStrikethrough(true, contentRange);
                    break;
                case InlineKind.Code:
                    layout.SetFontFamilyName("Consolas", contentRange);
                    if (ctx is not null)
                        layout.SetDrawingEffect(ctx.GetBrush(CanvasTheme.IsDark
                            ? WpfColor.FromRgb(0xD7, 0xDA, 0xE0)
                            : WpfColor.FromRgb(0x45, 0x45, 0x45)), contentRange);
                    break;
                case InlineKind.Ref:
                case InlineKind.MarkdownLink:
                    if (ctx is not null)
                        layout.SetDrawingEffect(ctx.GetBrush(WpfColor.FromRgb(0x2D, 0x7D, 0xB8)),
                            new Vortice.DirectWrite.TextRange((uint)span.Start, (uint)span.Length));
                    break;
                case InlineKind.Tag:
                    if (ctx is not null)
                        layout.SetDrawingEffect(ctx.GetBrush(WpfColor.FromRgb(0x4F, 0x8F, 0xC7)),
                            new Vortice.DirectWrite.TextRange((uint)span.Start, (uint)span.Length));
                    break;
                case InlineKind.BlockRef:
                    if (ctx is not null)
                        layout.SetDrawingEffect(ctx.GetBrush(WpfColor.FromRgb(0x2A, 0x9D, 0x8F)),
                            new Vortice.DirectWrite.TextRange((uint)span.Start, (uint)span.Length));
                    break;
            }
        }
    }

    private static float MeasureRowHeight(Vortice.DirectWrite.IDWriteTextLayout layout, float rowH) =>
        Math.Max(rowH, layout.Metrics.Height + 2f);

    /// <summary>
    /// Fill subtle inline backgrounds for keycap/code, highlights, and block refs on a single line.
    /// Called BEFORE the text layout is drawn so the text sits on top of the pill.
    /// </summary>
    private static void DrawInlineDecorationsSpans(
        DrawingContext ctx,
        Vortice.DirectWrite.IDWriteTextLayout layout,
        IReadOnlyList<InlineSpan> spans,
        float textX,
        float lineY,
        float rowH)
    {
        if (spans.Count == 0) return;
        var blockRefBg = ctx.GetBrush(WpfColor.FromArgb(34, 42, 157, 143));
        var highlightBg = ctx.GetBrush(CanvasTheme.IsDark
            ? WpfColor.FromArgb(105, 132, 102, 36)
            : WpfColor.FromArgb(145, 255, 235, 137));
        var codeBg = ctx.GetBrush(CanvasTheme.IsDark
            ? WpfColor.FromArgb(95, 70, 76, 86)
            : WpfColor.FromRgb(0xE7, 0xE7, 0xE7));

        foreach (var span in spans)
        {
            if (span.Kind is not (InlineKind.BlockRef or InlineKind.Code or InlineKind.Highlight)) continue;
            layout.HitTestTextPosition((uint)span.Start, false, out float sx, out _, out _);
            layout.HitTestTextPosition((uint)(span.Start + span.Length), false, out float ex, out _, out _);
            float padX = span.Kind == InlineKind.Code ? 4f : 3f;
            var rect = new RoundedRectangle(
                new RectangleF(textX + sx - padX, lineY + 3f, Math.Max(2, ex - sx) + padX * 2, rowH - 6f),
                3f, 3f);
            var bg = span.Kind switch
            {
                InlineKind.Code => codeBg,
                InlineKind.Highlight => highlightBg,
                _ => blockRefBg,
            };
            ctx.RenderTarget.FillRoundedRectangle(rect, bg);
        }
    }

    /// <summary>
    /// Build the per-line display string used at draw time. When <paramref name="hideMarkers"/>
    /// is true (view mode), strips the **/~~/`/* /==/[[ ]] markers so the user sees clean
    /// formatted text. When false (edit mode), leaves the raw markers in so cursor
    /// positions stay 1:1 with the underlying body. Returns the display string and the
    /// span list with positions translated to the display string's coordinate space.
    /// </summary>
    private static (string DisplayText, List<InlineSpan> Spans) BuildDisplay(string rawLineText, bool hideMarkers)
    {
        var rawSpans = ParseInlineSpans(rawLineText).ToList();
        if (!hideMarkers || rawSpans.Count == 0)
            return (rawLineText, rawSpans);

        var sb = new System.Text.StringBuilder();
        var spans = new List<InlineSpan>();
        int rawPos = 0;
        foreach (var span in rawSpans)
        {
            if (span.Start > rawPos)
                sb.Append(rawLineText, rawPos, span.Start - rawPos);

            int dispStart = sb.Length;
            switch (span.Kind)
            {
                case InlineKind.Bold:
                case InlineKind.Italic:
                case InlineKind.Strike:
                case InlineKind.Code:
                case InlineKind.Highlight:
                case InlineKind.Ref:
                case InlineKind.MarkdownLink:
                case InlineKind.BlockRef:
                    // Drop the marker chars; only keep the inner content.
                    sb.Append(rawLineText, span.ContentStart, span.ContentLength);
                    spans.Add(new InlineSpan(dispStart, span.ContentLength, dispStart, span.ContentLength, span.Kind));
                    break;
                case InlineKind.Tag:
                    // # stays visible as part of the tag; nothing to strip.
                    sb.Append(rawLineText, span.Start, span.Length);
                    spans.Add(new InlineSpan(dispStart, span.Length, dispStart + 1, span.Length - 1, span.Kind));
                    break;
            }
            rawPos = span.Start + span.Length;
        }
        if (rawPos < rawLineText.Length)
            sb.Append(rawLineText, rawPos, rawLineText.Length - rawPos);

        return (sb.ToString(), spans);
    }

    private static (string DisplayText, List<InlineSpan> Spans, int HeadingLevel, int RawDisplayStart) PrepareDisplayLine(string rawLineText, bool hideMarkers)
    {
        if (hideMarkers && TryParseMarkdownHeading(rawLineText, out int headingLevel, out int contentStart))
        {
            var (displayText, spans) = BuildDisplay(rawLineText[contentStart..], hideMarkers);
            return (displayText, spans, headingLevel, contentStart);
        }

        var (plainDisplay, plainSpans) = BuildDisplay(rawLineText, hideMarkers);
        return (plainDisplay, plainSpans, 0, 0);
    }

    private static bool TryParseMarkdownHeading(string text, out int level, out int contentStart)
    {
        level = 0;
        contentStart = 0;
        int i = 0;
        while (i < text.Length && text[i] == '#') i++;
        if (i is < 1 or > 6 || i >= text.Length || text[i] != ' ')
            return false;

        level = Math.Min(i, 4);
        contentStart = i + 1;
        return contentStart < text.Length;
    }

    internal enum InlineKind { Bold, Italic, Strike, Code, Highlight, Tag, Ref, MarkdownLink, BlockRef }
    internal readonly record struct InlineSpan(int Start, int Length, int ContentStart, int ContentLength, InlineKind Kind);

    /// <summary>
    /// Scans line text for non-overlapping inline-markdown spans. The scanner is
    /// intentionally simple: longest marker (**) is tried before short (*), and
    /// matches are taken greedily so we don't recurse into nested markup.
    /// </summary>
    internal static IEnumerable<InlineSpan> ParseInlineSpans(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            // **bold**
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new InlineSpan(i, end + 2 - i, i + 2, end - (i + 2), InlineKind.Bold);
                    i = end + 2;
                    continue;
                }
            }
            // *italic* — only when not adjacent to another '*'
            else if (c == '*')
            {
                int end = FindUnpairedMarker(text, '*', i + 1);
                if (end > i + 1)
                {
                    yield return new InlineSpan(i, end + 1 - i, i + 1, end - (i + 1), InlineKind.Italic);
                    i = end + 1;
                    continue;
                }
            }
            // ~~strike~~
            else if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new InlineSpan(i, end + 2 - i, i + 2, end - (i + 2), InlineKind.Strike);
                    i = end + 2;
                    continue;
                }
            }
            // ==highlight==
            else if (c == '=' && i + 1 < text.Length && text[i + 1] == '=')
            {
                int end = text.IndexOf("==", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new InlineSpan(i, end + 2 - i, i + 2, end - (i + 2), InlineKind.Highlight);
                    i = end + 2;
                    continue;
                }
            }
            // `code`
            else if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    yield return new InlineSpan(i, end + 1 - i, i + 1, end - (i + 1), InlineKind.Code);
                    i = end + 1;
                    continue;
                }
            }
            // #tag (no whitespace, no closing marker)
            else if (c == '#' && i + 1 < text.Length && IsTagChar(text[i + 1]))
            {
                int end = i + 1;
                while (end < text.Length && IsTagChar(text[end])) end++;
                yield return new InlineSpan(i, end - i, i + 1, end - (i + 1), InlineKind.Tag);
                i = end;
                continue;
            }
            // [[Card Title]] — wikilink
            else if (c == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int end = text.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new InlineSpan(i, end + 2 - i, i + 2, end - (i + 2), InlineKind.Ref);
                    i = end + 2;
                    continue;
                }
            }
            // [label](url)
            else if (c == '[')
            {
                int closeLabel = text.IndexOf(']', i + 1);
                if (closeLabel > i + 1
                    && closeLabel + 1 < text.Length
                    && text[closeLabel + 1] == '(')
                {
                    int closeUrl = text.IndexOf(')', closeLabel + 2);
                    if (closeUrl > closeLabel + 2)
                    {
                        yield return new InlineSpan(i, closeUrl + 1 - i, i + 1, closeLabel - (i + 1), InlineKind.MarkdownLink);
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }
            // ((^anchor)) — block reference (transclusion source)
            else if (c == '(' && i + 1 < text.Length && text[i + 1] == '(')
            {
                int end = text.IndexOf("))", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new InlineSpan(i, end + 2 - i, i + 2, end - (i + 2), InlineKind.BlockRef);
                    i = end + 2;
                    continue;
                }
            }
            i++;
        }
    }

    /// <summary>Public scan-only API: returns all tag and ref spans across the entire
    /// outline body so other parts of the app (filters, indexes) can use them.</summary>
    public static IEnumerable<(int RawStart, int RawLength, string Inner, bool IsRef)> ScanTagsAndRefs(string body)
    {
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int lineStart = 0;
        foreach (var lineText in body.Split('\n'))
        {
            int prefix = PrefixLengthForLine(lineText);
            string visible = prefix <= lineText.Length ? lineText[prefix..] : string.Empty;
            int visibleStartRaw = lineStart + prefix;
            foreach (var span in ParseInlineSpans(visible))
            {
                if (span.Kind is InlineKind.Tag or InlineKind.Ref)
                {
                    string inner = visible.Substring(span.ContentStart, span.ContentLength);
                    yield return (visibleStartRaw + span.Start, span.Length, inner, span.Kind == InlineKind.Ref);
                }
            }
            lineStart += lineText.Length + 1; // +1 for the \n
        }
    }

    private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/';

    /// <summary>
    /// Find the next standalone '*' (not part of '**') in <paramref name="text"/>
    /// at-or-after <paramref name="from"/>. Returns -1 if none.
    /// </summary>
    private static int FindUnpairedMarker(string text, char marker, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != marker) continue;
            // skip doubled markers (those belong to ** sequences)
            if (i + 1 < text.Length && text[i + 1] == marker) { i++; continue; }
            if (i - 1 >= from && text[i - 1] == marker) continue;
            return i;
        }
        return -1;
    }

    private static void DrawTodoCheckbox(DrawingContext ctx, float x, float y, bool done, WpfColor color)
    {
        var stroke = ctx.GetBrush(done
            ? WpfColor.FromArgb(220, 22, 163, 74)   // green for DONE
            : WpfColor.FromArgb(220, color.R, color.G, color.B));
        var fill = ctx.GetBrush(done
            ? WpfColor.FromArgb(60, 22, 163, 74)
            : WpfColor.FromArgb(40, color.R, color.G, color.B));
        var rect = new RoundedRectangle(new RectangleF(x, y, CheckboxSize, CheckboxSize), 2.5f, 2.5f);
        ctx.RenderTarget.FillRoundedRectangle(rect, fill);
        ctx.RenderTarget.DrawRoundedRectangle(rect, stroke, ctx.InvStroke(1.0f));
        if (done)
        {
            // Checkmark: two short strokes inside the box.
            var p1 = new Vector2(x + 2.4f, y + CheckboxSize * 0.55f);
            var p2 = new Vector2(x + CheckboxSize * 0.45f, y + CheckboxSize - 2.5f);
            var p3 = new Vector2(x + CheckboxSize - 2.0f, y + 2.8f);
            ctx.RenderTarget.DrawLine(p1, p2, stroke, ctx.InvStroke(1.6f));
            ctx.RenderTarget.DrawLine(p2, p3, stroke, ctx.InvStroke(1.6f));
        }
    }

    /// <summary>
    /// Hit-test a click against TODO/DONE checkbox glyphs. Returns the 0-based raw
    /// line index of the bullet that owns the checkbox, or -1 on miss.
    /// </summary>
    public static int TryHitTodoCheckbox(RenderBlock block, Rect bounds, WpfPoint world,
        Vortice.DirectWrite.IDWriteFactory? dwrite = null)
    {
        if (!IsOutlineBlock(block)) return -1;

        var style = block.Style ?? new BoardItemStyle();
        var doc = Parse(block.Body ?? string.Empty, style);
        var content = GetContentRect(block, bounds);
        float fontSize = block.Kind == BlockKind.Note ? Math.Clamp((float)style.FontSize, 8f, 48f) : Math.Clamp((float)style.FontSize, 8f, 96f);
        float rowH = Math.Max(18f, fontSize * 1.5f);
        float maxW = (float)content.Width;

        Vortice.DirectWrite.IDWriteTextFormat? fmt = null;
        if (dwrite is not null)
        {
            string fontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily!;
            fmt = dwrite.CreateTextFormat(fontFamily,
                style.Bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
                style.Italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
                Vortice.DirectWrite.FontStretch.Normal, fontSize);
            fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;
        }

        float y = (float)content.Y;
        try
        {
            foreach (var line in doc.VisibleLines)
            {
                if (y > content.Bottom) break;
                float indent = line.Level * IndentWidth;
                if (ClassifyTodo(line.Text) != TodoState.None)
                {
                    // Mirror Draw's column layout so the hit area lands on the actual
                    // checkbox glyph at the bullet position.
                    float bulletX = (float)content.X + indent + 12f;
                    float checkboxX = bulletX - CheckboxSize * 0.5f + 1;
                    var hit = new Rect(checkboxX - 4, y, CheckboxSize + 8, rowH);
                    if (hit.Contains(world))
                        return line.Index;
                }
                float drawn = rowH;
                if (fmt is not null)
                {
                    float available = Math.Max(8f, maxW - indent - 22f);
                    using var layout = dwrite!.CreateTextLayout(
                        string.IsNullOrWhiteSpace(line.Text) ? " " : line.Text, fmt, available, MaxTextLayoutHeight);
                    drawn = MeasureRowHeight(layout, rowH);
                }
                y += drawn;
            }
        }
        finally
        {
            fmt?.Dispose();
        }
        return -1;
    }

    /// <summary>
    /// Toggle the TODO ↔ DONE prefix on the raw body line at <paramref name="lineIndex"/>.
    /// Returns the rewritten body, or the original if the line doesn't qualify.
    /// </summary>
    public static string ToggleTodoLine(string body, int lineIndex)
    {
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = body.Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return body;

        string raw = lines[lineIndex];
        int prefix = PrefixLengthForLine(raw);
        string head = raw[..prefix];
        string rest = prefix <= raw.Length ? raw[prefix..] : string.Empty;

        if (rest.StartsWith("TODO ", StringComparison.Ordinal))
            rest = "DONE " + rest[5..];
        else if (rest.StartsWith("DONE ", StringComparison.Ordinal))
            rest = "TODO " + rest[5..];
        else if (string.Equals(rest, "TODO", StringComparison.Ordinal))
            rest = "DONE";
        else if (string.Equals(rest, "DONE", StringComparison.Ordinal))
            rest = "TODO";
        else
            return body;

        lines[lineIndex] = head + rest;
        return string.Join('\n', lines);
    }

    // -----------------------------------------------------------------------
    // Bullet anchor-ID helpers (for bullet-to-block connections)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the 8-hex-char anchor ID if the raw line text ends with " ^xxxxxxxx",
    /// otherwise returns null.
    /// </summary>
    public static string? ParseBulletAnchorId(string rawLineText)
    {
        // Format: " ^12345678" = 10 chars (space, caret, 8 hex digits)
        if (rawLineText.Length < 10) return null;
        int anchorStart = rawLineText.Length - 10;
        if (rawLineText[anchorStart] != ' ' || rawLineText[anchorStart + 1] != '^') return null;
        string hex = rawLineText[(anchorStart + 2)..];
        if (hex.Length != 8) return null;
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return null;
        return hex;
    }

    /// <summary>
    /// Ensures the raw body line at <paramref name="lineIndex"/> has a " ^xxxxxxxx"
    /// anchor ID suffix. Returns the (potentially updated) body and the bullet's anchor ID.
    /// </summary>
    public static (string NewBody, string AnchorId) GetOrAllocateBulletAnchorId(string body, int lineIndex)
    {
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = body.Split('\n').ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count)
            return (body, GenerateAnchorId());

        string line = lines[lineIndex];
        // Check if the display part (after prefix) already has a ^id
        int prefix = PrefixLengthForLine(line);
        string display = prefix <= line.Length ? line[prefix..] : string.Empty;
        string? existing = ParseBulletAnchorId(display);
        if (existing is not null)
            return (body, existing);

        string newId = GenerateAnchorId();
        lines[lineIndex] = line + " ^" + newId;
        return (string.Join('\n', lines), newId);
    }

    public static string GenerateAnchorIdPublic() =>
        Guid.NewGuid().ToString("N")[..8];

    private static string GenerateAnchorId() =>
        GenerateAnchorIdPublic();

    /// <summary>
    /// Returns the font size for an outline block, mirroring BlockRenderer's AutoFontSize logic.
    /// </summary>
    public static float GetFontSize(RenderBlock block)
    {
        var style = block.Style ?? new BoardItemStyle();
        if (block.Kind == BlockKind.Note)
            return Math.Clamp((float)style.FontSize, 8f, 48f);
        float baseFont = Math.Clamp((float)style.FontSize, 8f, 96f);
        if (!style.AutoFontSize || string.IsNullOrEmpty(block.Body))
            return baseFont;
        float lines = Math.Max(1, (block.Body ?? "").Split('\n').Length);
        return (float)Math.Clamp((block.Height - 8) / (lines * 1.4f), 6f, 96f);
    }

    /// <summary>
    /// Per-visible-line layout geometry for an outline block, measured exactly the way
    /// <see cref="Draw"/> lays rows out (BuildDisplay + MeasureRowHeight, accumulating the
    /// drawn height of each row). All connection-anchor consumers (hit-test, render, and
    /// endpoint resolution) share this so they stay pixel-aligned with the rendered text
    /// and don't drift apart as rows wrap or markers are hidden.
    /// </summary>
    private readonly record struct ActiveBranchRow(float BulletX, float BulletY, bool IsCurrent);

    internal readonly record struct BulletRow(
        OutlineLine Line, float RowTop, float RowHeight, float Indent, float BulletMidY);

    internal static IReadOnlyList<BulletRow> LayoutBulletRows(
        RenderBlock block, Rect bounds, bool hideMarkers,
        Vortice.DirectWrite.IDWriteFactory dwrite)
    {
        var rows = new List<BulletRow>();
        if (!IsOutlineBlock(block)) return rows;

        var style = block.Style ?? new BoardItemStyle();
        var doc = Parse(block.Body ?? string.Empty, style);
        var content = GetContentRect(block, bounds);
        float fontSize = GetFontSize(block);
        float rowH = Math.Max(16f, fontSize * 1.45f);
        float maxW = (float)content.Width;

        string fontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily!;
        using var fmt = dwrite.CreateTextFormat(fontFamily,
            style.Bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
            style.Italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
            Vortice.DirectWrite.FontStretch.Normal, fontSize);
        fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;

        float y = (float)content.Y;
        foreach (var line in doc.VisibleLines)
        {
            float indent = line.Level * IndentWidth;
            float available = Math.Max(8f, maxW - indent - 22f);

            var (displayText, _, _, _) = PrepareDisplayLine(line.Text, hideMarkers);
            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var layout = dwrite.CreateTextLayout(visibleText, fmt, available, MaxTextLayoutHeight);
            float drawn = MeasureRowHeight(layout, rowH);

            // The bullet glyph is drawn at the top sub-row's vertical centre (y + rowH/2),
            // not the centre of the whole wrapped row — match that so anchors sit on the bullet.
            float bulletMidY = y + rowH * 0.5f;
            rows.Add(new BulletRow(line, y, drawn, indent, bulletMidY));
            y += drawn;
        }

        return rows;
    }

    /// <summary>
    /// Hit-tests whether <paramref name="world"/> is near the connection anchor of any
    /// visible line in an outline block. Returns the line's index within the raw body and
    /// the world-space point that should be used as the connection endpoint: the block's
    /// LEFT edge (fixed X), vertically aligned with the bullet's row.
    /// </summary>
    public static (int LineIndex, WpfPoint ConnectionPoint)? TryHitBullet(
        RenderBlock block, Rect bounds, WpfPoint world,
        Vortice.DirectWrite.IDWriteFactory? dwrite = null)
    {
        if (!IsOutlineBlock(block) || dwrite is null) return null;

        // Only hit-test if the world point is inside (or within a few px of) the block.
        double hitExpand = 12;
        if (world.X < bounds.Left - hitExpand || world.X > bounds.Right + hitExpand
         || world.Y < bounds.Top - hitExpand || world.Y > bounds.Bottom + hitExpand)
            return null;

        // The block isn't being edited when hit-testing for a new connection drag, so
        // markers are hidden — measure the same way the view renders them.
        var rows = LayoutBulletRows(block, bounds, hideMarkers: true, dwrite);
        float anchorX = (float)bounds.Left;
        const double hitRadius = 12.0;
        foreach (var row in rows)
        {
            double dx = world.X - anchorX;
            double dy = world.Y - row.BulletMidY;
            if (dx * dx + dy * dy <= hitRadius * hitRadius)
                return (row.Line.Index, new WpfPoint(anchorX, row.BulletMidY));
        }
        return null;
    }

    /// <summary>
    /// Returns the world-space point for a connection attached to the bullet whose
    /// anchor ID is <paramref name="anchorId"/>: the block's LEFT edge (fixed X) aligned
    /// vertically with the bullet's row. Falls back to null if the ID cannot be found
    /// (e.g. block body was edited after the connection was created).
    /// </summary>
    public static WpfPoint? GetBulletConnectionPoint(
        RenderBlock block, Rect bounds, string anchorId, bool hideMarkers,
        Vortice.DirectWrite.IDWriteFactory dwrite)
    {
        if (!IsOutlineBlock(block)) return null;

        var rows = LayoutBulletRows(block, bounds, hideMarkers, dwrite);
        float anchorX = (float)bounds.Left;
        foreach (var row in rows)
        {
            if (string.Equals(row.Line.AnchorId, anchorId, StringComparison.Ordinal))
                return new WpfPoint(anchorX, row.BulletMidY);
        }
        return null;
    }

    /// <summary>
    /// If <paramref name="rawPos"/> falls inside a line's bullet prefix area
    /// (indent + "- "), move it to a position the user can actually see the caret at:
    /// direction &gt;= 0 ⇒ start of that line's visible content (PrefixEnd);
    /// direction &lt; 0  ⇒ end of the previous visible line, or PrefixEnd if none.
    /// </summary>
    public static int SnapToVisible(string text, BoardItemStyle? style, int rawPos, int direction)
    {
        var doc = Parse(text, style);
        if (doc.Lines.Count == 0) return rawPos;

        for (int i = 0; i < doc.Lines.Count; i++)
        {
            var line = doc.Lines[i];
            int lineEnd = line.Start + line.Length;
            // The position right after the last char (before \n) belongs to this line.
            // Beyond that, advance to the next line.
            if (rawPos < line.Start) continue;
            if (rawPos > lineEnd) continue;

            int prefixEnd = line.Start + line.PrefixLength;
            if (rawPos >= prefixEnd) return rawPos; // already in visible content

            if (direction < 0)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    var prev = doc.Lines[j];
                    if (prev.IsHidden) continue;
                    return prev.Start + prev.Length - prev.AnchorLength;
                }
                return prefixEnd;
            }
            return prefixEnd;
        }
        return rawPos;
    }

    public static int LineIndexAt(string text, int pos)
    {
        pos = Math.Clamp(pos, 0, text.Length);
        int index = 0;
        for (int i = 0; i < pos; i++)
            if (text[i] == '\n') index++;
        return index;
    }

    public static (int Start, int End, string Text) LineAt(string text, int pos)
    {
        pos = Math.Clamp(pos, 0, text.Length);
        int start = pos;
        while (start > 0 && text[start - 1] != '\n') start--;
        int end = pos;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end, text[start..end]);
    }

    public static int PrefixLengthForLine(string line)
    {
        int indent = line.TakeWhile(c => c is ' ' or '\t').Count();
        string rest = line[indent..];
        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal) || rest.StartsWith("\u2022 ", StringComparison.Ordinal))
            return indent + 2;
        return indent;
    }

    private static OutlineLineBuilder ParseLine(int index, int start, string line)
    {
        int indentChars = 0;
        int level = 0;
        foreach (char c in line)
        {
            if (c == '\t') { indentChars++; level++; }
            else if (c == ' ') indentChars++;
            else break;
        }
        level += line.TakeWhile(c => c == ' ').Count() / 2;
        string rest = line[indentChars..];
        int bulletLen = 0;
        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal) || rest.StartsWith("\u2022 ", StringComparison.Ordinal))
            bulletLen = 2;
        string display = bulletLen > 0 ? rest[bulletLen..] : rest;

        // Strip the ` ^xxxxxxxx` bullet anchor-ID suffix if present.
        string? anchorId = ParseBulletAnchorId(display);
        int anchorLength = 0;
        if (anchorId is not null)
        {
            anchorLength = 10; // space + '^' + 8 hex chars
            display = display[..^anchorLength];
        }

        string id = StableId(index, level, display.Trim());
        return new OutlineLineBuilder(index, start, line.Length, Math.Max(0, level), indentChars + bulletLen, display, id, anchorId, anchorLength);
    }

    private static string StableId(int index, int level, string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            string key = $"{level}|{index}|{text}";
            foreach (char c in key)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    private sealed record OutlineLineBuilder(int Index, int Start, int Length, int Level, int PrefixLength, string Text, string Id, string? AnchorId = null, int AnchorLength = 0);
}
