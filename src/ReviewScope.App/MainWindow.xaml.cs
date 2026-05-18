using ReviewScope.App.ViewModels;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using System.Windows;
using System.Windows.Input;

namespace ReviewScope.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private string? _editingNoteKey;
    private bool _noteEditDiscarded;

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Wire canvas events
        CanvasViewport.BlockActivatedCommand = new RelayCommand<BlockActivatedArgs>(OnBlockActivated);
        CanvasViewport.ExtractRequestedCommand = new RelayCommand<ExtractRequestedArgs>(OnExtractRequested);
        CanvasViewport.FocusRequestedCommand = new RelayCommand<FocusRequestedArgs>(OnFocusRequested);
        CanvasViewport.RestoreRequestedCommand = new RelayCommand<RestoreRequestedArgs>(OnRestoreRequested);
        CanvasViewport.ConnectionDrawnCommand = new RelayCommand<ConnectionDrawnArgs>(OnConnectionDrawn);
        CanvasViewport.AnnotationRequestedCommand = new RelayCommand<AnnotationRequestedArgs>(OnAnnotationRequested);
        CanvasViewport.NoteEditRequestedCommand = new RelayCommand<NoteEditRequestedArgs>(OnNoteEditRequested);

        // When scene is mutated inside the canvas (drag, delete, resize), sync back
        CanvasViewport.BlockMovedCommand = new RelayCommand<RenderScene>(OnSceneChangedByCanvas);
    }

    private async void OnExplorerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerTree.SelectedItem is FileExplorerItemViewModel item && item.IsFile && item.FilePath is not null)
        {
            await _vm.LoadSymbolsForFileAsync(item.FilePath);
            await _vm.AddFileToCanvasAsync(item.FilePath);
        }
    }

    private async void OnSessionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _vm.ActivateSelectedSessionAsync();
    }

    private void OnFrameAll(object sender, RoutedEventArgs e) => CanvasViewport.FrameAll();

    // Canvas drag-drop from explorer
    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        foreach (string file in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            await _vm.AddFileToCanvasAsync(file);
    }

    // Canvas event handlers
    private void OnBlockActivated(BlockActivatedArgs? args) { /* double-click on code block */ }

    private async void OnExtractRequested(ExtractRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleExtractRequestAsync(args);
    }

    private async void OnFocusRequested(FocusRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleFocusRequestAsync(args);
    }

    private async void OnRestoreRequested(RestoreRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleRestoreAsync(args);
    }

    private async void OnConnectionDrawn(ConnectionDrawnArgs? args)
    {
        if (args is not null) await _vm.HandleConnectionDrawnAsync(args);
    }

    private async void OnAnnotationRequested(AnnotationRequestedArgs? args)
    {
        if (args is null) return;
        await _vm.AddAnnotationAsync(args);
        // Immediately open the inline editor for the new note
        var newNote = _vm.Scene.Blocks.LastOrDefault(b => b.Kind == BlockKind.Note);
        if (newNote is not null)
            OpenNotePopup(newNote.Key, newNote.Title, newNote.Body ?? string.Empty,
                newNote.X, newNote.Y, newNote.Width, newNote.Height);
    }

    private void OnNoteEditRequested(NoteEditRequestedArgs? args)
    {
        if (args is null) return;
        OpenNotePopup(args.NoteKey, args.Title, args.Body, args.WorldX, args.WorldY, args.WorldW, args.WorldH);
    }

    private void OpenNotePopup(string noteKey, string title, string body,
        double worldX, double worldY, double worldW, double worldH)
    {
        var camera = CanvasViewport.Camera;
        double screenX = worldX * camera.Zoom + camera.OffsetX;
        double screenY = worldY * camera.Zoom + camera.OffsetY;
        double screenW = Math.Max(200, worldW * camera.Zoom);
        double screenH = Math.Max(90, worldH * camera.Zoom);

        _editingNoteKey = noteKey;
        _noteEditDiscarded = false;
        NoteEditTitleBox.Text = title;
        NoteEditBodyBox.Text = body;
        NoteEditPopupContent.Width = screenW;
        NoteEditPopupContent.Height = screenH;
        NoteEditPopup.HorizontalOffset = screenX;
        NoteEditPopup.VerticalOffset = screenY;
        NoteEditPopup.IsOpen = true;
        NoteEditTitleBox.Focus();
        NoteEditTitleBox.SelectAll();
    }

    private async void OnNoteEditPopupClosed(object sender, EventArgs e)
    {
        if (_editingNoteKey is not null && !_noteEditDiscarded)
            await _vm.SaveNoteEditAsync(_editingNoteKey, NoteEditTitleBox.Text, NoteEditBodyBox.Text);
        _editingNoteKey = null;
        _noteEditDiscarded = false;
    }

    private void OnNoteEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _noteEditDiscarded = true;
            _editingNoteKey = null;
            NoteEditPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            NoteEditPopup.IsOpen = false; // triggers Closed → saves
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && sender == NoteEditTitleBox)
        {
            NoteEditBodyBox.Focus();
            e.Handled = true;
        }
    }

    private async void OnSceneChangedByCanvas(RenderScene? newScene)
    {
        if (newScene is not null) await _vm.OnSceneChangedByCanvas(newScene);
    }
}

// Simple relay command for canvas wiring
internal sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    public RelayCommand(Action<T?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
