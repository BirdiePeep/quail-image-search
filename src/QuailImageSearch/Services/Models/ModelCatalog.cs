using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuailImageSearch.Services.Models;

/// <param name="Sha256">Lowercase hex SHA-256 of the file, checked after downloading (null in lists without hashes).</param>
public sealed record ModelFile(string Path, long Size, string? Sha256 = null)
{
    /// <summary>Where the file lives inside the model's folder (the repo layout, flattened).</summary>
    [JsonIgnore]
    public string LocalName => System.IO.Path.GetFileName(Path);
}

public sealed record ModelFiles(ModelFile Vision, ModelFile Text, ModelFile Tokenizer);

/// <summary>Image preprocessing, mirroring the model's Hugging Face image processor.</summary>
public sealed record ImageSpec(
    int Size,
    string Resize,     // "shortest_edge": scale the short side to Size, then center-crop; "exact": squash to Size x Size
    string Resample,   // "bicubic" | "bilinear" (Pillow's antialiased filters)
    float[] Mean,
    float[] Std);

public sealed record TextSpec(int MaxLength, int? PadTo, int PadId);

/// <summary>
/// A downloadable image-text embedding model: separate ONNX text and vision encoders plus a
/// tokenizer.json. <see cref="Family"/> picks the tokenizer and text conventions ("clip" covers
/// OpenAI CLIP and MobileCLIP, "siglip" covers SigLIP).
/// </summary>
public sealed record ModelInfo(
    string Id,
    string Name,
    string Family,
    string Description,
    string License,
    string LicenseUrl,
    string? LicenseNote,
    string Repo,
    string Revision,
    ModelFiles Files,
    ImageSpec Image,
    TextSpec Text)
{
    public const string ClipFamily = "clip";
    public const string SiglipFamily = "siglip";
    public static readonly string[] SupportedFamilies = [ClipFamily, SiglipFamily];

    [JsonIgnore]
    public IReadOnlyList<ModelFile> AllFiles => [Files.Vision, Files.Text, Files.Tokenizer];

    [JsonIgnore]
    public long TotalSize => AllFiles.Sum(f => f.Size);

    [JsonIgnore]
    public string SizeText => TotalSize >= 1_000_000_000 ? $"{TotalSize / 1e9:0.0} GB" : $"{TotalSize / 1e6:0} MB";

    [JsonIgnore]
    public string FamilyText => Family == SiglipFamily ? "SigLIP" : Name.StartsWith("MobileCLIP") ? "MobileCLIP" : "CLIP";

    [JsonIgnore]
    public string SourceUrl => $"https://huggingface.co/{Repo}";

    public string FileUrl(ModelFile f) => $"https://huggingface.co/{Repo}/resolve/{Revision}/{f.Path}";
}

public sealed record ModelCatalogFile(int SchemaVersion, List<ModelInfo> Models);

/// <summary>
/// The list of downloadable models. It ships with the app (models.json, embedded) and is designed
/// to also come from the internet: <see cref="RefreshAsync"/> downloads a newer list into the
/// app-data folder, which takes precedence on the next start. Entries for model families this
/// version can't run are ignored, so an online list can be ahead of the app.
/// </summary>
public static partial class ModelCatalog
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Downloaded copy of an online catalog, if any.</summary>
    public static string CachePath { get; } = QuailImageSearch.Services.AppPaths.File("model-catalog.json");

    /// <summary>Where the current list came from, for the Models page.</summary>
    public static string Source { get; private set; } = "";

    public static IReadOnlyList<ModelInfo> All { get; private set; } = Load();

    /// <summary>The model suggested when none is installed (accurate, Apache-2.0).</summary>
    public const string RecommendedId = "siglip-b16-224";

    private static IReadOnlyList<ModelInfo> Load()
    {
        if (File.Exists(CachePath))
        {
            try
            {
                var cached = Parse(File.ReadAllText(CachePath));
                Source = $"Downloaded list ({File.GetLastWriteTime(CachePath):yyyy-MM-dd})";
                return cached;
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                // Fall back to the built-in list.
            }
        }
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("QuailImageSearch.Services.Models.models.json")
                           ?? throw new InvalidOperationException("models.json resource missing");
        Source = "Built-in list";
        return Parse(new StreamReader(stream).ReadToEnd());
    }

    public static IReadOnlyList<ModelInfo> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ModelCatalogFile>(json, Json) ?? throw new InvalidDataException("empty catalog");
        if (file.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"catalog schema {file.SchemaVersion} isn't supported (expected {SchemaVersion})");
        return file.Models
            .Where(m => ModelInfo.SupportedFamilies.Contains(m.Family) && IsSafe(m))
            .GroupBy(m => m.Id).Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// The id becomes a folder name, the repo, revision and file paths become download URLs and
    /// file names, so entries that could point outside the app's folders or elsewhere on the
    /// internet are dropped (this matters once the list can come from the internet).
    /// </summary>
    private static bool IsSafe(ModelInfo m) =>
        m.Id is { Length: > 0 and <= 64 } && SafeId().IsMatch(m.Id)
        && m.Repo is not null && SafeRepo().IsMatch(m.Repo)
        && m.Revision is not null && Revision().IsMatch(m.Revision)
        && m.Files is { Vision: not null, Text: not null, Tokenizer: not null }
        && m.AllFiles.All(f => f.Path is { Length: > 0 } && SafePath().IsMatch(f.Path) && !f.Path.Split('/').Contains("..")
                               && f.Size >= 0 && (f.Sha256 is null || Sha256().IsMatch(f.Sha256)));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SafeId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeRepo();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Revision();

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafePath();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    /// <summary>
    /// Fetches a catalog from <paramref name="url"/> and, if it's valid, caches it for the next
    /// start. Not wired to the UI yet; the list is local-only for now.
    /// </summary>
    public static async Task<int> RefreshAsync(HttpClient http, string url, CancellationToken ct)
    {
        var json = await http.GetStringAsync(url, ct);
        var models = Parse(json); // throws if invalid, leaving the current cache untouched
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, json, ct);
        return models.Count;
    }
}
