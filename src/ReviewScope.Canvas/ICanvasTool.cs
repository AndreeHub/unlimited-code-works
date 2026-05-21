using System.Windows;
using System.Windows.Input;

namespace ReviewScope.Canvas;

internal interface ICanvasTool
{
    string Name { get; }
    void HandleLDown(Point screen, Point world, ModifierKeys modifiers);
    void HandleLUp(Point screen, Point world, ModifierKeys modifiers);
    void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers);
    void HandleRDown(Point screen, Point world, ModifierKeys modifiers);
    void HandleKeyDown(Key key, ModifierKeys modifiers);
    void HandleKeyUp(Key key, ModifierKeys modifiers);
    void Deactivate();
}

internal abstract class CanvasToolBase : ICanvasTool
{
    protected readonly CanvasViewport Viewport;
    public abstract string Name { get; }

    protected CanvasToolBase(CanvasViewport viewport)
    {
        Viewport = viewport;
    }

    public virtual void HandleLDown(Point screen, Point world, ModifierKeys modifiers) { }
    public virtual void HandleLUp(Point screen, Point world, ModifierKeys modifiers) { }
    public virtual void HandleMouseMove(Point screen, Point world, ModifierKeys modifiers) { }
    public virtual void HandleRDown(Point screen, Point world, ModifierKeys modifiers) { }
    public virtual void HandleKeyDown(Key key, ModifierKeys modifiers) { }
    public virtual void HandleKeyUp(Key key, ModifierKeys modifiers) { }
    public virtual void Deactivate() { }
}
