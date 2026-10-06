using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ArbetsWatch.Desktop.Presentation;

/// <summary>A restrained accent choice. Status colors (success, failure, warning, running) never change with it.</summary>
public sealed record AccentPreset(string Name, string? Hex)
{
    /// <summary>The swatch shown next to the name in Settings (the dark-theme accent).</summary>
    public IBrush Swatch => new SolidColorBrush(AccentPalette.Colors(Hex).Dark);

    public override string ToString() => Name;
}

/// <summary>
/// Applies the accent to the HUD brushes (brand, frame, glow, hover, selection, chips) and to Fluent controls
/// (toggle chips, check boxes, focus), with a darker variant on light surfaces so text keeps its contrast.
/// The same presets and rules as Repo Watch.
/// </summary>
public static class AccentPalette
{
    /// <summary>The default "station cyan" plus alternatives whose hues differ from every status color.</summary>
    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new("Station cyan", null),
        new("Nebula violet", "#9D7CFF"),
        new("Ion blue", "#5B8CFF"),
        new("Plasma magenta", "#E15BFF"),
    ];

    private static readonly Color DefaultDark = Color.Parse("#3FE0FF");
    private static readonly Color DefaultLight = Color.Parse("#006F8E");
    private static readonly Color DefaultFluentDark = Color.Parse("#1FB8D6");

    public static AccentPreset Find(string? hex) =>
        Presets.FirstOrDefault(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    /// <summary>The (dark-theme, light-theme) accent for a stored #RRGGBB value, or the defaults.</summary>
    public static (Color Dark, Color Light) Colors(string? hex)
    {
        // Only presets are honored: a hand-edited value would have no contrast guarantee and couldn't be shown in Settings.
        hex = Find(hex).Hex;
        if (hex is null || !Color.TryParse(hex, out var accent))
        {
            return (DefaultDark, DefaultLight);
        }

        return (accent, Darken(accent, 0.42));
    }

    public static void Apply(Application application, string? hex)
    {
        ArgumentNullException.ThrowIfNull(application);
        var (dark, light) = Colors(hex);

        // Alphas match App.axaml, so the default accent reproduces the XAML exactly (it stays the source of truth).
        Apply(application.Resources, ThemeVariant.Dark, dark, frame: 0x80, hover: 0x1A, panel: 0x0D, glow: 0x55);
        Apply(application.Resources, ThemeVariant.Light, light, frame: 0x66, hover: 0x14, panel: 0x0A, glow: 0x33);

        if (application.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            if (fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var darkPalette))
            {
                darkPalette.Accent = Find(hex).Hex is null ? DefaultFluentDark : dark;
            }

            if (fluent.Palettes.TryGetValue(ThemeVariant.Light, out var lightPalette))
            {
                lightPalette.Accent = light;
            }
        }
    }

    /// <summary>Mixes a color toward black by <paramref name="amount"/> (0–1).</summary>
    public static Color Darken(Color color, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R * (1 - amount)),
        (byte)Math.Round(color.G * (1 - amount)),
        (byte)Math.Round(color.B * (1 - amount)));

    private static void Apply(IResourceDictionary resources, ThemeVariant variant, Color accent, byte frame, byte hover, byte panel, byte glow)
    {
        if (!resources.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not IResourceDictionary theme)
        {
            return;
        }

        theme["HudAccentBrush"] = new SolidColorBrush(accent);
        theme["HudFrameBrush"] = new SolidColorBrush(WithAlpha(accent, frame));
        theme["RowHoverBrush"] = new SolidColorBrush(WithAlpha(accent, hover));
        theme["PanelBrush"] = new SolidColorBrush(WithAlpha(accent, panel));
        theme["HudGlowShadow"] = BoxShadows.Parse(string.Create(CultureInfo.InvariantCulture, $"0 0 6 0 #{glow:X2}{accent.R:X2}{accent.G:X2}{accent.B:X2}"));
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
