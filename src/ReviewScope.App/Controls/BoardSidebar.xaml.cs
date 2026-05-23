using ReviewScope.App.ViewModels;
using ReviewScope.Canvas;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DrawingColor = System.Drawing.Color;
using DrawingColorTranslator = System.Drawing.ColorTranslator;
using Forms = System.Windows.Forms;

namespace ReviewScope.App.Controls;

public partial class BoardSidebar : UserControl
{
    public static readonly DependencyProperty CameraProperty =
        DependencyProperty.Register(
            nameof(Camera),
            typeof(CameraState),
            typeof(BoardSidebar),
            new FrameworkPropertyMetadata(CameraState.Default, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ViewportWidthProperty =
        DependencyProperty.Register(nameof(ViewportWidth), typeof(double), typeof(BoardSidebar), new PropertyMetadata(0d));

    public static readonly DependencyProperty ViewportHeightProperty =
        DependencyProperty.Register(nameof(ViewportHeight), typeof(double), typeof(BoardSidebar), new PropertyMetadata(0d));

    public BoardSidebar()
    {
        InitializeComponent();
    }

    public CameraState Camera
    {
        get => (CameraState)GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public double ViewportWidth
    {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    public double ViewportHeight
    {
        get => (double)GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnPickColorCustom(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        string currentHex = vm.SelectedStyleTarget switch
        {
            "Stroke" => vm.SelectedStroke,
            "Text" => vm.SelectedTextColor,
            _ => vm.SelectedFill
        };

        if (TryPickColor(currentHex, out string hex))
        {
            ApplySelectedStyleColor(vm, hex);
            await vm.ApplySelectionPropertiesAsync();
        }
    }

    private static bool TryPickColor(string currentHex, out string hex)
    {
        hex = currentHex;
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        try
        {
            dialog.Color = ParseDialogColor(currentHex);
        }
        catch
        {
            dialog.Color = DrawingColor.White;
        }

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return false;

        DrawingColor color = dialog.Color;
        hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }

    private static DrawingColor ParseDialogColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("#00000000", StringComparison.OrdinalIgnoreCase))
            return DrawingColor.White;

        string value = hex.Trim();
        if (value.StartsWith('#'))
            value = value[1..];

        if (value.Length == 8)
            value = value[2..];

        return value.Length == 6
            ? DrawingColorTranslator.FromHtml($"#{value}")
            : DrawingColor.White;
    }

    private async void OnSelectColorPreset(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button btn || btn.CommandParameter is not string hex)
            return;

        ApplySelectedStyleColor(vm, hex);
        await vm.ApplySelectionPropertiesAsync();
    }

    private void OnSelectStyleTarget(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is RadioButton radio && radio.Tag is string target)
            vm.SelectedStyleTarget = target;
    }

    private static void ApplySelectedStyleColor(MainWindowViewModel vm, string hex)
    {
        if (vm.SelectedStyleTarget == "Stroke")
            vm.SelectedStroke = hex;
        else if (vm.SelectedStyleTarget == "Text")
            vm.SelectedTextColor = hex;
        else
            vm.SelectedFill = hex;
    }

    private async void OnSelectStrokeWidth(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button btn || btn.CommandParameter is not string width)
            return;

        vm.SelectedStrokeWidth = width;
        await vm.ApplySelectionPropertiesAsync();
    }

    private async void OnApplySelectionProperties(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.ApplySelectionPropertiesAsync();
    }

    private async void OnApplySelectionPropertiesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (ViewModel is { } vm)
        {
            e.Handled = true;
            await vm.ApplySelectionPropertiesAsync();
        }
    }

    private async void OnSelectTextAlignment(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button btn || btn.CommandParameter is not string alignment)
            return;

        vm.SelectedTextAlignment = alignment;
        await vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectStrokeStyle(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button btn || btn.CommandParameter is not string style)
            return;

        vm.SelectedDashed = style == "dashed";
        await vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectFillStyle(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button btn || btn.CommandParameter is not string style)
            return;

        vm.SelectedFillStyle = style;
        await vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectCornerRadius(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm ||
            sender is not Button btn ||
            btn.CommandParameter is not string rStr ||
            !double.TryParse(rStr, out double r))
        {
            return;
        }

        vm.SelectedCornerRadius = r;
        await vm.ApplySelectionPropertiesAsync();
    }

    private void OnPickColorForActiveInspector(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string propName) return;
        if (ViewModel?.ActiveInspector is not { } inspector) return;

        var prop = inspector.GetType().GetProperty(propName);
        if (prop is null) return;

        string currentHex = prop.GetValue(inspector) as string ?? "#FFFFFF";
        if (TryPickColor(currentHex, out string newHex))
        {
            prop.SetValue(inspector, newHex);
        }
    }
}
