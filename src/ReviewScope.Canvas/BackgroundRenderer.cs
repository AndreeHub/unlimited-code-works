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

    public void DrawBackground(Size viewSize, CanvasBackgroundMode mode, CameraState camera)
    {
        if (mode == CanvasBackgroundMode.Dots)
            DrawDots(viewSize, camera);
        else
            DrawGrid(viewSize, camera);
    }

    private void DrawGrid(Size viewSize, CameraState camera)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        const float worldSpacing = 36f;
        const int majorEvery = 5;

        float screenSpacing = worldSpacing * (float)camera.Zoom;
        if (screenSpacing < 3f) return;

        var minor = _ctx.GetBrush(WpfColor.FromArgb(62, 198, 207, 219));
        var major = _ctx.GetBrush(WpfColor.FromArgb(104, 172, 184, 199));

        // World-anchored: world line at x = i*worldSpacing is at screen x = i*screenSpacing + offsetX.
        // Find the first world index whose screen position is >= 0.
        int firstIx = (int)System.Math.Floor(-camera.OffsetX / screenSpacing);
        int firstIy = (int)System.Math.Floor(-camera.OffsetY / screenSpacing);

        for (int ix = firstIx; ; ix++)
        {
            float x = ix * screenSpacing + (float)camera.OffsetX;
            if (x > w) break;
            var brush = (ix % majorEvery == 0) ? major : minor;
            _ctx.RenderTarget.DrawLine(new Vector2(x, 0), new Vector2(x, h), brush, 1f);
        }
        for (int iy = firstIy; ; iy++)
        {
            float y = iy * screenSpacing + (float)camera.OffsetY;
            if (y > h) break;
            var brush = (iy % majorEvery == 0) ? major : minor;
            _ctx.RenderTarget.DrawLine(new Vector2(0, y), new Vector2(w, y), brush, 1f);
        }
    }

    private void DrawDots(Size viewSize, CameraState camera)
    {
        float w = (float)viewSize.Width, h = (float)viewSize.Height;
        const float worldSpacing = 20f;
        const float worldRadius = 1.4f;

        float screenSpacing = worldSpacing * (float)camera.Zoom;
        // When zoomed way out, dots crowd into noise — drop them.
        if (screenSpacing < 4f) return;

        // Keep dot size readable but proportional to zoom (clamped).
        float radius = System.Math.Clamp(worldRadius * (float)camera.Zoom, 0.6f, 3.0f);

        var dot = _ctx.GetBrush(WpfColor.FromArgb(132, 176, 186, 200));

        int firstIx = (int)System.Math.Floor(-camera.OffsetX / screenSpacing);
        int firstIy = (int)System.Math.Floor(-camera.OffsetY / screenSpacing);

        for (int iy = firstIy; ; iy++)
        {
            float y = iy * screenSpacing + (float)camera.OffsetY;
            if (y > h + radius) break;
            for (int ix = firstIx; ; ix++)
            {
                float x = ix * screenSpacing + (float)camera.OffsetX;
                if (x > w + radius) break;
                _ctx.RenderTarget.FillEllipse(new Ellipse(new Vector2(x, y), radius, radius), dot);
            }
        }
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
