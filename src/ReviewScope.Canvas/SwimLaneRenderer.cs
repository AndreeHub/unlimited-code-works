using System.Numerics;
using System.Windows;
using ReviewScope.Domain;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using RectangleF = System.Drawing.RectangleF;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

internal sealed class SwimLaneRenderer
{
    private readonly DrawingContext _ctx;

    public SwimLaneRenderer(DrawingContext ctx)
    {
        _ctx = ctx;
    }

    public void DrawSwimLane(SceneSwimLaneVisual laneVis)
    {
        var lane = laneVis.Lane;
        WpfColor color = CanvasDrawingUtils.ParseColor(lane.Color);
        bool selected = lane.IsSelected;

        float x = (float)laneVis.Bounds.X, y = (float)laneVis.Bounds.Y;
        float w = (float)laneVis.Bounds.Width, h = (float)laneVis.Bounds.Height;

        // Fill: translucent color
        _ctx.RenderTarget.FillRectangle(new RectangleF(x, y, w, h),
            _ctx.GetBrush(WpfColor.FromArgb(22, color.R, color.G, color.B)));

        // Dashed border
        var borderBrush = _ctx.GetBrush(selected
            ? WpfColor.FromArgb(200, color.R, color.G, color.B)
            : WpfColor.FromArgb(100, color.R, color.G, color.B));
        float stroke = _ctx.InvStroke(selected ? 2.0f : 1.25f);

        _ctx.RenderTarget.DrawRectangle(new RectangleF(x, y, w, h), borderBrush, stroke);

        // Label bar at top
        float labelH = 32;
        _ctx.RenderTarget.FillRectangle(new RectangleF(x, y, w, labelH),
            _ctx.GetBrush(WpfColor.FromArgb(55, color.R, color.G, color.B)));
        _ctx.DrawText(lane.Name, x + 12, y + 8, w - 24, 13, WpfColor.FromArgb(220, color.R, color.G, color.B));

        // Resize handle indicator
        float hs = 12;
        _ctx.RenderTarget.FillRectangle(new RectangleF(x + w - hs, y + h - hs, hs, hs),
            _ctx.GetBrush(WpfColor.FromArgb(80, color.R, color.G, color.B)));
    }
}
