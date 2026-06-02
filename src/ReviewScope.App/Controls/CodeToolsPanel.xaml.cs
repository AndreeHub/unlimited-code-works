using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReviewScope.App.ViewModels;

namespace ReviewScope.App.Controls;

/// <summary>
/// The code search + extraction tools (Explorer / Symbols / Search), factored out of the main
/// window so they can be docked left/right or detached into a floating window. The control is a
/// dumb view over <see cref="MainWindowViewModel"/> (its DataContext); the two actions that need
/// the host's canvas (open a file, navigate a board-search hit) are surfaced as events.
/// </summary>
public partial class CodeToolsPanel : UserControl
{
    public event Action? DockLeftRequested;
    public event Action? DockRightRequested;
    public event Action? FloatRequested;
    public event Action? CloseRequested;
    public event Action<FileExplorerItemViewModel>? FileActivated;
    public event Action<BoardSearchResultViewModel>? BoardSearchActivated;

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public CodeToolsPanel() => InitializeComponent();

    private void OnDockLeft(object sender, RoutedEventArgs e) => DockLeftRequested?.Invoke();
    private void OnDockRight(object sender, RoutedEventArgs e) => DockRightRequested?.Invoke();
    private void OnFloat(object sender, RoutedEventArgs e) => FloatRequested?.Invoke();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // File open needs the host's image/text-file helpers + canvas, so it's surfaced as an event.
    private void OnExplorerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView { SelectedItem: FileExplorerItemViewModel item })
            FileActivated?.Invoke(item);
    }

    private async void OnSymbolsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is TreeView { SelectedItem: SymbolExplorerItemViewModel sym } && sym.StartLine.HasValue)
            await Vm.AddSymbolToCanvasAsync(sym);
    }

    // Board-search navigation frames the canvas, which only the host can do.
    private void OnBoardSearchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BoardSearchResultViewModel result })
            BoardSearchActivated?.Invoke(result);
    }

    private void OnExplorerSearchKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Return || e.Key == Key.Enter) && Vm is not null)
            Vm.ApplyExplorerSearchCommand.Execute(null);
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e) => Vm?.SetExplorerExpanded(false);
    private void OnExpandAll(object sender, RoutedEventArgs e) => Vm?.SetExplorerExpanded(true);

    private async void OnBranchComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || Vm is null) return;
        string branch = e.AddedItems[0] as string ?? string.Empty;
        await Vm.LoadFromSelectedBranchAsync(branch);
    }
}
