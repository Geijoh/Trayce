using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Windows.Forms;

namespace Trayce;

internal enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>One concrete set of color tokens, mirrored from the prototype's CSS variables.</summary>
internal sealed class ThemePalette
{
    public required Color Bg { get; init; }
    public required Color Bg2 { get; init; }
    public required Color Card { get; init; }
    public required Color Card2 { get; init; }
    public required Color Flyout { get; init; }
    public required Color Menu { get; init; }
    public required Color Titlebar { get; init; }
    public required Color TaskbarSolid { get; init; }
    public required Color Border { get; init; }
    public required Color Border2 { get; init; }
    public required Color Control { get; init; }
    public required Color ControlHover { get; init; }
    public required Color Text { get; init; }
    public required Color Text2 { get; init; }
    public required Color Text3 { get; init; }
    public required Color Accent { get; init; }
    public required Color Accent2 { get; init; }
    public required Color Ok { get; init; }
    public required Color Warn { get; init; }
    public required Color Crit { get; init; }
    public required Color CodeBg { get; init; }
    public required Color CodeFg { get; init; }
    public required double ScrimOpacity { get; init; }

    // dark: { '--bg':'#202020', ... } from the prototype renderVals()
    public static readonly ThemePalette Dark = new()
    {
        Bg = Color.FromArgb(0x20, 0x20, 0x20),
        Bg2 = Color.FromArgb(0x27, 0x27, 0x27),
        Card = Color.FromArgb(0x2b, 0x2b, 0x2b),
        Card2 = Color.FromArgb(0x32, 0x32, 0x32),
        Flyout = Color.FromArgb(0x2b, 0x2b, 0x2b),
        Menu = Color.FromArgb(0x2b, 0x2b, 0x2b),
        Titlebar = Color.FromArgb(0x2b, 0x2b, 0x2b),
        TaskbarSolid = Color.FromArgb(0x26, 0x26, 0x28),
        Border = Color.FromArgb(60, 60, 60),     // --stroke  rgba(255,255,255,.08) flattened
        Border2 = Color.FromArgb(76, 76, 76),    // --stroke2 rgba(255,255,255,.15) flattened
        Control = Color.FromArgb(56, 56, 56),    // --control rgba(255,255,255,.055)
        ControlHover = Color.FromArgb(66, 66, 66), // --control-h rgba(255,255,255,.1)
        Text = Color.White,
        Text2 = Color.FromArgb(198, 198, 198),   // rgba(255,255,255,.76)
        Text3 = Color.FromArgb(140, 140, 140),   // rgba(255,255,255,.5)
        Accent = Color.FromArgb(0x60, 0xcd, 0xff),
        Accent2 = Color.FromArgb(0x00, 0x78, 0xd4),
        Ok = Color.FromArgb(0x6c, 0xcb, 0x5f),
        Warn = Color.FromArgb(0xfb, 0xd3, 0x24),
        Crit = Color.FromArgb(0xff, 0x99, 0xa4),
        CodeBg = Color.FromArgb(0x1a, 0x1a, 0x1a),
        CodeFg = Color.FromArgb(0xd6, 0xdb, 0xe2),
        ScrimOpacity = 0.45
    };

    // light: { '--bg':'#f3f3f3', ... }
    public static readonly ThemePalette Light = new()
    {
        Bg = Color.FromArgb(0xf3, 0xf3, 0xf3),
        Bg2 = Color.FromArgb(0xed, 0xee, 0xf1),
        Card = Color.FromArgb(0xff, 0xff, 0xff),
        Card2 = Color.FromArgb(0xf7, 0xf7, 0xf7),
        Flyout = Color.FromArgb(0xf9, 0xf9, 0xf9),
        Menu = Color.FromArgb(0xf3, 0xf3, 0xf3),
        Titlebar = Color.FromArgb(0xee, 0xf0, 0xf4),
        TaskbarSolid = Color.FromArgb(0xea, 0xea, 0xea),
        Border = Color.FromArgb(237, 237, 237),  // --stroke  rgba(0,0,0,.07)
        Border2 = Color.FromArgb(222, 222, 222), // --stroke2 rgba(0,0,0,.13)
        Control = Color.FromArgb(247, 247, 247),
        ControlHover = Color.FromArgb(238, 238, 238),
        Text = Color.FromArgb(0x1a, 0x1a, 0x1a),
        Text2 = Color.FromArgb(90, 90, 90),
        Text3 = Color.FromArgb(138, 138, 138),
        Accent = Color.FromArgb(0x00, 0x67, 0xc0),
        Accent2 = Color.FromArgb(0x00, 0x78, 0xd4),
        Ok = Color.FromArgb(0x0f, 0x7b, 0x0f),
        Warn = Color.FromArgb(0x9d, 0x5d, 0x00),
        Crit = Color.FromArgb(0xc4, 0x2b, 0x1c),
        CodeBg = Color.FromArgb(0xf6, 0xf8, 0xfa),
        CodeFg = Color.FromArgb(0x1f, 0x23, 0x28),
        ScrimOpacity = 0.28
    };
}

internal static class SystemTheme
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) return value == 0;
        }
        catch
        {
            // ponytail: registry missing/locked → assume the Windows 11 default (dark-friendly).
        }
        return true;
    }

    public static ThemeMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System
    };

    public static string ToConfig(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system"
    };
}

/// <summary>Swappable accessor over the current <see cref="ThemePalette"/>. Field names match the original
/// static palette so existing call sites compile unchanged; values now follow the active theme.</summary>
internal static class UiPalette
{
    private static ThemePalette current = ThemePalette.Dark;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the active theme changes so live windows can repaint/rebuild.</summary>
    public static event Action? Changed;

    public static bool Apply(ThemeMode mode)
    {
        var nextDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemTheme.IsDark());
        var changed = IsDark != nextDark;
        Mode = mode;
        IsDark = nextDark;
        current = IsDark ? ThemePalette.Dark : ThemePalette.Light;
        if (changed) Changed?.Invoke();
        return changed;
    }

    public static Color Bg => current.Bg;
    public static Color Bg2 => current.Bg2;
    public static Color Card => current.Card;
    public static Color Card2 => current.Card2;
    public static Color Flyout => current.Flyout;
    public static Color Menu => current.Menu;
    public static Color Titlebar => current.Titlebar;
    public static Color TaskbarSolid => current.TaskbarSolid;
    public static Color Border => current.Border;
    public static Color Border2 => current.Border2;
    public static Color Control => current.Control;
    public static Color ControlHover => current.ControlHover;
    public static Color Text => current.Text;
    public static Color Text2 => current.Text2;
    public static Color Text3 => current.Text3;
    public static Color Accent => current.Accent;
    public static Color Accent2 => current.Accent2;
    public static Color Ok => current.Ok;
    public static Color Warn => current.Warn;
    public static Color Crit => current.Crit;
    public static Color CodeBg => current.CodeBg;
    public static Color CodeFg => current.CodeFg;
    public static double ScrimOpacity => current.ScrimOpacity;

    public static Color Backdrop(Control control, Color fallback)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.BackColor.A > 0 && parent.BackColor != Color.Transparent) return parent.BackColor;
        }

        return fallback;
    }

    public static void FillRound(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = RoundPath(rect, radius);
        g.FillPath(brush, path);
    }

    public static void FillRound(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = RoundPath(rect, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRound(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = RoundPath(rect, radius);
        g.DrawPath(pen, path);
    }

    public static GraphicsPath RoundPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
