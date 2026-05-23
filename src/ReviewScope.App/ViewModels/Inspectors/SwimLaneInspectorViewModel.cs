using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels.Inspectors;

public sealed partial class SwimLaneInspectorViewModel : InspectorViewModelBase
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _color = "#4A90D9";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;

    public SwimLaneInspectorViewModel(MainWindowViewModel parent) : base(parent)
    {
        Refresh();
    }

    public override void Refresh()
    {
        var lane = Parent.SelectedSwimLane;
        if (lane is null) return;

        IsRefreshing = true;
        try
        {
            Name = lane.Name;
            Color = lane.Color;
            X = Math.Round(lane.X);
            Y = Math.Round(lane.Y);
            Width = Math.Round(lane.Width);
            Height = Math.Round(lane.Height);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public override void ApplyChanges()
    {
        var lane = Parent.SelectedSwimLane;
        if (lane is null) return;

        var nextLane = lane with
        {
            Name = Name,
            Color = Color,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height
        };

        Parent.UpdateSceneSwimLane(nextLane, "Updated frame properties");
    }
}
