using System.Collections.Generic;

namespace QuailImageSearch.Services;

/// <summary>User preferences, saved to preferences.json in the app-data folder.</summary>
public sealed class Preferences
{
    /// <summary>
    /// Tool used for new tabs (and for restored tabs whose tool no longer exists); empty means the
    /// first available tool.
    /// </summary>
    public string DefaultToolId { get; set; } = "";
    public decimal DefaultMaxResults { get; set; } = 200;

    /// <summary>
    /// GPU use for the downloaded models: "auto" (default: GPU when the system can run the model,
    /// otherwise CPU), "on" (try the GPU even if the checks say no) or "off".
    /// </summary>
    public string UseGpu { get; set; } = Models.GpuModes.Auto;

    /// <summary>Images per GPU call when indexing (models above 256 px use half); see <see cref="Models.GpuBatch"/>.</summary>
    public int GpuBatchSize { get; set; } = Models.GpuBatch.Default;

    /// <summary>UI theme ("fluent" or "simple"), light/dark mode ("system", "light", "dark") and Fluent's density; see <see cref="Appearance"/>.</summary>
    public string Theme { get; set; } = Appearance.Fluent;
    public string ThemeMode { get; set; } = Appearance.System;
    public string ThemeDensity { get; set; } = Appearance.Normal;

    /// <summary>Min score for new tabs, on the calibrated 0–100 scale (the same for every calibrated tool).</summary>
    public decimal DefaultMinScore { get; set; } = StandardMinScore;

    /// <summary>
    /// 2 = scores are on the calibrated 0–100 scale. Files without it (0) stored raw-score defaults
    /// like 0.22; <see cref="Migrate"/> resets those.
    /// </summary>
    public int ScoreScaleVersion { get; set; } // 0 when missing from the file, so old files are detected

    /// <summary>1 = command-line tools are configured in <see cref="CustomTools"/>.</summary>
    public int ToolsVersion { get; set; }

    public const int CurrentScoreScaleVersion = 2;
    public const int CurrentToolsVersion = 1;

    // On the calibration scale, real matches scored 67–96 and weak real matches 44–56 in testing,
    // while the best unrelated image in a small folder scored ~12–59 (median ~43).
    public const decimal StandardMinScore = 40;

    /// <summary>Size and position of the image preview window, restored when it opens.</summary>
    public WindowPlacement? PreviewWindow { get; set; }

    /// <summary>User-defined command-line tools; no code needed to add one.</summary>
    public List<CommandLineToolDefinition> CustomTools { get; set; } = new();

    /// <summary>
    /// Preferences for a new install: no command-line tools (they can be added in Preferences), and
    /// nothing to migrate.
    /// </summary>
    public static Preferences CreateDefault() => new()
    {
        ScoreScaleVersion = CurrentScoreScaleVersion,
        ToolsVersion = CurrentToolsVersion,
    };

    /// <summary>Upgrades preferences saved by older versions. Returns true if anything changed.</summary>
    public bool Migrate()
    {
        var changed = false;
        if (ScoreScaleVersion < CurrentScoreScaleVersion)
        {
            DefaultMinScore = StandardMinScore;
            ScoreScaleVersion = CurrentScoreScaleVersion;
            changed = true;
        }
        if (ToolsVersion < CurrentToolsVersion)
        {
            ToolsVersion = CurrentToolsVersion;
            changed = true;
        }
        return changed;
    }
}

/// <summary>
/// A search tool defined entirely by configuration: run <see cref="Executable"/> with
/// <see cref="Arguments"/> (placeholders {query}, {folder}, {top}) in the searched folder, and
/// parse each stdout line with <see cref="OutputPattern"/>, a regex with a named group "path"
/// and an optional group "score".
/// </summary>
public sealed class CommandLineToolDefinition
{
    public const string DefaultOutputPattern = @"^\s*(?<score>-?\d+(?:\.\d+)?)\s+""?(?<path>.+?)""?\s*$";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A path, or a command name looked up on PATH.</summary>
    public string Executable { get; set; } = "";

    /// <summary>
    /// Where else to look for the executable when it isn't found (e.g. an installer's default
    /// folder). Environment variables like %LOCALAPPDATA% and a leading ~ are expanded.
    /// </summary>
    public List<string> FallbackPaths { get; set; } = new();

    public string Arguments { get; set; } = "{query}";
    public string OutputPattern { get; set; } = DefaultOutputPattern;

    /// <summary>Score for results whose line has no "score" group (e.g. tools that only print paths).</summary>
    public double DefaultScore { get; set; } = 1.0;

    /// <summary>Calibrate this tool's scores automatically (like the built-in models) instead of on demand.</summary>
    public bool AutoCalibrate { get; set; }

    public CommandLineToolDefinition Clone()
    {
        var copy = (CommandLineToolDefinition)MemberwiseClone();
        copy.FallbackPaths = new List<string>(FallbackPaths);
        return copy;
    }
}
