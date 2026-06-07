using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels;

/// <summary>
/// Per-document canvas state — the unit that makes independent panes possible. Each open board
/// owns its own <see cref="Scene"/> and undo/redo history here, instead of those living as singletons
/// on <see cref="MainWindowViewModel"/>. The shell delegates its <c>Scene</c>/undo/<c>_activeSession</c>
/// members to the <i>active</i> instance, so existing shell code keeps operating on "the focused board"
/// unchanged while a second pane can hold a different instance.
///
/// Selection/inspector deliberately stay shell-level (one inspector reflects the focused pane), so this
/// type holds only the document data, not the editing chrome.
/// </summary>
public sealed partial class CanvasDocumentViewModel : ObservableObject
{
    /// <summary>The board this document renders/persists. Null for a blank/placeholder canvas.</summary>
    public ReviewSession? Session { get; set; }

    [ObservableProperty] private RenderScene _scene = RenderScene.Empty;

    /// <summary>Per-document undo/redo. Switching panes must not cross-contaminate history.</summary>
    public Stack<RenderScene> UndoStack { get; } = new();
    public Stack<RenderScene> RedoStack { get; } = new();

    public CanvasDocumentViewModel(ReviewSession? session)
    {
        Session = session;
    }
}
