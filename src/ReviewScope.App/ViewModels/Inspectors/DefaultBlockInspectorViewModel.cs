using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels.Inspectors;

public sealed partial class DefaultBlockInspectorViewModel : InspectorViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _lineRange = string.Empty;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isLocked;

    public DefaultBlockInspectorViewModel(MainWindowViewModel parent) : base(parent)
    {
        Refresh();
    }

    public override void Refresh()
    {
        var block = Parent.SelectedBlock;
        if (block is null) return;

        IsRefreshing = true;
        try
        {
            Title = block.Title;
            Subtitle = block.Subtitle;
            FilePath = block.FilePath ?? string.Empty;
            LineRange = block.StartLine.HasValue && block.EndLine.HasValue
                ? $"Lines {block.StartLine.Value}-{block.EndLine.Value}"
                : string.Empty;
            X = Math.Round(block.X);
            Y = Math.Round(block.Y);
            Width = Math.Round(block.Width);
            Height = Math.Round(block.Height);
            IsLocked = block.IsLocked;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public override void ApplyChanges()
    {
        var block = Parent.SelectedBlock;
        if (block is null) return;

        // Lock fans out to the whole selection (a sensible bulk action); identity and geometry are
        // per-item and stay on the primary selection.
        Parent.UpdateSelectedBlocks((b, isPrimary) =>
        {
            var next = b with { IsLocked = IsLocked };
            if (isPrimary)
                next = next with
                {
                    Title = Title,
                    // Shapes display their Body as the in-block label; keep it in sync so editing the
                    // "Label Text" field is actually visible on the shape.
                    Body = b.Kind == BlockKind.Shape ? Title : b.Body,
                    X = X,
                    Y = Y,
                    Width = Width,
                    Height = Height
                };
            return next;
        }, "Updated item properties");
    }
}
