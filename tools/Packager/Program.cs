// Packages one published platform build of Quail Image Search (see scripts/build.ps1):
//   1. removes files users never need (native debug symbols, import libraries);
//   2. checks the build (model engine present, license and calibration files present, no Debug-only packages);
//   3. scans the shipped libraries for telemetry and web addresses (PRIVACY.md rules 2 and 6);
//   4. packages it: a zip (Windows), a zip with "Quail Image Search.app" (macOS) or a tar.gz (Linux),
//      with Unix file permissions set correctly even when built on Windows.
// Usage: Packager package --rid <rid> --publish <dir> --out <dir> --version <v> --bundle-id <id> --root <repo>
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var opts = new Dictionary<string, string>();
for (var i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
if (args.FirstOrDefault() != "package") { Console.Error.WriteLine("usage: Packager package --rid <rid> --publish <dir> --out <dir> --version <v> --bundle-id <id> --root <repo>"); return 2; }

var rid = opts["rid"];
var publish = Path.GetFullPath(opts["publish"]);
var outDir = Path.GetFullPath(opts["out"]);
var version = opts["version"];
var bundleId = opts["bundle-id"];
var root = Path.GetFullPath(opts["root"]);
const string AppName = "Quail Image Search";
const string ExeName = "QuailImageSearch";
var os = rid.StartsWith("win") ? "win" : rid.StartsWith("osx") ? "osx" : "linux";
var platformLabel = os == "osx" ? "macos" + rid["osx".Length..] : rid;
var packageBase = $"{ExeName}-{version}-{platformLabel}";
var errors = new List<string>();
var warnings = new List<string>();

// ---- 1. Cleanup: native debug symbols (libSkiaSharp.pdb etc., ~100 MB) and import libraries. ----
long removed = 0;
foreach (var f in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories)
             .Where(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)).ToList())
{
    removed += new FileInfo(f).Length;
    File.Delete(f);
}
Console.WriteLine($"  removed {removed / 1e6:0} MB of debug symbols and import libraries");

// ---- 2. Checks ----
bool Has(string relative) => File.Exists(Path.Combine(publish, relative));
var engine = os switch { "win" => "onnxruntime.dll", "osx" => "libonnxruntime.dylib", _ => "libonnxruntime.so" };
if (!Has(engine)) errors.Add($"the model engine ({engine}) is missing: models couldn't load on this platform");
var exe = os == "win" ? ExeName + ".exe" : ExeName;
if (!Has(exe)) errors.Add($"the program ({exe}) is missing");
if (os == "win" && !Has("DirectML.dll")) warnings.Add("DirectML.dll isn't bundled; GPU acceleration would rely on the copy in Windows");
foreach (var required in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md", Path.Combine("Calibration", "manifest.json") })
    if (!Has(required)) errors.Add($"{required} is missing");
if (!Directory.Exists(Path.Combine(publish, "licenses")) || !Directory.EnumerateFiles(Path.Combine(publish, "licenses")).Any())
    errors.Add("the licenses folder is missing or empty");
if (!Directory.Exists(Path.Combine(publish, "Calibration", "images")) || Directory.GetFiles(Path.Combine(publish, "Calibration", "images")).Length < 10)
    errors.Add("the calibration images are missing");
foreach (var f in Directory.EnumerateFiles(publish).Select(Path.GetFileName))
    if (f!.Contains("DiagnosticsSupport", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Avalonia.Diagnostics", StringComparison.OrdinalIgnoreCase)
        || f.StartsWith("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Microsoft.IO.RecyclableMemoryStream", StringComparison.OrdinalIgnoreCase))
        errors.Add($"a Debug-only file is in the release: {f}");

// ---- 3. Privacy scan of the libraries that come from packages (not the .NET runtime itself) ----
// Known, accepted findings (see PRIVACY.md): ONNX Runtime's telemetry is turned off in code; DirectML's
// diagnostics are disclosed; our own assembly calls ONNX Runtime's DisableTelemetryEvents.
string[] telemetryAllowed = ["onnxruntime.dll", "libonnxruntime.dylib", "libonnxruntime.so", "Microsoft.ML.OnnxRuntime.dll", "DirectML.dll", ExeName + ".dll"];
// Web addresses that are documentation, licenses, XML namespaces, certificates or our own model downloads.
string[] benignHosts =
[
    "github.com", "microsoft.com", "aka.ms", "apache.org", "unicode.org", "sil.org", "rsms.me", "w3.org", "freedesktop.org",
    "huggingface.co", "sqlite.org", "adobe.com", "color.org", "iec.ch", "aiim.org", "arxiv.org", "openxmlformats.org", "purl.org",
    "xmlsoap.org", "avaloniaui.net", "dot.net", "nuget.org", "digicert.com", "verisign.com", "symantec.com", "globalsign.com",
    "globalsign.net", "sectigo.com", "usertrust.com", "comodoca.com", "thawte.com", "entrust.net", "opensource.org", "gnu.org",
    "mozilla.org", "libpng.org", "zlib.net", "freetype.org", "harfbuzz.github.io", "skia.org", "google.com", "chromium.org",
    "shorturl.at", "ietf.org", "example.com", "example.org", "onnx.ai", "onnxruntime.ai", "python.org", "khronos.org",
    // Operator documentation and paper references inside ONNX Runtime; XML data-contract namespace in .NET resources.
    "nvidia.com", "scipy.org", "wikipedia.org", "ieee.org", "numpy.org", "nips.cc", "pytorch.org", "tensorflow.org",
    "tinyurl.com", "opencompute.org", "datacontract.org",
    // macOS builds: Apple code-signing certificate URLs; WebGPU (Dawn) documentation inside ONNX Runtime.
    "apple.com", "crbug.com", "googlesource.com", "gpuweb.github.io", "hackmd.io",
];
var scanFiles = new List<string>();
var deps = Path.Combine(publish, ExeName + ".deps.json");
if (File.Exists(deps))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(deps));
    foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
    foreach (var lib in target.Value.EnumerateObject())
    {
        if (lib.Name.StartsWith("runtimepack.", StringComparison.Ordinal)) continue; // the .NET runtime itself
        foreach (var kind in new[] { "runtime", "native" })
            if (lib.Value.TryGetProperty(kind, out var assets))
                foreach (var a in assets.EnumerateObject())
                    scanFiles.Add(Path.GetFileName(a.Name));
    }
}
else errors.Add($"{ExeName}.deps.json is missing, so the privacy scan couldn't tell package files from .NET's own");
var keyword = new Regex("telemetry|analytics|applicationinsights|tracelogging", RegexOptions.IgnoreCase);
var url = new Regex(@"https?://([A-Za-z0-9.-]+\.[A-Za-z]{2,})", RegexOptions.IgnoreCase);
var unknownHosts = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var name in scanFiles.Distinct(StringComparer.OrdinalIgnoreCase))
{
    var path = Path.Combine(publish, name);
    if (!File.Exists(path)) continue;
    var bytes = File.ReadAllBytes(path);
    // Text appears as ASCII or as UTF-16 (.NET strings); check both.
    var texts = new[] { Encoding.Latin1.GetString(bytes), Encoding.Latin1.GetString(bytes.Where(b => b != 0).ToArray()) };
    if (texts.Any(t => keyword.IsMatch(t)) && !telemetryAllowed.Contains(name, StringComparer.OrdinalIgnoreCase))
        errors.Add($"privacy: {name} mentions telemetry or analytics; check it against PRIVACY.md before shipping");
    foreach (Match m in texts.SelectMany(t => url.Matches(t)))
    {
        var host = m.Groups[1].Value.ToLowerInvariant().TrimEnd('.');
        // Strings packed together can run on after the address ("www.color.orggl"), so match by containment.
        if (benignHosts.Any(b => host == b || host.EndsWith("." + b) || host.Contains(b + ".") || host.StartsWith(b) || host.Contains("." + b))) continue;
        if (!unknownHosts.TryGetValue(host, out var files)) unknownHosts[host] = files = new(StringComparer.OrdinalIgnoreCase);
        files.Add(name);
    }
}
foreach (var (host, files) in unknownHosts)
    warnings.Add($"privacy: web address host {host} appears in {string.Join(", ", files)} (review; add to the benign list if it's documentation)");
Console.WriteLine($"  privacy scan: {scanFiles.Distinct().Count()} package files checked");

foreach (var w in warnings) Console.WriteLine("  warning: " + w);
if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine("  ERROR: " + e);
    return 1;
}

// ---- 4. Package ----
Directory.CreateDirectory(outDir);
static int ModeFor(string relative, string os)
{
    var name = Path.GetFileName(relative);
    var executable = os != "win" && (name == ExeName || name == "createdump" || name.EndsWith(".so") || name.EndsWith(".dylib") || name.EndsWith(".sh"));
    return executable ? 0b111_101_101 : 0b110_100_100; // 0755 or 0644
}
string packagePath;
if (os == "linux")
{
    // Add a desktop menu entry, its icon and a small installer for it.
    File.Copy(Path.Combine(root, "src", "QuailImageSearch", "Assets", "logo.png"), Path.Combine(publish, "quail-image-search.png"), true);
    File.WriteAllText(Path.Combine(publish, "quail-image-search.desktop"),
        $"[Desktop Entry]\nType=Application\nName={AppName}\nComment=Search your images with plain text\nExec=@DIR@/{ExeName}\nIcon=@DIR@/quail-image-search.png\nTerminal=false\nCategories=Graphics;Viewer;\n");
    File.WriteAllText(Path.Combine(publish, "install-desktop-entry.sh"),
        "#!/bin/sh\n# Adds Quail Image Search to your applications menu (run from the folder you unpacked it to).\nset -e\nDIR=$(cd \"$(dirname \"$0\")\" && pwd)\nmkdir -p \"$HOME/.local/share/applications\"\nsed \"s|@DIR@|$DIR|g\" \"$DIR/quail-image-search.desktop\" > \"$HOME/.local/share/applications/quail-image-search.desktop\"\necho \"Added Quail Image Search to the applications menu.\"\n");
    packagePath = Path.Combine(outDir, packageBase + ".tar.gz");
    using var file = File.Create(packagePath);
    using var gz = new GZipStream(file, CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gz, TarEntryFormat.Pax);
    foreach (var f in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var rel = Path.GetRelativePath(publish, f).Replace('\\', '/');
        var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{packageBase}/{rel}") { Mode = (UnixFileMode)ModeFor(rel, os), DataStream = File.OpenRead(f) };
        tar.WriteEntry(entry);
        entry.DataStream.Dispose();
    }
}
else
{
    var prefix = packageBase;
    var macApp = $"{AppName}.app";
    packagePath = Path.Combine(outDir, packageBase + ".zip");
    if (File.Exists(packagePath)) File.Delete(packagePath);
    using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
    {
        void Add(string source, string entryName, int mode)
        {
            var e = zip.CreateEntryFromFile(source, entryName, CompressionLevel.SmallestSize);
            e.ExternalAttributes = (0x8000 | mode) << 16; // regular file + Unix permissions
        }
        foreach (var f in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(publish, f).Replace('\\', '/');
            var name = os == "osx" ? $"{macApp}/Contents/MacOS/{rel}" : $"{prefix}/{rel}";
            Add(f, name, ModeFor(rel, os));
        }
        if (os == "osx")
        {
            var plist = Path.Combine(Path.GetTempPath(), $"Info-{Guid.NewGuid():N}.plist");
            File.WriteAllText(plist, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>CFBundleName</key><string>{AppName}</string>
                    <key>CFBundleDisplayName</key><string>{AppName}</string>
                    <key>CFBundleIdentifier</key><string>{bundleId}</string>
                    <key>CFBundleExecutable</key><string>{ExeName}</string>
                    <key>CFBundleIconFile</key><string>app.icns</string>
                    <key>CFBundlePackageType</key><string>APPL</string>
                    <key>CFBundleShortVersionString</key><string>{version}</string>
                    <key>CFBundleVersion</key><string>{version}</string>
                    <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 Chase Grozdina</string>
                    <key>NSHighResolutionCapable</key><true/>
                    <key>NSPrincipalClass</key><string>NSApplication</string>
                    <key>LSApplicationCategoryType</key><string>public.app-category.photography</string>
                </dict>
                </plist>
                """);
            Add(plist, $"{macApp}/Contents/Info.plist", 0b110_100_100);
            Add(Path.Combine(root, "src", "QuailImageSearch", "Assets", "app.icns"), $"{macApp}/Contents/Resources/app.icns", 0b110_100_100);
            File.Delete(plist);
        }
    }
    if (os == "osx") MarkZipAsUnix(packagePath);
}
var size = new FileInfo(packagePath).Length;
Console.WriteLine($"  package: {Path.GetFileName(packagePath)} ({size / 1e6:0.0} MB)");
return 0;

// Zip files record which system made them; macOS only applies the Unix permissions (needed for the
// program to be executable) when that says Unix. .NET writes "Windows" when run on Windows, so patch
// the "version made by" field of every central directory entry.
static void MarkZipAsUnix(string path)
{
    var bytes = File.ReadAllBytes(path);
    // Find the end-of-central-directory record (searching back from the end), then walk the entries.
    var eocd = -1;
    for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 22 - 65535); i--)
        if (BitConverter.ToUInt32(bytes, i) == 0x06054b50) { eocd = i; break; }
    if (eocd < 0) throw new InvalidDataException("zip end record not found");
    long count = BitConverter.ToUInt16(bytes, eocd + 10);
    long pos = BitConverter.ToUInt32(bytes, eocd + 16);
    if (count == 0xFFFF || pos == 0xFFFFFFFF) // ZIP64: the real values are in the ZIP64 end record
    {
        var zip64 = (int)BitConverter.ToUInt64(bytes, eocd - 20 + 8); // from the ZIP64 end locator
        count = (long)BitConverter.ToUInt64(bytes, zip64 + 32);
        pos = (long)BitConverter.ToUInt64(bytes, zip64 + 48);
    }
    for (var n = 0; n < count; n++)
    {
        if (BitConverter.ToUInt32(bytes, (int)pos) != 0x02014b50) throw new InvalidDataException("bad central directory entry");
        bytes[pos + 5] = 3; // high byte of "version made by": 3 = Unix
        int nameLen = BitConverter.ToUInt16(bytes, (int)pos + 28), extraLen = BitConverter.ToUInt16(bytes, (int)pos + 30), commentLen = BitConverter.ToUInt16(bytes, (int)pos + 32);
        pos += 46 + nameLen + extraLen + commentLen;
    }
    File.WriteAllBytes(path, bytes);
}
