using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels.Inspectors;

public sealed partial class ShapeInspectorViewModel : InspectorViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _shapeType = "rectangle";
    [ObservableProperty] private string _fill = "#FFFFFF";
    [ObservableProperty] private string _fillStyle = "solid";
    [ObservableProperty] private string _stroke = "#2E7DD7";
    [ObservableProperty] private double _strokeWidth = 1.4;
    [ObservableProperty] private bool _dashed;
    [ObservableProperty] private double _cornerRadius = 3;
    [ObservableProperty] private double _opacity = 1.0;
    [ObservableProperty] private double _hatchOpacity = 0.6;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isLocked;

    public ShapeInspectorViewModel(MainWindowViewModel parent) : base(parent)
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
            ShapeType = block.ShapeType ?? "rectangle";
            X = Math.Round(block.X);
            Y = Math.Round(block.Y);
            Width = Math.Round(block.Width);
            Height = Math.Round(block.Height);
            IsLocked = block.IsLocked;

            var style = block.Style ?? new BoardItemStyle();
            Fill = style.Fill;
            FillStyle = style.FillStyle ?? "solid";
            Stroke = style.Stroke;
            StrokeWidth = style.StrokeWidth;
            Dashed = style.Dashed;
            CornerRadius = style.CornerRadius;
            Opacity = style.Opacity;
            HatchOpacity = style.HatchOpacity;
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
            Fill = Fill,
            FillStyle = FillStyle,
            Stroke = Stroke,
            StrokeWidth = StrokeWidth,
            Dashed = Dashed,
            CornerRadius = CornerRadius,
            Opacity = Opacity,
            HatchOpacity = HatchOpacity
        };

        var nextBlock = block with
        {
            Title = Title,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            IsLocked = IsLocked,
            Style = nextStyle
        };

        Parent.UpdateSceneBlock(nextBlock, "Updated shape properties");
    }
}
