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

    /// <summary>Per-shape render look: "auto" follows the canvas-wide default, "sketch" forces the
    /// hand-drawn look, "vector" forces the crisp look.</summary>
    [ObservableProperty] private string _renderStyle = "auto";

    // --- Label text settings (rendered inside the shape) ---
    [ObservableProperty] private string _textColor = "#111827";
    [ObservableProperty] private double _fontSize = 16;
    [ObservableProperty] private string _textAlignment = "Center";     // Left | Center | Right
    [ObservableProperty] private string _verticalAlignment = "Middle"; // Top | Middle | Bottom

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isLinearShape;

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
            IsLinearShape = IsLinearShapeType(ShapeType);
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
            RenderStyle = string.IsNullOrWhiteSpace(style.RenderStyle) ? "auto" : style.RenderStyle;

            TextColor = string.IsNullOrWhiteSpace(style.Text) ? "#111827" : style.Text;
            FontSize = style.FontSize <= 0 ? 16 : style.FontSize;
            TextAlignment = string.IsNullOrWhiteSpace(style.TextAlign) ? "Center" : style.TextAlign;
            VerticalAlignment = string.IsNullOrWhiteSpace(style.VerticalAlign) ? "Middle" : style.VerticalAlign;
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

        // Style (fill, stroke, corners, opacity, label text) fans out to every selected block so a
        // multi-selection can be recolored or restyled in one step. Shape type also fans out, but only
        // to shape-kind blocks (it is meaningless on text cards / notes that may be co-selected).
        // Per-item fields (title, geometry, lock) stay on the primary selection.
        Parent.UpdateSelectedBlocks((b, isPrimary) =>
        {
            var style = b.Style ?? new BoardItemStyle();
            var nextStyle = style with
            {
                Fill = Fill,
                FillStyle = FillStyle,
                Stroke = Stroke,
                StrokeWidth = StrokeWidth,
                Dashed = Dashed,
                CornerRadius = CornerRadius,
                Opacity = Opacity,
                HatchOpacity = HatchOpacity,
                RenderStyle = RenderStyle == "auto" ? null : RenderStyle,
                Text = TextColor,
                FontSize = FontSize,
                TextAlign = TextAlignment,
                VerticalAlign = VerticalAlignment
            };

            var next = b with { Style = nextStyle };
            if (b.Kind == BlockKind.Shape)
                next = next with { ShapeType = ShapeType };
            if (isPrimary)
                next = next with
                {
                    Title = Title,
                    X = X,
                    Y = Y,
                    Width = Width,
                    Height = Height,
                    IsLocked = IsLocked
                };
            return next;
        }, "Updated shape properties");
    }

    partial void OnShapeTypeChanged(string value)
    {
        IsLinearShape = IsLinearShapeType(value);
    }

    private static bool IsLinearShapeType(string? shapeType) =>
        shapeType is "line" or "arrow" or "polyline";
}
