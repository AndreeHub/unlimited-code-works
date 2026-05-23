using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels.Inspectors;

public sealed partial class StickyNoteInspectorViewModel : InspectorViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private double _fontSize = 12.5;
    [ObservableProperty] private string _fill = "#FFF3C7";
    [ObservableProperty] private string _textColor = "#3C3412";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isLocked;

    public StickyNoteInspectorViewModel(MainWindowViewModel parent) : base(parent)
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
            Body = block.Body ?? string.Empty;
            X = Math.Round(block.X);
            Y = Math.Round(block.Y);
            Width = Math.Round(block.Width);
            Height = Math.Round(block.Height);
            IsLocked = block.IsLocked;

            var style = block.Style ?? new BoardItemStyle();
            FontSize = style.FontSize;
            Fill = style.Fill;
            TextColor = style.Text;
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

        var style = block.Style ?? new BoardItemStyle();
        var nextStyle = style with
        {
            FontSize = FontSize,
            Fill = Fill,
            Text = TextColor
        };

        var nextBlock = block with
        {
            Title = Title,
            Body = Body,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            IsLocked = IsLocked,
            Style = nextStyle
        };

        Parent.UpdateSceneBlock(nextBlock, "Updated sticky note properties");
    }
}
