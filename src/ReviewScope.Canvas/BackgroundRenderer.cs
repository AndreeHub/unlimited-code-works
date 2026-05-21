using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

internal sealed class BackgroundRenderer
{
    private readonly DrawingContext _ctx;

    public BackgroundRenderer(DrawingContext ctx)
    {
        _ctx = ctx;
    }

    public void DrawBackground(Size viewSize, CanvasBackgroundMode mode)
    {
        if (mode == CanvasBackgroundMode.Dots)
            DrawDots(viewSize);
        else
            DrawGrid(viewSize);
    }

    private void DrawGrid(Size viewSize)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        var minor = _ctx.GetBrush(WpfColor.FromArgb(62, 198, 207, 219));
        var major = _ctx.GetBrush(WpfColor.FromArgb(104, 172, 184, 199));
        // Camera-aware grid: lines stay locked to world units so panning feels physical
        float spacing = 36f;
        float majorEvery = 5f;
        double zoom = _ctx.Camera.Zoom;
        float gridStep = (float)(spacing * zoom);
        if (gridStep < 12f) gridStep = (float)(spacing * 4 * zoom);
        if (gridStep < 8f) return;

        float ox = (float)(_ctx.Camera.OffsetX % gridStep);
        float oy = (float)(_ctx.Camera.OffsetY % gridStep);
        int i = 0;
        for (float x = ox; x < w; x += gridStep, i++)
            _ctx.RenderTarget.DrawLine(new Vector2(x, 0), new Vector2(x, h), i % (int)majorEvery == 0 ? major : minor, 1f);
        i = 0;
        for (float y = oy; y < h; y += gridStep, i++)
            _ctx.RenderTarget.DrawLine(new Vector2(0, y), new Vector2(w, y), i % (int)majorEvery == 0 ? major : minor, 1f);
    }

    private void DrawDots(Size viewSize)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        const double worldSpacing = 24;
        float spacing = (float)(worldSpacing * _ctx.Camera.Zoom);
        while (spacing < 13f) spacing *= 2f;
        while (spacing > 30f) spacing *= 0.5f;

        float ox = (float)(_ctx.Camera.OffsetX % spacing);
        float oy = (float)(_ctx.Camera.OffsetY % spacing);
        byte alpha = (byte)Math.Clamp(132 - Math.Abs(spacing - 20f) * 2.0f, 72, 132);
        var dot = _ctx.GetBrush(WpfColor.FromArgb(alpha, 176, 186, 200));
        float r = Math.Clamp(spacing / 14f, 1.15f, 1.85f);
        for (float y = oy; y < h; y += spacing)
            for (float x = ox; x < w; x += spacing)
                _ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2(x, y), r, r), dot);
    }

    public void DrawEmptyHint(Size viewSize)
    {
        float cw = 480, ch = 180;
        float x = (float)((viewSize.Width - cw) / 2), y = (float)((viewSize.Height - ch) / 2);
        var rr = new RoundedRectangle(new RectangleF(x, y, cw, ch), 8, 8);
        _ctx.RenderTarget.FillRoundedRectangle(rr, _ctx.GetBrush(WpfColor.FromRgb(255, 255, 255)));
        _ctx.RenderTarget.DrawRoundedRectangle(rr, _ctx.GetBrush(WpfColor.FromArgb(220, 226, 232, 240)), 1f);
        _ctx.DrawText("ReviewScope", x + 24, y + 22, cw - 48, 16, WpfColor.FromRgb(31, 41, 51));
        _ctx.DrawText("Open a project, then place files and extracted methods on the canvas.", x + 24, y + 52, cw - 48, 13, WpfColor.FromRgb(83, 96, 112));
        _ctx.DrawText("Use the explorer, command bar, and note tools to map review context.", x + 24, y + 90, cw - 48, 11, WpfColor.FromRgb(119, 132, 150));
    }
}
