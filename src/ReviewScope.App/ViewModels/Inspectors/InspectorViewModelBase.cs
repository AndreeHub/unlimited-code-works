using CommunityToolkit.Mvvm.ComponentModel;

namespace ReviewScope.App.ViewModels.Inspectors;

public abstract partial class InspectorViewModelBase : ObservableObject
{
    protected readonly MainWindowViewModel Parent;

    public MainWindowViewModel ParentVm => Parent;
    protected bool IsRefreshing;

    protected InspectorViewModelBase(MainWindowViewModel parent)
    {
        Parent = parent;
    }

    /// <summary>
    /// Re-reads block or connection properties to synchronize with the canvas state.
    /// </summary>
    public abstract void Refresh();

    /// <summary>
    /// Applies changes from the inspector properties back to the canvas item.
    /// </summary>
    public abstract void ApplyChanges();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!IsRefreshing)
        {
            ApplyChanges();
        }
    }
}
