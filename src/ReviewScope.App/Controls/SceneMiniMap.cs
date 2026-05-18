using ReviewScope.Domain;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReviewScope.Canvas;

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

    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(
        nameof(Camera),
        typeof(CameraState),
        typeof(SceneMiniMap),
        new FrameworkPropertyMetadata(CameraState.Default,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public CameraState Camera
    {
        get => (CameraState)GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(
        nameof(ViewportWidth),
        typeof(double),
        typeof(SceneMiniMap),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ViewportWidth
    {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(
        nameof(ViewportHeight),
        typeof(double),
        typeof(SceneMiniMap),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ViewportHeight
    {
        get => (double)GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
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

        var transform = GetMapTransform(bounds, width, height);

        var laneBrush = new SolidColorBrush(Color.FromArgb(24, 35, 162, 109));
        var lanePen = new Pen(new SolidColorBrush(Color.FromArgb(100, 35, 162, 109)), 1);
        foreach (var lane in Scene.SwimLanes)
        {
            dc.DrawRectangle(laneBrush, lanePen, MapRect(lane.X, lane.Y, lane.Width, lane.Height, transform));
        }

        var blockBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        var blockPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 46, 125, 215)), 1);
        var noteBrush = new SolidColorBrush(Color.FromRgb(255, 243, 199));
        var notePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 226, 186, 76)), 1);
        foreach (var block in Scene.Blocks)
        {
            var rect = MapRect(block.X, block.Y, block.Width, block.Height, transform);
            dc.DrawRectangle(block.Kind == BlockKind.Note ? noteBrush : blockBrush, block.Kind == BlockKind.Note ? notePen : blockPen, rect);
        }

        var viewport = GetViewportRect();
        if (!viewport.IsEmpty)
        {
            var rect = MapRect(viewport.X, viewport.Y, viewport.Width, viewport.Height, transform);
            var fill = new SolidColorBrush(Color.FromArgb(34, 37, 99, 235));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 1.4);
            dc.DrawRectangle(fill, pen, rect);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        MoveCameraTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            MoveCameraTo(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            MoveCameraTo(e.GetPosition(this));
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void MoveCameraTo(Point point)
    {
        var bounds = GetSceneBounds(Scene);
        if (bounds.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0 || Camera.Zoom <= 0) return;

        var transform = GetMapTransform(bounds, ActualWidth, ActualHeight);
        double worldX = (point.X - transform.OffsetX) / transform.Scale;
        double worldY = (point.Y - transform.OffsetY) / transform.Scale;

        Camera = Camera with
        {
            OffsetX = ViewportWidth / 2 - worldX * Camera.Zoom,
            OffsetY = ViewportHeight / 2 - worldY * Camera.Zoom
        };
    }

    private Rect GetViewportRect()
    {
        if (ViewportWidth <= 0 || ViewportHeight <= 0 || Camera.Zoom <= 0) return Rect.Empty;
        return new Rect(
            -Camera.OffsetX / Camera.Zoom,
            -Camera.OffsetY / Camera.Zoom,
            ViewportWidth / Camera.Zoom,
            ViewportHeight / Camera.Zoom);
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

    private static MapTransform GetMapTransform(Rect bounds, double width, double height)
    {
        const double pad = 10;
        double sx = (width - pad * 2) / Math.Max(1, bounds.Width);
        double sy = (height - pad * 2) / Math.Max(1, bounds.Height);
        double scale = Math.Min(sx, sy);
        double offsetX = pad + (width - pad * 2 - bounds.Width * scale) / 2 - bounds.X * scale;
        double offsetY = pad + (height - pad * 2 - bounds.Height * scale) / 2 - bounds.Y * scale;
        return new MapTransform(scale, offsetX, offsetY);
    }

    private static Rect MapRect(double x, double y, double width, double height, MapTransform transform) =>
        new(x * transform.Scale + transform.OffsetX,
            y * transform.Scale + transform.OffsetY,
            Math.Max(2, width * transform.Scale),
            Math.Max(2, height * transform.Scale));

    private readonly record struct MapTransform(double Scale, double OffsetX, double OffsetY);
}
