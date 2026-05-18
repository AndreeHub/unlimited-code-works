using ReviewScope.Domain;
using System.Windows;
using System.Windows.Media;

namespace ReviewScope.App.Controls;

public sealed class SceneMiniMap : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene),
        typeof(RenderScene),
        typeof(SceneMiniMap),
        new FrameworkPropertyMetadata(RenderScene.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public RenderScene Scene
    {
        get => (RenderScene)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var border = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
        dc.DrawRoundedRectangle(Brushes.White, border, new Rect(0.5, 0.5, width - 1, height - 1), 5, 5);

        var bounds = GetSceneBounds(Scene);
        if (bounds.IsEmpty) return;

        const double pad = 10;
        double sx = (width - pad * 2) / Math.Max(1, bounds.Width);
        double sy = (height - pad * 2) / Math.Max(1, bounds.Height);
        double scale = Math.Min(sx, sy);
        double offsetX = pad + (width - pad * 2 - bounds.Width * scale) / 2 - bounds.X * scale;
        double offsetY = pad + (height - pad * 2 - bounds.Height * scale) / 2 - bounds.Y * scale;

        var laneBrush = new SolidColorBrush(Color.FromArgb(24, 35, 162, 109));
        var lanePen = new Pen(new SolidColorBrush(Color.FromArgb(100, 35, 162, 109)), 1);
        foreach (var lane in Scene.SwimLanes)
        {
            dc.DrawRectangle(laneBrush, lanePen, MapRect(lane.X, lane.Y, lane.Width, lane.Height, scale, offsetX, offsetY));
        }

        var blockBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        var blockPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 46, 125, 215)), 1);
        var noteBrush = new SolidColorBrush(Color.FromRgb(255, 243, 199));
        var notePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 226, 186, 76)), 1);
        foreach (var block in Scene.Blocks)
        {
            var rect = MapRect(block.X, block.Y, block.Width, block.Height, scale, offsetX, offsetY);
            dc.DrawRectangle(block.Kind == BlockKind.Note ? noteBrush : blockBrush, block.Kind == BlockKind.Note ? notePen : blockPen, rect);
        }
    }

    private static Rect GetSceneBounds(RenderScene scene)
    {
        Rect bounds = Rect.Empty;

        foreach (var lane in scene.SwimLanes)
            bounds = Union(bounds, new Rect(lane.X, lane.Y, lane.Width, lane.Height));

        foreach (var block in scene.Blocks)
            bounds = Union(bounds, new Rect(block.X, block.Y, block.Width, block.Height));

        return bounds;
    }

    private static Rect Union(Rect current, Rect next)
    {
        if (next.IsEmpty) return current;
        if (current.IsEmpty) return next;
        current.Union(next);
        return current;
    }

    private static Rect MapRect(double x, double y, double width, double height, double scale, double offsetX, double offsetY) =>
        new(x * scale + offsetX, y * scale + offsetY, Math.Max(2, width * scale), Math.Max(2, height * scale));
}
