using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using WpfColor = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;
using DWriteFontWeight = Vortice.DirectWrite.FontWeight;
using DWriteFontStyle = Vortice.DirectWrite.FontStyle;
using DWriteFontStretch = Vortice.DirectWrite.FontStretch;
using DWriteTextAlignment = Vortice.DirectWrite.TextAlignment;
using DWriteParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment;

namespace ReviewScope.Canvas;

/// <summary>
/// Provides access to drawing resources and camera state for decomposed renderers.
/// </summary>
internal sealed class DrawingContext
{
    private readonly ID2D1RenderTarget _rt;
    private readonly ID2D1Factory _factory;
    private readonly IDWriteFactory _dwrite;
    private readonly CameraState _camera;
    private readonly Func<WpfColor, ID2D1SolidColorBrush> _brushProvider;
    private readonly Func<float, bool, IDWriteTextFormat> _textFormatProvider;
    private readonly ID2D1StrokeStyle _dashedStroke;
    private readonly ID2D1StrokeStyle? _roundStroke;
    private readonly ID2D1StrokeStyle? _dottedStroke;

    public ID2D1RenderTarget RenderTarget => _rt;
    public ID2D1Factory Factory => _factory;
    public IDWriteFactory DWriteFactory => _dwrite;
    public CameraState Camera => _camera;
    public double Zoom => _camera.Zoom;

    public DrawingContext(
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        IDWriteFactory dwrite,
        CameraState camera,
        Func<WpfColor, ID2D1SolidColorBrush> brushProvider,
        Func<float, bool, IDWriteTextFormat> textFormatProvider,
        ID2D1StrokeStyle dashedStroke,
        ID2D1StrokeStyle? roundStroke = null,
        ID2D1StrokeStyle? dottedStroke = null)
    {
        _rt = rt;
        _factory = factory;
        _dwrite = dwrite;
        _camera = camera;
        _brushProvider = brushProvider;
        _textFormatProvider = textFormatProvider;
        _dashedStroke = dashedStroke;
        _roundStroke = roundStroke;
        _dottedStroke = dottedStroke;
    }

    public ID2D1SolidColorBrush GetBrush(WpfColor color) => _brushProvider(color);
    
    public IDWriteTextFormat GetTextFormat(float size, bool sketchy = false) => _textFormatProvider(size, sketchy);

    public ID2D1StrokeStyle DashedStroke => _dashedStroke;

    /// <summary>Dotted stroke (Excalidraw's third line style). Falls back to dashed if unavailable.</summary>
    public ID2D1StrokeStyle DottedStroke => _dottedStroke ?? _dashedStroke;

    /// <summary>Resolves the outline stroke style for a shape style: dashed wins over dotted; null = solid.</summary>
    public ID2D1StrokeStyle? StrokeStyleFor(ReviewScope.Domain.BoardItemStyle style) =>
        style.Dashed ? DashedStroke : style.Dotted ? DottedStroke : null;

    /// <summary>Solid stroke with round caps and joins — used for freehand (freedraw) strokes so
    /// pen lines have smooth, rounded ends instead of flat cut-offs. Falls back to the dashed
    /// style's geometry if no dedicated round style was provided.</summary>
    public ID2D1StrokeStyle RoundStroke => _roundStroke ?? _dashedStroke;

    public float InvStroke(float strokeWidth) => (float)(strokeWidth / _camera.Zoom);

    public void DrawText(string text, float x, float y, float maxWidth, float fontSize, WpfColor color, bool sketchy = false)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 1) return;
        var fmt = GetTextFormat(fontSize, sketchy);
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, 1000f);
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
    }

    public void DrawWrappedText(string text, float x, float y, float maxWidth, float maxHeight, float fontSize, WpfColor color, bool wrap = false, bool sketchy = false, Vortice.DirectWrite.TextAlignment alignment = Vortice.DirectWrite.TextAlignment.Leading)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 1 || maxHeight < 1) return;
        var fmt = GetTextFormat(fontSize, sketchy);
        var oldTextAlign = fmt.TextAlignment;
        fmt.TextAlignment = alignment;
        
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, maxHeight);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
        
        fmt.TextAlignment = oldTextAlign;
    }

    public Color4 ToColor4(WpfColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    // ---- Rich text (custom font family/weight/style) ----

    private readonly Dictionary<string, IDWriteTextFormat> _richFormats = new();

    public IDWriteTextFormat GetRichFormat(string fontFamily, float size, bool bold, bool italic)
    {
        string key = $"{fontFamily}|{size:F1}|{(bold ? 'B' : 'n')}|{(italic ? 'I' : 'n')}";
        if (_richFormats.TryGetValue(key, out var cached)) return cached;

        var weight = bold ? DWriteFontWeight.Bold : DWriteFontWeight.Normal;
        var style = italic ? DWriteFontStyle.Italic : DWriteFontStyle.Normal;
        IDWriteTextFormat fmt;
        try
        {
            fmt = _dwrite.CreateTextFormat(fontFamily, weight, style, DWriteFontStretch.Normal, size);
        }
        catch
        {
            fmt = _dwrite.CreateTextFormat("Segoe UI", weight, style, DWriteFontStretch.Normal, size);
        }
        fmt.WordWrapping = WordWrapping.Wrap;
        _richFormats[key] = fmt;
        return fmt;
    }

    /// <summary>
    /// Draws text with full rich-text styling: font family, weight, italic, underline,
    /// strikethrough, horizontal + vertical alignment, optional word-wrap, optional
    /// drop shadow.
    /// </summary>
    public void DrawRichText(
        string text,
        float x, float y, float width, float height,
        string fontFamily,
        float fontSize,
        bool bold,
        bool italic,
        bool underline,
        bool strikethrough,
        WpfColor color,
        DWriteTextAlignment hAlign,
        DWriteParagraphAlignment vAlign,
        bool wrap,
        bool shadow)
    {
        if (string.IsNullOrEmpty(text) || width < 1 || height < 1) return;

        var fmt = GetRichFormat(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily, fontSize, bold, italic);
        var oldH = fmt.TextAlignment;
        var oldV = fmt.ParagraphAlignment;
        var oldWrap = fmt.WordWrapping;
        fmt.TextAlignment = hAlign;
        fmt.ParagraphAlignment = vAlign;
        fmt.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;

        using var layout = _dwrite.CreateTextLayout(text, fmt, width, height);
        var fullRange = new TextRange(0, (uint)text.Length);
        if (underline) layout.SetUnderline(true, fullRange);
        if (strikethrough) layout.SetStrikethrough(true, fullRange);

        if (shadow)
        {
            var shadowColor = WpfColor.FromArgb((byte)(color.A * 0.35), 0, 0, 0);
            _rt.DrawTextLayout(new Vector2(x + 1.5f, y + 1.5f), layout, GetBrush(shadowColor), DrawTextOptions.Clip);
        }
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);

        fmt.TextAlignment = oldH;
        fmt.ParagraphAlignment = oldV;
        fmt.WordWrapping = oldWrap;
    }
}
