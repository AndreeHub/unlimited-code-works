namespace ReviewScope.App.ViewModels.Inspectors;

/// <summary>
/// Sticky-note inspector shares the entire formatting surface (font, alignment,
/// colors, spacing, geometry, etc.) with the Text-card inspector. The only things
/// that differ are: (a) the undo-history label written on every change, and (b)
/// whether the Title is auto-derived from the body — Text cards derive, sticky
/// notes keep the title as a separate editable field.
/// </summary>
public sealed class StickyNoteInspectorViewModel : TextBlockInspectorViewModel
{
    public StickyNoteInspectorViewModel(MainWindowViewModel parent) : base(parent) { }

    protected override string ApplyChangesActionDescription => "Updated sticky note properties";
    protected override bool DeriveTitleFromBody => false;
}
