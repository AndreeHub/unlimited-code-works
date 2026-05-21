using System.Numerics;
using System.Windows;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using WpfColor = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;

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
        ID2D1StrokeStyle dashedStroke)
    {
        _rt = rt;
        _factory = factory;
        _dwrite = dwrite;
        _camera = camera;
        _brushProvider = brushProvider;
        _textFormatProvider = textFormatProvider;
        _dashedStroke = dashedStroke;
    }

    public ID2D1SolidColorBrush GetBrush(WpfColor color) => _brushProvider(color);
    
    public IDWriteTextFormat GetTextFormat(float size, bool sketchy = false) => _textFormatProvider(size, sketchy);

    public ID2D1StrokeStyle DashedStroke => _dashedStroke;

    public float InvStroke(float strokeWidth) => (float)(strokeWidth / _camera.Zoom);

    public void DrawText(string text, float x, float y, float maxWidth, float fontSize, WpfColor color, bool sketchy = false)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 1) return;
        var fmt = GetTextFormat(fontSize, sketchy);
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, 1000f);
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
    }

    public void DrawWrappedText(string text, float x, float y, float maxWidth, float maxHeight, float fontSize, WpfColor color, bool wrap = false, bool sketchy = false)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 1 || maxHeight < 1) return;
        var fmt = GetTextFormat(fontSize, sketchy);
        using var layout = _dwrite.CreateTextLayout(text, fmt, maxWidth, maxHeight);
        if (wrap) layout.WordWrapping = WordWrapping.Wrap;
        _rt.DrawTextLayout(new Vector2(x, y), layout, GetBrush(color), DrawTextOptions.Clip);
    }

    public Color4 ToColor4(WpfColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
}
