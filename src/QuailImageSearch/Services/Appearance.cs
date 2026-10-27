using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;

namespace QuailImageSearch.Services;

/// <summary>A value for one of the appearance dropdowns in Preferences.</summary>
public sealed record AppearanceOption(string Value, string Label);

/// <summary>
/// The UI look, from preferences: Avalonia's two built-in themes (Fluent, Simple), light/dark or
/// following the system, and Fluent's density. Applied live, without a restart.
/// </summary>
public static class Appearance
{
    public const string Fluent = "fluent", Simple = "simple";
    public const string System = "system", Light = "light", Dark = "dark";
    public const string Normal = "normal", Compact = "compact";

    public static IReadOnlyList<AppearanceOption> Themes { get; } =
    [
        new(Fluent, "Fluent (default)"),
        new(Simple, "Simple"),
    ];

    public static IReadOnlyList<AppearanceOption> Modes { get; } =
    [
        new(System, "Follow the system (default)"),
        new(Light, "Light"),
        new(Dark, "Dark"),
    ];

    public static IReadOnlyList<AppearanceOption> Densities { get; } =
    [
        new(Normal, "Normal (default)"),
        new(Compact, "Compact"),
    ];

    /// <summary>The theme the app started with; switching themes takes effect at the next start.</summary>
    public static string RunningTheme { get; private set; } = Fluent;

    /// <summary>
    /// At startup, before any window opens: sets the theme (Fluent or Simple), light/dark mode and density.
    /// </summary>
    public static void ApplyAtStartup(Application app, string theme, string mode, string density)
    {
        RunningTheme = theme == Simple ? Simple : Fluent;
        var current = app.Styles.FirstOrDefault(s => s is FluentTheme or SimpleTheme);
        var index = current is null ? 0 : app.Styles.IndexOf(current);
        if (RunningTheme == Simple && current is not SimpleTheme)
        {
            if (current is not null) app.Styles.RemoveAt(index);
            app.Styles.Insert(index, new SimpleTheme());
            app.Styles.Add(SimpleExtras);
        }
        ApplyLive(app, mode, density);
    }

    /// <summary>
    /// Light/dark mode and density, applied to the running app immediately. (Swapping Fluent and
    /// Simple while windows are open doesn't re-style them properly, so the theme itself only
    /// changes at startup.)
    /// </summary>
    public static void ApplyLive(Application app, string mode, string density)
    {
        app.RequestedThemeVariant = mode switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default, // follows the system
        };
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
            fluent.DensityStyle = density == Compact ? DensityStyle.Compact : DensityStyle.Normal;
    }

    /// <summary>
    /// Simple has no "accent" button style (Fluent does); give the app's main buttons (Search, Save,
    /// Download, Install) the accent colour there too.
    /// </summary>
    private static readonly Styles SimpleExtras =
    [
        new Style(x => x.OfType<Button>().Class("accent"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("AppAccentBrush")),
                new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("AppOnAccentBrush")),
            },
        },
    ];
}
