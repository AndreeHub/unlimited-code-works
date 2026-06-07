using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;
using System.Collections.ObjectModel;
using System.IO;

namespace ReviewScope.App.ViewModels;

public sealed partial class FileExplorerItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    public FileExplorerItemViewModel(string name, string? filePath, bool isFile, bool isProjectRoot = false)
    {
        Name = name;
        FilePath = filePath;
        IsFile = isFile;
        IsProjectRoot = isProjectRoot;
        IconKind = ResolveIconKind(name, filePath, isFile, isProjectRoot);
        KindLabel = ResolveKindLabel(name, filePath, isFile, isProjectRoot);
    }

    public string Name { get; }
    public string? FilePath { get; }
    public bool IsFile { get; }
    public bool IsFolder => !IsFile;
    public bool IsProjectRoot { get; }
    public string IconKind { get; }
    public string KindLabel { get; }

    public ObservableCollection<FileExplorerItemViewModel> Children { get; } = new();

    private static string ResolveIconKind(string name, string? filePath, bool isFile, bool isProjectRoot)
    {
        if (isProjectRoot) return "project";
        if (!isFile) return "folder";

        string fileName = Path.GetFileName(filePath ?? name);
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) return "solution";
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase)) return "project-file";
        if (fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) return "xaml";

        return extension switch
        {
            ".cs" => "csharp",
            ".md" => "markdown",
            ".markdown" => "markdown",
            ".json" => "json",
            ".xml" => "xml",
            ".props" => "xml",
            ".targets" => "xml",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "image",
            _ => "file"
        };
    }

    private static string ResolveKindLabel(string name, string? filePath, bool isFile, bool isProjectRoot)
    {
        if (isProjectRoot) return "Project";
        if (!isFile) return "Folder";

        string fileName = Path.GetFileName(filePath ?? name);
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) return "Solution";
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return "C# project";
        if (fileName.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase)) return "Shared project";
        if (fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) return "XAML";

        return extension switch
        {
            ".cs" => "C#",
            ".md" or ".markdown" => "Markdown",
            ".json" => "JSON",
            ".xml" or ".props" or ".targets" => "XML",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "Image",
            _ => "File"
        };
    }
}

public sealed partial class ProjectBrowserItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;

    private ProjectBrowserItemViewModel(string name, string iconKind, string kindLabel, ReviewSession? session = null)
    {
        Name = name;
        IconKind = iconKind;
        KindLabel = kindLabel;
        Session = session;
    }

    public string Name { get; }
    public string IconKind { get; }
    public string KindLabel { get; }
    public ReviewSession? Session { get; }
    public bool IsDocument => Session is not null;

    public ObservableCollection<ProjectBrowserItemViewModel> Children { get; } = new();

    public static ProjectBrowserItemViewModel Project(string name) => new(name, "project", "UBW Project");

    public static ProjectBrowserItemViewModel Group(string name, string iconKind) => new(name, iconKind, name);

    public static ProjectBrowserItemViewModel Document(ReviewSession session)
    {
        string iconKind = session.Kind switch
        {
            DocumentKind.Canvas => "board",
            DocumentKind.Journal => "journal",
            _ => "page"
        };

        return new ProjectBrowserItemViewModel(session.Name, iconKind, session.Kind.ToString(), session);
    }
}

public sealed partial class SymbolExplorerItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    public SymbolExplorerItemViewModel(string name, string detail, int? startLine = null, int? endLine = null, string? iconKind = null)
    {
        Name = name;
        Detail = detail;
        StartLine = startLine;
        EndLine = endLine;
        IconKind = string.IsNullOrWhiteSpace(iconKind) ? ResolveIconKind(name, detail) : ResolveIconKind(iconKind, detail);
    }

    public string Name { get; }
    public string Detail { get; }
    public int? StartLine { get; }
    public int? EndLine { get; }
    public string IconKind { get; }
    public string RangeText => StartLine.HasValue && EndLine.HasValue ? $"Lines {StartLine}-{EndLine}" : Detail;

    public ObservableCollection<SymbolExplorerItemViewModel> Children { get; } = new();

    private static string ResolveIconKind(string nameOrKind, string detail)
    {
        string value = $"{nameOrKind} {detail}".ToLowerInvariant();
        if (value.Contains("no symbols")) return "empty";
        if (value.Contains("interface")) return "interface";
        if (value.Contains("enum")) return "enum";
        if (value.Contains("struct")) return "struct";
        if (value.Contains("record")) return "record";
        if (value.Contains("class")) return "class";
        if (value.Contains("constructor")) return "constructor";
        if (value.Contains("method")) return "method";
        if (value.Contains("property")) return "property";
        if (value.Contains("field")) return "field";
        return "symbol";
    }
}

public sealed class BoardSearchResultViewModel
{
    public BoardSearchResultViewModel(string title, string detail, string key, string iconKind = "search")
    {
        Title = title;
        Detail = detail;
        Key = key;
        IconKind = iconKind;
    }

    public string Title { get; }
    public string Detail { get; }
    public string Key { get; }
    public string IconKind { get; }
}

public sealed class BoardFileUsageViewModel
{
    public BoardFileUsageViewModel(string title, string detail)
    {
        Title = title;
        Detail = detail;
    }

    public string Title { get; }
    public string Detail { get; }
}

/// <summary>One selectable bullet in the transclusion (block-reference) picker. Identifies
/// the source document and the bullet's line within it, so an anchor can be allocated on
/// demand if the bullet doesn't have one yet.</summary>
public sealed class TransclusionCandidateViewModel
{
    public TransclusionCandidateViewModel(Guid documentId, string documentName, int lineIndex, string? anchorId, string preview, int depth,
        bool isPage = false, string? pageName = null)
    {
        DocumentId = documentId;
        DocumentName = documentName;
        LineIndex = lineIndex;
        AnchorId = anchorId;
        Preview = preview;
        Depth = depth;
        IsPage = isPage;
        PageName = pageName;
    }

    public Guid DocumentId { get; }
    public string DocumentName { get; }
    public int LineIndex { get; }
    public string? AnchorId { get; }
    public string Preview { get; }
    public int Depth { get; }

    /// <summary>True when this entry is a whole PAGE (drops a page portal), not a single bullet.</summary>
    public bool IsPage { get; }

    /// <summary>The referenced page's name when <see cref="IsPage"/> is true.</summary>
    public string? PageName { get; }

    /// <summary>Visual indent applied in the picker so the bullet hierarchy reads at a glance.</summary>
    public System.Windows.Thickness Indent => new(Depth * 14.0, 0, 0, 0);
}
