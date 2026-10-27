using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QuailImageSearch.Services;

/// <summary>Everything restored on the next launch: tabs (with their results) and window placement.</summary>
public sealed class SessionState
{
    public List<TabState> Tabs { get; set; } = new();
    public int SelectedTab { get; set; }
    public bool IsLogVisible { get; set; }
    public WindowPlacement? Window { get; set; }
}

public sealed class TabState
{
    /// <summary>Search tool id; null in old sessions (the default tool is used).</summary>
    public string? ToolId { get; set; }

    /// <summary>Scale of <see cref="MinScore"/>: 2 = calibrated 0–100; missing (0) = raw scores (older versions).</summary>
    public int ScoreScaleVersion { get; set; }

    /// <summary>Thumbnail size in percent (50–200); null in older sessions (100%).</summary>
    public int? ThumbnailScale { get; set; }

    public string Query { get; set; } = "";

    /// <summary>Image the tab searches with instead of <see cref="Query"/> text; null for text searches.</summary>
    public string? QueryImage { get; set; }

    /// <summary>Score scale of <see cref="Results"/> when they came from an image search; null for text searches.</summary>
    public ScoreScale? ImageScoreScale { get; set; }

    public string LastQuery { get; set; } = "";
    public string Folder { get; set; } = "";
    public decimal? MaxResults { get; set; }
    public decimal? MinScore { get; set; }
    public List<SearchResult> Results { get; set; } = new();
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// The app's data folder: %APPDATA%\QuailImageSearch on Windows (~/.config/QuailImageSearch elsewhere), or the
/// folder in the QUAILIMAGESEARCH_DATA_DIR environment variable (IMAGESEARCH_DATA_DIR, its name before
/// the app was renamed, also works), e.g. to try a fresh install without touching your real models,
/// indexes and settings.
/// </summary>
public static class AppPaths
{
    private static readonly string? Override =
        Environment.GetEnvironmentVariable("QUAILIMAGESEARCH_DATA_DIR") is { Length: > 0 } dir ? dir
        : Environment.GetEnvironmentVariable("IMAGESEARCH_DATA_DIR") is { Length: > 0 } legacy ? legacy
        : null;

    private static string AppDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string DataDir { get; } =
        Override is not null ? Path.GetFullPath(Override) : Path.Combine(AppDataRoot, "QuailImageSearch");

    public static string File(params string[] parts) => Path.Combine([DataDir, .. parts]);

    /// <summary>
    /// The app was called "Image Search" and kept its data in an "ImageSearch" folder. On the first run
    /// under the new name, copy that folder (models, indexes, settings, session) to the new one, so
    /// nothing has to be downloaded or indexed again. The old folder is left untouched. Only done for
    /// the default location (not when the data folder is overridden). Returns a note for the log, or null.
    /// </summary>
    public static string? CopyLegacyData() =>
        Override is null ? CopyLegacyData(Path.Combine(AppDataRoot, "ImageSearch"), DataDir) : null;

    /// <summary>Copies <paramref name="oldDir"/> to <paramref name="newDir"/> if only the old one exists.</summary>
    public static string? CopyLegacyData(string oldDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return null;
        // Copy into a temporary folder and rename it at the end, so an interrupted copy isn't mistaken
        // for a complete one (the next start simply tries again).
        var temp = newDir + ".copying";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            long bytes = 0;
            var files = 0;
            foreach (var source in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(temp, Path.GetRelativePath(oldDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(source, target);
                bytes += new FileInfo(source).Length;
                files++;
            }
            Directory.CreateDirectory(temp); // an empty old folder still becomes the new one
            Directory.Move(temp, newDir);
            return $"copied the data from {oldDir} to {newDir} ({files} files, {bytes / 1e6:0} MB); the old folder was left as it was";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
            return $"couldn't copy the data from {oldDir} to {newDir}: {e.Message}; starting with an empty data folder";
        }
    }
}

/// <summary>Loads/saves a JSON file in the app-data folder (<see cref="AppPaths.DataDir"/>).</summary>
public sealed class JsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonFileStore(string fileName) => FilePath = AppPaths.File(fileName);

    public string FilePath { get; }

    /// <summary>Set when <see cref="Load"/> found the file but couldn't read it.</summary>
    public string? LoadError { get; private set; }

    public T? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file shouldn't stop the app from starting; keep a copy for inspection.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            LoadError = $"{Path.GetFileName(FilePath)} couldn't be read ({e.Message}), so defaults are used. A copy was kept as {Path.GetFileName(FilePath)}.bad.";
            return null;
        }
    }

    public void Save(T state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, FilePath, overwrite: true); // atomic replace, so a crash mid-write can't corrupt it
    }
}

public static class AppFiles
{
    public static JsonFileStore<SessionState> Session { get; } = new("session.json");
    public static JsonFileStore<Preferences> Preferences { get; } = new("preferences.json");
}
