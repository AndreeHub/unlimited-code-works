using System;
using WpfColor = System.Windows.Media.Color;

namespace ReviewScope.Canvas;

/// <summary>
/// Central palette for the custom Direct2D surfaces (canvas + outliner) so they theme
/// alongside the WPF chrome. The renderers read these values instead of hardcoded
/// literals; the app's theme manager flips the whole palette via <see cref="Apply"/>,
/// which raises <see cref="Changed"/> so each hosted view can redraw.
/// </summary>
public static class CanvasTheme
{
    public static bool IsDark { get; private set; }

    /// <summary>Accent used for selection/active cues on the canvas (brighter in dark).</summary>
    public static WpfColor Accent { get; private set; }

    // --- Canvas surface ---------------------------------------------------------------
    public static WpfColor Surface { get; private set; }
    public static WpfColor Dot { get; private set; }
    public static WpfColor GridMinor { get; private set; }
    public static WpfColor GridMajor { get; private set; }

    // --- Canvas "empty board" hint card -----------------------------------------------
    public static WpfColor HintCardFill { get; private set; }
    public static WpfColor HintCardBorder { get; private set; }
    public static WpfColor HintTitle { get; private set; }
    public static WpfColor HintSub { get; private set; }
    public static WpfColor HintMuted { get; private set; }

    // --- Outliner body ----------------------------------------------------------------
    public static WpfColor OutlineBg { get; private set; }
    public static WpfColor OutlineText { get; private set; }

    // --- Popups drawn directly on the D2D target (slash menu, etc.) --------------------
    public static WpfColor PopupBg { get; private set; }
    public static WpfColor PopupBorder { get; private set; }
    public static WpfColor PopupHeader { get; private set; }
    public static WpfColor PopupSelectedRow { get; private set; }
    public static WpfColor PopupLabel { get; private set; }
    public static WpfColor PopupHint { get; private set; }

    /// <summary>Raised after the palette is swapped so hosted views can redraw.</summary>
    public static event Action? Changed;

    static CanvasTheme() => Apply(false);

    public static void Apply(bool dark)
    {
        IsDark = dark;
        if (!dark)
        {
            Accent           = Rgb(46, 125, 215);
            Surface          = Rgb(0xFA, 0xFB, 0xFC);
            Dot              = Argb(132, 176, 186, 200);
            GridMinor        = Argb(62, 198, 207, 219);
            GridMajor        = Argb(104, 172, 184, 199);

            HintCardFill     = Rgb(0xFF, 0xFF, 0xFF);
            HintCardBorder   = Argb(220, 226, 232, 240);
            HintTitle        = Rgb(31, 41, 51);
            HintSub          = Rgb(83, 96, 112);
            HintMuted        = Rgb(119, 132, 150);

            OutlineBg        = Rgb(0xFF, 0xFF, 0xFF);
            OutlineText      = Rgb(0x22, 0x22, 0x22);

            PopupBg          = Argb(250, 255, 255, 255);
            PopupBorder      = Argb(235, 207, 215, 226);
            PopupHeader      = Rgb(150, 158, 170);
            PopupSelectedRow = Argb(255, 232, 242, 255);
            PopupLabel       = Rgb(38, 47, 64);
            PopupHint        = Rgb(170, 178, 190);
        }
        else
        {
            Accent           = Rgb(0x4F, 0xA0, 0xEA);
            Surface          = Rgb(0x0E, 0x11, 0x15);
            Dot              = Rgb(0x26, 0x2D, 0x37);
            GridMinor        = Argb(80, 0x2A, 0x31, 0x3B);
            GridMajor        = Argb(130, 0x3A, 0x43, 0x4F);

            HintCardFill     = Rgb(0x1A, 0x1E, 0x25);
            HintCardBorder   = Rgb(0x2A, 0x31, 0x3B);
            HintTitle        = Rgb(0xE7, 0xEB, 0xF1);
            HintSub          = Rgb(0x9B, 0xA5, 0xB3);
            HintMuted        = Rgb(0x6A, 0x74, 0x80);

            OutlineBg        = Rgb(0x18, 0x1B, 0x21);
            OutlineText      = Rgb(0xEA, 0xEC, 0xF0);

            PopupBg          = Rgb(0x22, 0x27, 0x30);
            PopupBorder      = Rgb(0x3A, 0x43, 0x4F);
            PopupHeader      = Rgb(0x6A, 0x74, 0x80);
            PopupSelectedRow = Rgb(0x17, 0x30, 0x49);
            PopupLabel       = Rgb(0xE7, 0xEB, 0xF1);
            PopupHint        = Rgb(0x6A, 0x74, 0x80);
        }
        Changed?.Invoke();
    }

    private static WpfColor Rgb(byte r, byte g, byte b) => WpfColor.FromRgb(r, g, b);
    private static WpfColor Argb(byte a, byte r, byte g, byte b) => WpfColor.FromArgb(a, r, g, b);
}
