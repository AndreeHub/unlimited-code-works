using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ReviewScope.Canvas;

namespace ReviewScope.App.Theming;

/// <summary>
/// Runtime light/dark theming for the WPF chrome. Rather than swap whole resource
/// dictionaries (which would require every consumer to use <c>DynamicResource</c>), this
/// mutates the <see cref="Color"/> of the shared, non-frozen <see cref="SolidColorBrush"/>
/// resources in place — every <c>StaticResource</c> consumer holds the same brush instance,
/// so the change propagates live. The custom Direct2D surfaces are flipped through
/// <see cref="CanvasTheme"/>.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>Raised after a theme is applied (for toggles that mirror the state).</summary>
    public static event Action<bool>? ThemeChanged;

    // Semantic + canvas tokens, keyed by the brush resource name. (light, dark).
    private static readonly Dictionary<string, (Color Light, Color Dark)> Tokens = new()
    {
        ["ShellBg"]          = (H("#F4F6F9"), H("#14171C")),
        ["PanelBg"]          = (H("#FFFFFF"), H("#1A1E25")),
        ["CanvasBg"]         = (H("#FAFBFC"), H("#0E1115")),
        ["PanelBorder"]      = (H("#E3E7ED"), H("#2A313B")),
        ["SoftBorder"]       = (H("#EDF0F4"), H("#222931")),
        ["ForegroundBrush"]  = (H("#1B2530"), H("#E7EBF1")),
        ["SubtleForeground"] = (H("#56616F"), H("#9BA5B3")),
        ["MutedForeground"]  = (H("#98A1AE"), H("#6A7480")),
        ["AccentBrush"]      = (H("#2E7DD7"), H("#4FA0EA")),
        ["AccentHover"]      = (H("#2467B5"), H("#6BB1EF")),
        ["AccentSoft"]       = (H("#E7F0FB"), H("#173049")),
        ["AccentBorder"]     = (H("#CFE3FA"), H("#2F5C86")),
        ["SuccessBrush"]     = (H("#2F9E66"), H("#46B883")),
        ["SuccessSoft"]      = (H("#E4F4EC"), H("#16291E")),
        ["WarningBrush"]     = (H("#C2891C"), H("#D7A23F")),
        ["WarningSoft"]      = (H("#FAF0DC"), H("#2E2614")),
        ["SelectionBg"]      = (H("#D3E5F9"), H("#1D3A57")),
        ["HoverBg"]          = (H("#F0F3F7"), H("#222932")),
        // Canvas-render tokens (mirrored as brushes for any XAML that needs them).
        ["CanvasDot"]        = (H("#D2D8E0"), H("#262D37")),
        ["ShapeFill"]        = (H("#FFFFFF"), H("#1E2530")),
        ["ShapeStroke"]      = (H("#C2CAD4"), H("#3A434F")),
        ["ConnectorStroke"]  = (H("#9AA4B2"), H("#5A6573")),
    };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ReviewScope", "theme.txt");

    /// <summary>Reads the persisted choice (defaults to light) and applies it.</summary>
    public static void Initialize()
    {
        bool dark = false;
        try
        {
            if (File.Exists(SettingsPath))
                dark = string.Equals(File.ReadAllText(SettingsPath).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* fall back to light */ }
        Apply(dark, persist: false);
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark, bool persist = true)
    {
        IsDark = dark;

        var res = Application.Current?.Resources;
        if (res is not null)
        {
            foreach (var (key, val) in Tokens)
            {
                var color = dark ? val.Dark : val.Light;
                if (res[key] is SolidColorBrush brush && !brush.IsFrozen)
                    brush.Color = color;
                else
                    res[key] = new SolidColorBrush(color);
            }
        }

        CanvasTheme.Apply(dark);

        if (persist)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, dark ? "dark" : "light");
            }
            catch { /* non-fatal */ }
        }

        ThemeChanged?.Invoke(dark);
    }

    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
