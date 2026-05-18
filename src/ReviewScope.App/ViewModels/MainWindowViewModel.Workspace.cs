using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using System.Collections.ObjectModel;
using System.IO;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // -----------------------------------------------------------------------
    // Workspace loading
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task OpenWorkspaceAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Workspace",
            Filter = "Solution / Project|*.sln;*.csproj|All Files|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        await LoadWorkspaceAsync(dlg.FileName);
    }

    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Open Folder" };
        if (dlg.ShowDialog() != true) return;
        await LoadWorkspaceAsync(dlg.FolderName);
    }

    private async Task LoadWorkspaceAsync(string path)
    {
        StatusMessage = "Loading workspaceâ€¦";
        try
        {
            var ct = CancellationToken.None;
            _currentSnapshot = await _workspace.LoadAsync(path, ct);
            WorkspacePath = _currentSnapshot.DisplayName;
            StatusMessage = $"Loaded: {_currentSnapshot.DisplayName}  â€¢  {_currentSnapshot.Files.Count} files";

            BuildExplorer(_currentSnapshot);
            await LoadSessionsAsync(_currentSnapshot.WorkspaceKey, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to load workspace.");
        }
    }

    private void BuildExplorer(WorkspaceSnapshot snapshot)
    {
        ExplorerRoots.Clear();
        var grouped = snapshot.Files.GroupBy(f => f.ClusterKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new FileExplorerItemViewModel(group.Key, null, isFile: false) { IsExpanded = true };
            foreach (var file in group.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                AddFileToExplorer(folder, file);
            }
            ExplorerRoots.Add(folder);
        }

        SymbolRoots.Clear();
        SelectedSymbolsHeader = "Symbols";
    }

    private static void AddFileToExplorer(FileExplorerItemViewModel projectRoot, WorkspaceFileSummary file)
    {
        string rel = file.RelativePath;
        string projectMarker = file.ProjectName.Replace('\\', '/');
        int markerIndex = rel.IndexOf(projectMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            rel = rel[(markerIndex + projectMarker.Length)..].TrimStart('/');

        string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            projectRoot.Children.Add(new FileExplorerItemViewModel(Path.GetFileName(file.FilePath), file.FilePath, isFile: true));
            return;
        }

        var parent = projectRoot;
        foreach (string segment in segments.Take(segments.Length - 1))
        {
            var folder = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new FileExplorerItemViewModel(segment, null, isFile: false);
                parent.Children.Add(folder);
            }
            parent = folder;
        }

        parent.Children.Add(new FileExplorerItemViewModel(segments[^1], file.FilePath, isFile: true));
    }

    public async Task LoadSymbolsForFileAsync(string filePath)
    {
        SymbolRoots.Clear();
        SelectedSymbolsHeader = $"Symbols: {Path.GetFileName(filePath)}";

        var structure = await _fileStructure.GetFileStructureAsync(filePath, CancellationToken.None);
        if (structure is null || structure.Types.Count == 0)
        {
            SymbolRoots.Add(new SymbolExplorerItemViewModel("No symbols found", "Try another source file"));
            return;
        }

        foreach (var type in structure.Types)
        {
            var typeItem = new SymbolExplorerItemViewModel($"{type.Kind} {type.Name}", $"Lines {type.StartLine}-{type.EndLine}", type.StartLine, type.EndLine);
            foreach (var method in type.Methods)
                typeItem.Children.Add(new SymbolExplorerItemViewModel(method.Signature, method.Kind, method.StartLine, method.EndLine));
            typeItem.IsExpanded = true;
            SymbolRoots.Add(typeItem);
        }
    }
}
