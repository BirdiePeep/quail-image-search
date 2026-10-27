using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuailImageSearch.Services;

/// <summary>
/// Generic adapter for any command-line search utility, driven by a
/// <see cref="CommandLineToolDefinition"/> from preferences. Runs the tool once per search.
/// </summary>
public sealed partial class CommandLineTool : ObservableObject, ISearchTool
{
    private readonly DebugLog _log;
    private CommandLineToolDefinition _def;
    private Regex _pattern;

    public CommandLineTool(DebugLog log, CommandLineToolDefinition definition)
    {
        _log = log;
        _def = definition.Clone();
        _pattern = CompilePattern(_def.OutputPattern);
    }

    public string Id => _def.Id;
    public string Name => _def.Name;
    public bool AutoCalibrate => _def.AutoCalibrate;

    /// <summary>Changes whenever the definition changes, so an edited tool needs recalibrating.</summary>
    public string ScoreVersion => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes($"{_def.Executable}\n{string.Join('|', _def.FallbackPaths)}\n{_def.Arguments}\n{_def.OutputPattern}\n{_def.DefaultScore}")))[..16];

    /// <summary>
    /// The executable to run, as a full path: <see cref="CommandLineToolDefinition.Executable"/> if it
    /// exists (as a path or on PATH), otherwise the first existing fallback path, otherwise the name
    /// as given (so the error message names it). Returning the full path matters: given a bare name,
    /// Windows would look in the app's and the current folder before PATH.
    /// </summary>
    public static string ResolveExecutable(CommandLineToolDefinition def)
    {
        var exe = Expand(def.Executable);
        if (Path.IsPathRooted(exe))
        {
            if (File.Exists(exe)) return exe;
        }
        else if (FindOnPath(exe) is { } found)
        {
            return found;
        }
        return def.FallbackPaths.Select(Expand).FirstOrDefault(File.Exists) ?? exe;

        static string Expand(string path)
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            if (path.StartsWith('~'))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            return path;
        }
    }

    /// <summary>Full path of <paramref name="name"/> in a PATH folder (on Windows, the .exe/.com), or null.</summary>
    private static string? FindOnPath(string name)
    {
        if (name.Length == 0) return null;
        if (name.IndexOfAny(['/', '\\']) >= 0) return File.Exists(name) ? Path.GetFullPath(name) : null;
        // Batch files are left out on purpose: they can't be run safely (see IsBatchFile).
        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? [".exe", ".com"] : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => extensions.Select(ext => Path.Combine(dir.Trim('"'), name + ext)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// True for Windows batch files. Windows runs them through cmd.exe, which doesn't follow the
    /// argument quoting every other program uses, so a folder name or query containing &amp;, | or "
    /// could run commands. They're refused as tools.
    /// </summary>
    public static bool IsBatchFile(string path) =>
        OperatingSystem.IsWindows() && Path.GetExtension(path.Trim()).ToLowerInvariant() is ".bat" or ".cmd";

    /// <summary>Updates the definition in place so tabs using this tool keep their selection.</summary>
    public void Update(CommandLineToolDefinition definition)
    {
        _def = definition.Clone();
        _pattern = CompilePattern(_def.OutputPattern);
        OnPropertyChanged(nameof(Name));
    }

    public static Regex CompilePattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var def = _def;
        var pattern = _pattern;
        _log.Write($"search [{def.Name}]: query=\"{request.Query}\" folder=\"{request.Folder}\" top={request.MaxResults}");

        var args = CommandTemplate.Expand(def.Arguments, new Dictionary<string, string>
        {
            ["query"] = request.Query,
            ["folder"] = request.Folder,
            ["top"] = request.MaxResults.ToString(CultureInfo.InvariantCulture),
        });
        var executable = ResolveExecutable(def);
        if (IsBatchFile(executable))
            throw new InvalidOperationException($"{def.Name} is a batch file ({Path.GetFileName(executable)}), which can't be run safely. " +
                                                "Point the tool at the program's .exe in Preferences > Command-line tools.");
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, // StreamReader also drops a leading BOM
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Many CLI tools are Python programs; make them emit UTF-8 so non-ASCII paths survive.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var commandLine = CommandTemplate.Format(executable, args);
        var sw = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e)
        {
            _log.Write($"command: {commandLine}\n  could not start: {e.Message}");
            throw new InvalidOperationException($"Could not run '{executable}': {e.Message}. Check the tool's executable in Preferences > Command-line tools.", e);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                _log.Write($"command: {commandLine}\n  cancelled");
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var log = new StringBuilder($"command: {commandLine}\n  cwd: {request.Folder}\n  exit code: {process.ExitCode}\n  took: {sw.ElapsedMilliseconds} ms");
            if (stdout.Length > 0) log.Append("\n  raw output:\n").Append(stdout.TrimEnd());
            if (stderr.Length > 0) log.Append("\n  stderr:\n").Append(Tail(stderr.TrimEnd(), 4000));
            _log.Write(log.ToString());

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{def.Name} exited with code {process.ExitCode}: {Tail(stderr.Trim(), 300)}");

            var (results, skipped) = ParseOutput(stdout, request.Folder, pattern, def.DefaultScore);
            _log.Write($"{results.Count} result(s) parsed" + (skipped > 0 ? $", {skipped} line(s) skipped (didn't match the output pattern, or not an existing image file)" : ""));
            return results;
        }
    }

    /// <summary>Turns stdout into results: one match of <paramref name="pattern"/> per line.</summary>
    public static (List<SearchResult> Results, int Skipped) ParseOutput(string stdout, string folder, Regex pattern, double defaultScore)
    {
        var results = new List<SearchResult>();
        var skipped = 0;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            var m = pattern.Match(line);
            if (!m.Success || !m.Groups["path"].Success) { skipped++; continue; }

            var path = m.Groups["path"].Value.Trim().Trim('"');
            var score = m.Groups["score"].Success
                && double.TryParse(m.Groups["score"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                ? s : defaultScore;
            try
            {
                // Only existing image files: results can be opened with the default app, deleted and
                // rotated, so a stray line naming a program or a document must not become a result.
                var full = Path.GetFullPath(Path.Combine(folder, path));
                if (ResultExtensions.Contains(Path.GetExtension(full)) && File.Exists(full))
                    results.Add(new SearchResult(full, score));
                else
                    skipped++;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                skipped++;
            }
        }
        return (results, skipped);
    }

    /// <summary>Image formats a command-line tool may return (wider than what the built-in models index).</summary>
    private static readonly HashSet<string> ResultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".jxl", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    public void Dispose() { }
}

/// <summary>Splits an argument template into arguments and fills in {placeholders}.</summary>
public static class CommandTemplate
{
    /// <summary>
    /// Splits on whitespace (double quotes group words), then replaces placeholders inside each
    /// argument, so a query containing spaces or quotes always stays a single argument.
    /// </summary>
    public static List<string> Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in template)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());

        return args.Select(a => values.Aggregate(a, (s, kv) => s.Replace("{" + kv.Key + "}", kv.Value))).ToList();
    }

    /// <summary>Human-readable command line for the debug log.</summary>
    public static string Format(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"')
            ? "\"" + a.Replace("\"", "\\\"") + "\""
            : a));
}
