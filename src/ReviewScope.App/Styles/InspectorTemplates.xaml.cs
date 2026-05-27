using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ReviewScope.App.ViewModels.Inspectors;
using DrawingColor = System.Drawing.Color;
using DrawingColorTranslator = System.Drawing.ColorTranslator;
using Forms = System.Windows.Forms;

namespace ReviewScope.App.Styles;

public partial class InspectorTemplates : ResourceDictionary
{
    public InspectorTemplates()
    {
        InitializeComponent();
    }

    private void OnPickColorForActiveInspector(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string propName) return;
        if (fe.DataContext is not InspectorViewModelBase inspector) return;

        var prop = inspector.GetType().GetProperty(propName);
        if (prop is null) return;

        string currentHex = prop.GetValue(inspector) as string ?? "#FFFFFF";
        if (TryPickColor(currentHex, out string newHex))
        {
            prop.SetValue(inspector, newHex);
        }
    }

    private void OnCopyTextStyle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextBlockInspectorViewModel vm })
            vm.CopyTextStyle();
    }

    private void OnPasteTextStyle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextBlockInspectorViewModel vm })
            vm.PasteTextStyle();
    }

    private void OnClearFormatting(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextBlockInspectorViewModel vm })
            vm.ClearFormatting();
    }

    private void OnZOrder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode, DataContext: InspectorViewModelBase vm }) return;
        var cmd = vm.ParentVm.ChangeZOrderCommand;
        if (cmd.CanExecute(mode)) cmd.Execute(mode);
    }

    private void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string spec, DataContext: TextBlockInspectorViewModel vm }) return;
        // Tag format: "fill|stroke|text"
        var parts = spec.Split('|');
        if (parts.Length != 3) return;
        vm.ApplyPreset(parts[0], parts[1], parts[2]);
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
}

/// <summary>
/// Two-way converter: IsChecked = (value == parameter). When the radio is checked,
/// writes ConverterParameter back to the source.
/// </summary>
public sealed class StringEqConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? parameter as string : Binding.DoNothing;
}

/// <summary>
/// Multi-value converter for the bottom shape toolbar. Takes (button.Tag, ActiveCanvasShapeTool,
/// PendingCanvasItemPlacement) and returns true when this button represents the currently
/// active tool. An empty Tag stands for "Selection / move" mode and lights up when no tool
/// is active.
/// </summary>
public sealed class ActiveToolButtonConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string tag = (values.Length > 0 ? values[0] as string : null) ?? string.Empty;
        string active = (values.Length > 1 ? values[1] as string : null) ?? string.Empty;
        string pending = (values.Length > 2 ? values[2] as string : null) ?? string.Empty;
        if (string.IsNullOrEmpty(tag))
            return string.IsNullOrEmpty(active) && string.IsNullOrEmpty(pending);
        return string.Equals(tag, active, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, pending, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
