using CommunityToolkit.Mvvm.ComponentModel;
using ReviewScope.Domain;
using System;

namespace ReviewScope.App.ViewModels.Inspectors;

public partial class TextBlockInspectorViewModel : InspectorViewModelBase
{
    /// <summary>The undo-history label written when <see cref="ApplyChanges"/> commits.
    /// Subclasses override to disambiguate (e.g. "Updated sticky note properties").</summary>
    protected virtual string ApplyChangesActionDescription => "Updated text card properties";

    /// <summary>When true, the visible Title is auto-derived from the first ~40 chars of
    /// the body (text-card behavior). Subclasses set false to keep Title fully user-editable
    /// (sticky-note behavior).</summary>
    protected virtual bool DeriveTitleFromBody => true;

    // Captured style buffer for Copy / Paste text style across selections.
    private static BoardItemStyle? s_copiedStyle;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;

    // --- Font ---
    [ObservableProperty] private string _fontFamily = "Helvetica";
    [ObservableProperty] private double _fontSize = 12;
    [ObservableProperty] private bool _bold;
    [ObservableProperty] private bool _italic;
    [ObservableProperty] private bool _underline;
    [ObservableProperty] private bool _strikethrough;

    // --- Alignment & layout ---
    [ObservableProperty] private string _textAlignment = "Center";       // Left | Center | Right
    [ObservableProperty] private string _verticalAlignment = "Middle";   // Top | Middle | Bottom
    [ObservableProperty] private string _position = "Center";
    [ObservableProperty] private string _writingDirection = "Automatic";

    // --- Colors (with enabled toggles) ---
    [ObservableProperty] private bool _fontColorEnabled = true;
    [ObservableProperty] private string _textColor = "#111827";

    [ObservableProperty] private bool _backgroundColorEnabled;
    [ObservableProperty] private string _fill = "#FFFFFF";

    [ObservableProperty] private bool _borderColorEnabled;
    [ObservableProperty] private string _stroke = "#CBD5E1";

    [ObservableProperty] private bool _shadowEnabled;

    // --- Toggles ---
    [ObservableProperty] private bool _wordWrap = true;
    [ObservableProperty] private bool _formattedText = true;
    [ObservableProperty] private bool _outlineEnabled;
    [ObservableProperty] private bool _convertLabelsToSvg;
    [ObservableProperty] private bool _automaticFontSize;

    // --- Card style legacy (still applied, just no longer surfaced as before) ---
    [ObservableProperty] private string _fillStyle = "solid";
    [ObservableProperty] private double _strokeWidth = 1.0;
    [ObservableProperty] private bool _dashed;
    [ObservableProperty] private double _cornerRadius = 4.0;
    [ObservableProperty] private double _opacity = 1.0;

    // --- Spacing ---
    [ObservableProperty] private double _spacingTop = 4;
    [ObservableProperty] private double _spacingRight = 4;
    [ObservableProperty] private double _spacingBottom = 4;
    [ObservableProperty] private double _spacingLeft = 4;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _isLocked;

    public TextBlockInspectorViewModel(MainWindowViewModel parent) : base(parent)
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
            FontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? "Helvetica" : style.FontFamily;
            FontSize = style.FontSize;
            Bold = style.Bold;
            Italic = style.Italic;
            Underline = style.Underline;
            Strikethrough = style.Strikethrough;

            TextAlignment = style.TextAlign ?? "Center";
            VerticalAlignment = string.IsNullOrWhiteSpace(style.VerticalAlign) ? "Middle" : style.VerticalAlign;
            Position = string.IsNullOrWhiteSpace(style.Position) ? "Center" : style.Position;
            WritingDirection = string.IsNullOrWhiteSpace(style.WritingDirection) ? "Automatic" : style.WritingDirection;

            FontColorEnabled = style.FontColorEnabled;
            TextColor = style.Text ?? "#111827";

            BackgroundColorEnabled = style.BackgroundColorEnabled;
            Fill = style.Fill ?? "#FFFFFF";

            BorderColorEnabled = style.BorderColorEnabled;
            Stroke = style.Stroke ?? "#CBD5E1";

            ShadowEnabled = style.ShadowEnabled;
            WordWrap = style.WordWrap;
            FormattedText = style.FormattedText;
            OutlineEnabled = style.OutlineEnabled;
            ConvertLabelsToSvg = style.ConvertLabelsToSvg;
            AutomaticFontSize = style.AutoFontSize;

            FillStyle = style.FillStyle ?? "solid";
            StrokeWidth = style.StrokeWidth;
            Dashed = style.Dashed;
            CornerRadius = style.CornerRadius;
            Opacity = style.Opacity;

            SpacingTop = style.SpacingTop;
            SpacingRight = style.SpacingRight;
            SpacingBottom = style.SpacingBottom;
            SpacingLeft = style.SpacingLeft;
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
            FontFamily = FontFamily,
            FontSize = FontSize,
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            Strikethrough = Strikethrough,
            TextAlign = TextAlignment,
            VerticalAlign = VerticalAlignment,
            Position = Position,
            WritingDirection = WritingDirection,
            Text = TextColor,
            FontColorEnabled = FontColorEnabled,
            Fill = Fill,
            BackgroundColorEnabled = BackgroundColorEnabled,
            Stroke = Stroke,
            BorderColorEnabled = BorderColorEnabled,
            ShadowEnabled = ShadowEnabled,
            WordWrap = WordWrap,
            FormattedText = FormattedText,
            OutlineEnabled = OutlineEnabled,
            ConvertLabelsToSvg = ConvertLabelsToSvg,
            AutoFontSize = AutomaticFontSize,
            FillStyle = FillStyle,
            StrokeWidth = StrokeWidth,
            Dashed = Dashed,
            CornerRadius = CornerRadius,
            Opacity = Opacity,
            SpacingTop = SpacingTop,
            SpacingRight = SpacingRight,
            SpacingBottom = SpacingBottom,
            SpacingLeft = SpacingLeft
        };

        if (DeriveTitleFromBody)
        {
            string nextTitle = Body;
            if (nextTitle.Length > 40)
                nextTitle = nextTitle.Substring(0, 37) + "...";
            else if (string.IsNullOrWhiteSpace(nextTitle))
                nextTitle = "Text";

            if (Title != nextTitle)
            {
                IsRefreshing = true;
                try { Title = nextTitle; }
                finally { IsRefreshing = false; }
            }
        }

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

        Parent.UpdateSceneBlock(nextBlock, ApplyChangesActionDescription);
    }

    // --- Copy / Clear style ---

    public void CopyTextStyle()
    {
        var block = Parent.SelectedBlock;
        if (block is null) return;
        s_copiedStyle = block.Style ?? new BoardItemStyle();
    }

    public void PasteTextStyle()
    {
        if (s_copiedStyle is null) return;
        IsRefreshing = true;
        try
        {
            var s = s_copiedStyle;
            FontFamily = s.FontFamily;
            FontSize = s.FontSize;
            Bold = s.Bold;
            Italic = s.Italic;
            Underline = s.Underline;
            Strikethrough = s.Strikethrough;
            TextAlignment = s.TextAlign;
            VerticalAlignment = s.VerticalAlign;
            Position = s.Position;
            WritingDirection = s.WritingDirection;
            FontColorEnabled = s.FontColorEnabled;
            TextColor = s.Text;
            BackgroundColorEnabled = s.BackgroundColorEnabled;
            Fill = s.Fill;
            BorderColorEnabled = s.BorderColorEnabled;
            Stroke = s.Stroke;
            ShadowEnabled = s.ShadowEnabled;
            WordWrap = s.WordWrap;
            FormattedText = s.FormattedText;
            OutlineEnabled = s.OutlineEnabled;
            ConvertLabelsToSvg = s.ConvertLabelsToSvg;
            AutomaticFontSize = s.AutoFontSize;
            Opacity = s.Opacity;
            SpacingTop = s.SpacingTop;
            SpacingRight = s.SpacingRight;
            SpacingBottom = s.SpacingBottom;
            SpacingLeft = s.SpacingLeft;
        }
        finally { IsRefreshing = false; }
        ApplyChanges();
    }

    /// <summary>
    /// Apply a one-click style preset (fill + stroke + text color combo). Enables
    /// background + border colors so the change is immediately visible.
    /// </summary>
    public void ApplyPreset(string fillHex, string strokeHex, string textHex)
    {
        IsRefreshing = true;
        try
        {
            Fill = fillHex;
            Stroke = strokeHex;
            TextColor = textHex;
            BackgroundColorEnabled = true;
            BorderColorEnabled = true;
            FontColorEnabled = true;
        }
        finally { IsRefreshing = false; }
        ApplyChanges();
    }

    public void ClearFormatting()
    {
        IsRefreshing = true;
        try
        {
            var d = new BoardItemStyle();
            FontFamily = d.FontFamily;
            FontSize = d.FontSize;
            Bold = false;
            Italic = false;
            Underline = false;
            Strikethrough = false;
            TextAlignment = d.TextAlign;
            VerticalAlignment = d.VerticalAlign;
            Position = d.Position;
            WritingDirection = d.WritingDirection;
            FontColorEnabled = true;
            TextColor = d.Text;
            BackgroundColorEnabled = false;
            BorderColorEnabled = false;
            ShadowEnabled = false;
            WordWrap = true;
            FormattedText = true;
            OutlineEnabled = false;
            ConvertLabelsToSvg = false;
            AutomaticFontSize = false;
            Opacity = 1;
            SpacingTop = d.SpacingTop;
            SpacingRight = d.SpacingRight;
            SpacingBottom = d.SpacingBottom;
            SpacingLeft = d.SpacingLeft;
        }
        finally { IsRefreshing = false; }
        ApplyChanges();
    }
}
