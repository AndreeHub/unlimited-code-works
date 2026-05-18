using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace ReviewScope.App.ViewModels;

public sealed partial class FileExplorerItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    public FileExplorerItemViewModel(string name, string? filePath, bool isFile)
    {
        Name = name;
        FilePath = filePath;
        IsFile = isFile;
    }

    public string Name { get; }
    public string? FilePath { get; }
    public bool IsFile { get; }
    public bool IsFolder => !IsFile;

    public ObservableCollection<FileExplorerItemViewModel> Children { get; } = new();
}
