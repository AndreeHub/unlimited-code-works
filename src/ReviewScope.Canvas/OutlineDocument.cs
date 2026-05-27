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
    bool IsHidden);

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
    private const float BulletRadius = 2.4f;
    private const float ToggleSize = 9f;
    private const float CheckboxSize = 11f;

    /// <summary>State of a TODO-style line: nothing, open, or done.</summary>
    public enum TodoState { None, Todo, Done }

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
        block.Kind is BlockKind.Note or BlockKind.Text;

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
                if (collapsed.Contains(raw[parent].Id))
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
            collapsed.Contains(line.Id),
            hidden[i])).ToList());
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

    public static bool TryHitToggle(RenderBlock block, Rect bounds, WpfPoint world, out string lineId)
    {
        lineId = string.Empty;
        if (!IsOutlineBlock(block))
            return false;

        var style = block.Style ?? new BoardItemStyle();
        var doc = Parse(block.Body ?? string.Empty, style);
        var content = GetContentRect(block, bounds);
        float fontSize = block.Kind == BlockKind.Note ? Math.Clamp((float)style.FontSize, 8f, 48f) : Math.Clamp((float)style.FontSize, 8f, 96f);
        float rowH = Math.Max(16f, fontSize * 1.45f);
        float y = (float)content.Y;
        foreach (var line in doc.VisibleLines)
        {
            if (y > content.Bottom) break;
            float toggleX = (float)content.X + line.Level * IndentWidth + 1f;
            var hit = new Rect(toggleX - 3, y + (rowH - ToggleSize) * 0.5f - 3, ToggleSize + 6, ToggleSize + 6);
            if (line.HasChildren && hit.Contains(world))
            {
                lineId = line.Id;
                return true;
            }
            y += rowH;
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
        float rowH = Math.Max(16f, fontSize * 1.45f);
        var guideBrush = ctx.GetBrush(WpfColor.FromArgb(70, color.R, color.G, color.B));
        var bulletBrush = ctx.GetBrush(WpfColor.FromArgb(150, color.R, color.G, color.B));
        var textBrush = ctx.GetBrush(color);
        var caretBrush = ctx.GetBrush(color);
        var selBrush = ctx.GetBrush(WpfColor.FromArgb(80, 46, 125, 215));
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

        foreach (var line in doc.VisibleLines)
        {
            if (y > content.Bottom - 2) break;
            float indent = line.Level * IndentWidth;
            float toggleX = x + indent + 1f;
            float bulletX = x + indent + 12f;
            float textX = x + indent + 22f;
            float available = Math.Max(8f, maxW - indent - 22f);

            for (int level = 1; level <= line.Level; level++)
            {
                float guideX = x + level * IndentWidth - 7f;
                ctx.RenderTarget.DrawLine(new Vector2(guideX, y - 1), new Vector2(guideX, Math.Min(y + rowH, (float)content.Bottom)), guideBrush, ctx.InvStroke(0.75f));
            }

            var todo = ClassifyTodo(line.Text);
            bool hasToggle = line.HasChildren;
            bool hasCheckbox = todo != TodoState.None;

            // When a row needs BOTH the collapse toggle and a TODO/DONE checkbox, the
            // checkbox slides right of the toggle and the text shifts to match so the
            // two glyphs don't stack on top of each other.
            float checkboxX = bulletX - CheckboxSize * 0.5f + 1;
            if (hasToggle && hasCheckbox)
            {
                checkboxX = toggleX + ToggleSize + 2;
                float overflow = (checkboxX + CheckboxSize + 3) - textX;
                if (overflow > 0)
                {
                    textX += overflow;
                    available = Math.Max(8f, available - overflow);
                }
            }

            if (hasToggle)
                DrawToggle(ctx, toggleX, y + (rowH - ToggleSize) * 0.5f, line.IsCollapsed, color);
            if (hasCheckbox)
                DrawTodoCheckbox(ctx, checkboxX, y + (rowH - CheckboxSize) * 0.5f, todo == TodoState.Done, color);
            else if (!hasToggle)
                ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2(bulletX, y + rowH * 0.5f), BulletRadius, BulletRadius), bulletBrush);

            // When the cursor is in this block, render raw text so cursor positions
            // and the markers themselves stay visible for editing. Otherwise strip
            // the markdown markers so the user sees clean formatted text.
            bool hideMarkers = !hasCursor;
            var (displayText, displaySpans) = BuildDisplay(line.Text, hideMarkers);

            string visibleText = string.IsNullOrWhiteSpace(displayText) ? " " : displayText;
            using var layout = ctx.DWriteFactory.CreateTextLayout(visibleText, fmt, available, rowH * 2.4f);
            ApplyInlineMarkdownSpans(layout, displaySpans);
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
                int lineRawEnd = line.Start + line.Length;
                int lineSelStart = Math.Max(selStart, lineRawStart);
                int lineSelEnd = Math.Min(selEnd, lineRawEnd);
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
                int lineRawEnd = line.Start + line.Length;
                if (editCursorPos >= line.Start && editCursorPos <= lineRawEnd)
                {
                    int visCol = Math.Max(0, Math.Min(line.Text.Length, editCursorPos - lineRawStart));
                    layout.HitTestTextPosition((uint)visCol, false, out float cxx, out float cyy, out _);
                    float cx = textX + cxx;
                    float cy = y + cyy;
                    float ch = Math.Max(fontSize, rowH * 0.8f);
                    ctx.RenderTarget.DrawLine(new Vector2(cx, cy), new Vector2(cx, cy + ch), caretBrush, ctx.InvStroke(1.2f));
                }
            }

            y += Math.Max(rowH, Math.Min(rowH * 2.4f, layout.Metrics.Height + 2f));
        }
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
        float rowH = Math.Max(16f, fontSize * 1.45f);
        using var fmt = dwrite.CreateTextFormat(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
            bold ? Vortice.DirectWrite.FontWeight.Bold : Vortice.DirectWrite.FontWeight.Normal,
            italic ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
            Vortice.DirectWrite.FontStretch.Normal,
            fontSize);
        fmt.WordWrapping = Vortice.DirectWrite.WordWrapping.Wrap;

        OutlineLine? lastLine = null;
        float lastTextX = x, lastY = y;
        float lastAvailable = Math.Max(8f, maxW);
        float lastHeight = rowH;

        foreach (var line in doc.VisibleLines)
        {
            float indent = line.Level * IndentWidth;
            float textX = x + indent + 22f;
            float available = Math.Max(8f, maxW - indent - 22f);
            using var layout = dwrite.CreateTextLayout(string.IsNullOrWhiteSpace(line.Text) ? " " : line.Text, fmt, available, rowH * 2.4f);
            float drawn = Math.Max(rowH, Math.Min(rowH * 2.4f, layout.Metrics.Height + 2f));

            if (world.Y >= y && world.Y < y + drawn)
            {
                SharpGen.Runtime.RawBool isTrailing = false; SharpGen.Runtime.RawBool isInside = false;
                var hit = layout.HitTestPoint((float)world.X - textX, (float)world.Y - y, out isTrailing, out isInside);
                int visCol = Math.Clamp((int)hit.TextPosition + (isTrailing ? 1 : 0), 0, line.Text.Length);
                return line.Start + line.PrefixLength + visCol;
            }

            lastLine = line;
            lastTextX = textX;
            lastY = y;
            lastAvailable = available;
            lastHeight = drawn;
            y += drawn;
        }

        // Below the last visible line — put cursor at the end of the last line if any,
        // otherwise at end of text.
        if (lastLine is not null)
            return lastLine.Start + lastLine.Length;
        return text.Length;
    }

    /// <summary>
    /// Apply inline-markdown styling to the given DirectWrite layout. <paramref name="spans"/>
    /// must reference positions inside the text that was used to create the layout — for
    /// edit mode that's raw line.Text; for view mode that's the cleaned text returned by
    /// <see cref="BuildDisplay"/>.
    /// </summary>
    private static void ApplyInlineMarkdownSpans(Vortice.DirectWrite.IDWriteTextLayout layout, IReadOnlyList<InlineSpan> spans)
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
                    break;
                case InlineKind.Ref:
                    layout.SetUnderline(true, new Vortice.DirectWrite.TextRange((uint)span.Start, (uint)span.Length));
                    break;
            }
        }
    }

    /// <summary>
    /// Fill the colored pill backgrounds for #tag and [[ref]] spans on a single line.
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
        var tagBg = ctx.GetBrush(WpfColor.FromArgb(70, 59, 130, 246));      // soft blue
        var refBg = ctx.GetBrush(WpfColor.FromArgb(70, 139, 92, 246));      // soft purple

        foreach (var span in spans)
        {
            if (span.Kind is not (InlineKind.Tag or InlineKind.Ref)) continue;
            layout.HitTestTextPosition((uint)span.Start, false, out float sx, out _, out _);
            layout.HitTestTextPosition((uint)(span.Start + span.Length), false, out float ex, out _, out _);
            float padX = 3f;
            var rect = new RoundedRectangle(
                new RectangleF(textX + sx - padX, lineY + 1f, Math.Max(2, ex - sx) + padX * 2, rowH - 2f),
                4f, 4f);
            ctx.RenderTarget.FillRoundedRectangle(rect, span.Kind == InlineKind.Tag ? tagBg : refBg);
        }
    }

    /// <summary>
    /// Build the per-line display string used at draw time. When <paramref name="hideMarkers"/>
    /// is true (view mode), strips the **/~~/`/* / [[ ]] markers so the user sees clean
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
                case InlineKind.Ref:
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

    internal enum InlineKind { Bold, Italic, Strike, Code, Tag, Ref }
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
    public static int TryHitTodoCheckbox(RenderBlock block, Rect bounds, WpfPoint world)
    {
        if (!IsOutlineBlock(block)) return -1;

        var style = block.Style ?? new BoardItemStyle();
        var doc = Parse(block.Body ?? string.Empty, style);
        var content = GetContentRect(block, bounds);
        float fontSize = block.Kind == BlockKind.Note ? Math.Clamp((float)style.FontSize, 8f, 48f) : Math.Clamp((float)style.FontSize, 8f, 96f);
        float rowH = Math.Max(16f, fontSize * 1.45f);
        float y = (float)content.Y;
        foreach (var line in doc.VisibleLines)
        {
            if (y > content.Bottom) break;
            if (ClassifyTodo(line.Text) != TodoState.None)
            {
                // Mirror Draw's column layout so the hit area lands on the actual
                // checkbox glyph (whether it's at the bullet position or shifted
                // right of the collapse toggle).
                float indent = line.Level * IndentWidth;
                float toggleX = (float)content.X + indent + 1f;
                float bulletX = (float)content.X + indent + 12f;
                bool hasToggle = line.HasChildren;
                float checkboxX = hasToggle ? toggleX + ToggleSize + 2 : bulletX - CheckboxSize * 0.5f + 1;
                var hit = new Rect(checkboxX - 4, y, CheckboxSize + 8, rowH);
                if (hit.Contains(world))
                    return line.Index;
            }
            y += rowH;
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

    private static void DrawToggle(DrawingContext ctx, float x, float y, bool collapsed, WpfColor color)
    {
        var brush = ctx.GetBrush(WpfColor.FromArgb(170, color.R, color.G, color.B));
        var fill = ctx.GetBrush(WpfColor.FromArgb(32, color.R, color.G, color.B));
        var rect = new RoundedRectangle(new RectangleF(x, y, ToggleSize, ToggleSize), 2, 2);
        ctx.RenderTarget.FillRoundedRectangle(rect, fill);
        ctx.RenderTarget.DrawRoundedRectangle(rect, brush, ctx.InvStroke(0.8f));
        var p1 = collapsed ? new Vector2(x + 3.4f, y + 2.4f) : new Vector2(x + 2.6f, y + 3.4f);
        var p2 = collapsed ? new Vector2(x + 6.2f, y + 4.5f) : new Vector2(x + 4.5f, y + 6.2f);
        var p3 = collapsed ? new Vector2(x + 3.4f, y + 6.6f) : new Vector2(x + 6.4f, y + 3.4f);
        ctx.RenderTarget.DrawLine(p1, p2, brush, ctx.InvStroke(1.2f));
        ctx.RenderTarget.DrawLine(p2, p3, brush, ctx.InvStroke(1.2f));
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
                    return prev.Start + prev.Length;
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

    public static string BulletPrefixForLine(string line)
    {
        int indent = line.TakeWhile(c => c is ' ' or '\t').Count();
        string leading = line[..indent];
        string rest = line[indent..];
        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal))
            return leading + rest[..2];
        if (rest.StartsWith("\u2022 ", StringComparison.Ordinal))
            return leading + "- ";
        return leading + "- ";
    }

    public static int PrefixLengthForLine(string line)
    {
        int indent = line.TakeWhile(c => c is ' ' or '\t').Count();
        string rest = line[indent..];
        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal) || rest.StartsWith("\u2022 ", StringComparison.Ordinal))
            return indent + 2;
        return indent;
    }

    public static string IndentLine(string line) => "  " + line;

    public static string OutdentLine(string line)
    {
        if (line.StartsWith("  ", StringComparison.Ordinal)) return line[2..];
        if (line.StartsWith("\t", StringComparison.Ordinal)) return line[1..];
        return line;
    }

    public static string MoveSubtree(string text, int cursorPos, int direction, out int nextCursorPos)
    {
        nextCursorPos = cursorPos;
        var lines = SplitLines(text);
        if (lines.Count <= 1) return text;
        int current = Math.Clamp(LineIndexAt(text, cursorPos), 0, lines.Count - 1);
        int start = current;
        int level = ParseLine(current, 0, lines[current]).Level;
        int end = current + 1;
        while (end < lines.Count && ParseLine(end, 0, lines[end]).Level > level) end++;

        if (direction < 0)
        {
            int prevStart = start - 1;
            while (prevStart >= 0 && ParseLine(prevStart, 0, lines[prevStart]).Level > level)
                prevStart--;
            if (prevStart < 0 || ParseLine(prevStart, 0, lines[prevStart]).Level != level) return text;
            var moving = lines.GetRange(start, end - start);
            lines.RemoveRange(start, end - start);
            lines.InsertRange(prevStart, moving);
            nextCursorPos = CursorPosForLine(lines, prevStart, Math.Max(0, cursorPos - LineStartOffset(text, current)));
            return string.Join('\n', lines);
        }

        int nextStart = end;
        if (nextStart >= lines.Count) return text;
        int nextLevel = ParseLine(nextStart, 0, lines[nextStart]).Level;
        if (nextLevel != level) return text;
        int nextEnd = nextStart + 1;
        while (nextEnd < lines.Count && ParseLine(nextEnd, 0, lines[nextEnd]).Level > nextLevel) nextEnd++;
        var subtree = lines.GetRange(start, end - start);
        lines.RemoveRange(start, end - start);
        int insertAt = nextEnd - subtree.Count;
        lines.InsertRange(insertAt, subtree);
        nextCursorPos = CursorPosForLine(lines, insertAt, Math.Max(0, cursorPos - LineStartOffset(text, current)));
        return string.Join('\n', lines);
    }

    private static int LineStartOffset(string text, int lineIndex)
    {
        int line = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (line == lineIndex) return i;
            if (text[i] == '\n') line++;
        }
        return text.Length;
    }

    private static int CursorPosForLine(IReadOnlyList<string> lines, int lineIndex, int column)
    {
        int pos = 0;
        for (int i = 0; i < lineIndex && i < lines.Count; i++)
            pos += lines[i].Length + 1;
        return pos + Math.Min(column, lines[Math.Clamp(lineIndex, 0, lines.Count - 1)].Length);
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

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
        string id = StableId(index, level, display.Trim());
        return new OutlineLineBuilder(index, start, line.Length, Math.Max(0, level), indentChars + bulletLen, display, id);
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

    private sealed record OutlineLineBuilder(int Index, int Start, int Length, int Level, int PrefixLength, string Text, string Id);
}
