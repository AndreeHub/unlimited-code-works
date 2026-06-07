using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels;

/// <summary>
/// A single Chrome-style tab in a pane's tab strip. Keyed on the stable document
/// <see cref="Id"/> (not the immutable <see cref="ReviewSession"/> record, which is replaced
/// by a new instance on every edit). <see cref="Name"/> and <see cref="IsActive"/> are observable
/// so renames and focus changes update the strip without rebuilding it.
/// </summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    public Guid Id { get; }
    public DocumentKind Kind { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isActive;

    /// <summary>True for canvas boards — lets the tab template pick the board vs. page glyph.</summary>
    public bool IsCanvas => Kind == DocumentKind.Canvas;

    public DocumentTabViewModel(Guid id, string name, DocumentKind kind)
    {
        Id = id;
        _name = name;
        Kind = kind;
    }
}
