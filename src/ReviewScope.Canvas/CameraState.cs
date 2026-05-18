namespace ReviewScope.Canvas;

public sealed record CameraState(double Zoom, double OffsetX, double OffsetY)
{
    public static CameraState Default { get; } = new(1.0, 48.0, 48.0);
}
