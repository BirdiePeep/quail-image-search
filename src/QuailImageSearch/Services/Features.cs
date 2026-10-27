namespace QuailImageSearch.Services;

/// <summary>Switches for features that are built but not shown right now.</summary>
public static class Features
{
    /// <summary>
    /// Command-line search tools (any program that prints one result per line; see
    /// <see cref="CommandLineTool"/>). Off for now (2026-10-09): the Preferences tab is hidden, and saved
    /// tools aren't offered as search tools, but they stay in preferences.json. Set to true to bring
    /// everything back; no other change is needed.
    /// </summary>
    public static bool CommandLineTools { get; } = false; // a property, so code behind the switch still compiles without warnings
}
