using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;

namespace QuailImageSearch.Services.Probes;

/// <summary>
/// Trained tag probes, one per model, in the app-data folder (%APPDATA%\QuailImageSearch\probes\{model id}.probe).
/// Probes are loaded on first use and kept in memory (about 20 MB each).
/// </summary>
public sealed class ProbeStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinearProbe> _loaded = new();
    private readonly Dictionary<string, ProbeHeader?> _headers = new();

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuailImageSearch", "probes");

    public static string PathFor(string modelId) => Path.Combine(Root, modelId + ".probe");

    /// <summary>Raised on the UI thread after a model's probe was saved or deleted.</summary>
    public event Action<string>? Changed;

    /// <summary>The probe's header (tags and scores) without loading its weights, or null if there's no probe.</summary>
    public ProbeHeader? HeaderFor(string modelId)
    {
        lock (_lock)
        {
            if (_headers.TryGetValue(modelId, out var cached)) return cached;
            ProbeHeader? header = null;
            try
            {
                if (File.Exists(PathFor(modelId))) header = LinearProbe.ReadHeader(PathFor(modelId));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                header = null; // unreadable: treated as missing
            }
            _headers[modelId] = header;
            return header;
        }
    }

    /// <summary>The model's probe with its weights; loads it on first use.</summary>
    public LinearProbe Get(string modelId)
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(modelId, out var probe)) return probe;
            probe = LinearProbe.Load(PathFor(modelId));
            _loaded[modelId] = probe;
            return probe;
        }
    }

    public void Save(LinearProbe probe)
    {
        var id = probe.Header.ModelId;
        lock (_lock)
        {
            probe.Save(PathFor(id));
            _loaded[id] = probe;
            _headers[id] = probe.Header;
        }
        Dispatcher.UIThread.Post(() => Changed?.Invoke(id));
    }

    /// <summary>Deletes the model's probe. Returns an error message, or null on success.</summary>
    public string? Delete(string modelId)
    {
        lock (_lock)
        {
            try { File.Delete(PathFor(modelId)); }
            catch (IOException e) { return e.Message; }
            catch (UnauthorizedAccessException e) { return e.Message; }
            _loaded.Remove(modelId);
            _headers[modelId] = null;
        }
        Changed?.Invoke(modelId);
        return null;
    }
}
