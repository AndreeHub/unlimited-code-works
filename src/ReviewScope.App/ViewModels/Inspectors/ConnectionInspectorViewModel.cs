using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels.Inspectors;

public sealed partial class ConnectionInspectorViewModel : InspectorViewModelBase
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _stroke = "#4584CB";
    [ObservableProperty] private bool _dashed;
    [ObservableProperty] private string _routeKind = "Curved";
    [ObservableProperty] private string _arrowKind = "Forward";

    public ConnectionInspectorViewModel(MainWindowViewModel parent) : base(parent)
    {
        Refresh();
    }

    public override void Refresh()
    {
        var conn = Parent.SelectedConnection;
        if (conn is null) return;

        IsRefreshing = true;
        try
        {
            Label = conn.Label ?? string.Empty;
            Stroke = conn.Stroke;
            Dashed = conn.Dashed;
            RouteKind = conn.RouteKind.ToString();
            ArrowKind = conn.ArrowKind.ToString();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public override void ApplyChanges()
    {
        var conn = Parent.SelectedConnection;
        if (conn is null) return;

        Enum.TryParse(RouteKind, out ConnectorRouteKind rKind);
        Enum.TryParse(ArrowKind, out ConnectorArrowKind aKind);

        var nextConn = conn with
        {
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            Stroke = Stroke,
            Dashed = Dashed,
            RouteKind = rKind,
            ArrowKind = aKind,
            MidControlBends = rKind == ConnectorRouteKind.Curved && conn.MidControlBends
        };

        Parent.UpdateSceneConnection(nextConn, "Updated connector properties");
    }
}
