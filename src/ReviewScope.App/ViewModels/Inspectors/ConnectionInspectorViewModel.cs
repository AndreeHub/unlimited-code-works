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

        // Color / line-style / route / arrow fan out across every selected connector so you can
        // recolor or retype a whole bundle of edges at once. The label reflects the primary
        // connector only, so it is written back to that one alone (avoids smearing one label
        // across the whole selection).
        Parent.UpdateSelectedConnections((c, isPrimary) =>
        {
            var next = c with
            {
                Stroke = Stroke,
                Dashed = Dashed,
                RouteKind = rKind,
                ArrowKind = aKind,
                MidControlBends = rKind == ConnectorRouteKind.Curved && c.MidControlBends
            };
            return isPrimary
                ? next with { Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim() }
                : next;
        }, "Updated connector properties");
    }
}
