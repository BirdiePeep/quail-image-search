# Quail Image Search

A cross-platform desktop app for searching a folder of images by text description. It isn't tied to one search engine: each tab picks a **search tool** from a dropdown. You can download image-text models that run inside the app (no Python needed), or use any command-line search tool.

- **UI:** C# / [Avalonia 12](https://avaloniaui.net/) (MVVM, CommunityToolkit.Mvvm). Runs on Windows, macOS and Linux.
- **Built-in models:** ONNX image-text models (OpenAI CLIP, Apple MobileCLIP and Google SigLIP) downloaded from Hugging Face, run with ONNX Runtime, and indexed in a local SQLite database.
- **Command-line tools** (switched off for now): any program that prints one result per line, run once per search. See "Search tools" below. There's no server and no network port.

## Features

- Tabs: one search per tab (`Ctrl+T` new, `Ctrl+W` close, or use the `+` / `✕` buttons)
- **Models** (Preferences > Models): download any of 9 models from the built-in list: CLIP ViT-B/32 (full and 8-bit), CLIP ViT-B/16, MobileCLIP S0/S2/B, and SigLIP B/16 at 224 (full and 8-bit) and 384. The page shows each model's family, size, license (with a warning for MobileCLIP's research-only license) and source. Each installed model becomes a search tool. Downloads are checked against the SHA-256 hash recorded in the model list before they're installed.
- Each tab has a search-tool dropdown, a query box, a max-results setting, and a folder picker
- **Search by image** (built-in models): right-click a result (or the preview) and choose **Find similar** to search the same folder for images like it in a new tab, or drop an image file on the search bar, or click the picture button in the query box to pick one. The image can be inside or outside the folder. Image searches score 0 for two unrelated photos and 100 for the same image (the 0 anchor is the median similarity between the bundled calibration photos), and start with Min score 25. Click ✕ on the image to go back to text. Command-line tools search by text only.
- **Preferences** (`File > Preferences…` or `Ctrl+,`) cover the default tool for new tabs, default Max results and Min score, the look (Avalonia's built-in **Fluent** or **Simple** theme, light, dark or following the system, and Fluent's normal or compact density), and downloadable models. Light/dark and density change immediately; a different theme is used from the next start. They're saved to `preferences.json` in the same folder as the session.
- **Scores on a 0–100 scale for every tool:** 0 means an unrelated image and 100 a strong match (see "Score calibration" below). Hover a tile to see its raw score.
- **Min score** filter (default 40) hides low-scoring results instantly without re-running the search. All results the tool returned are kept, so lowering the filter brings them back.
- **First run:** while no model is installed, the app opens with a short explanation of how it works and offers to download the recommended model, SigLIP B/16 (224), which then becomes the default search tool. You can also pick another model in Preferences > Models, or skip it (you're asked again at the next start).
- **Session restore:** tabs, queries, folders, settings, results, the selected tab, the log panel and the window position/size are saved to `session.json` in the app-data folder (`%APPDATA%\QuailImageSearch` on Windows, `~/.config/QuailImageSearch` on Linux/macOS). To try the app with a separate, empty data folder (e.g. to see the first-run experience without touching your models and indexes), set the environment variable `QUAILIMAGESEARCH_DATA_DIR` to another folder (the old name, `IMAGESEARCH_DATA_DIR`, still works). The session is saved on close, and also after every search and tab close.
- **Debug log** panel (`Ctrl+L`) shows each search: the query, the exact command that ran, the tool's raw output and timings
- **Thumbnail size slider** at the bottom right of each tab: 50–200% in 10% steps. The grid reflows live, thumbnails are re-decoded sharper when enlarged, and the size is saved with the tab; new tabs start at the current tab's size. A reset button next to it returns to 100%; it's hidden at 100%.
- Thumbnail grid of results. It's virtualized, so only visible rows are created and thumbnails load lazily off the UI thread.
- Selection works as in Explorer: click, `Ctrl`+click, `Shift`+click, arrow keys, `Ctrl+A`
- `Ctrl+C` copies the **actual files** to the clipboard, so you can paste them into Explorer, Finder or a chat app
- Drag the selected tiles out to the desktop or a file manager to copy them there
- **Preview window:** double-click an image to open it in a preview window (one window, reused; not modal, so it can sit on a second monitor; its size and position are remembered). It always shows the active tab's selected image and follows the selection. `Left`/`Right` (or the side buttons) step through the results and move the grid's selection with it; `Esc` closes it. Images are shown upright per their EXIF orientation, and the neighbouring images are decoded ahead.
- **Rotate** in the preview (`R` right, `Shift+R` left) fixes images saved with the wrong orientation and saves the file: JPEGs losslessly (only the EXIF Orientation tag changes; the image data is untouched), 8-bit PNGs by rotating the pixels and saving the PNG again, keeping its colour profile, gamma and text metadata (palette and grayscale PNGs are saved as RGBA, which is still lossless). 16-bit PNGs and other formats can't be rotated. The thumbnail updates, and the image is re-indexed on the next search.
- `Enter` opens the selection in the OS default app
- Right-click menu: Open, Show in folder (selects the files in Explorer/Finder), Copy, Find similar, Delete
- **Delete** (right-click or the `Delete` key) moves the selected files to the Recycle Bin/Trash after one confirmation; they disappear from all tabs and indexes. Files on drives without a Recycle Bin (network drives, USB sticks and memory cards) are kept and reported. If Windows can't recycle a file for another reason (the Recycle Bin is turned off for that drive, or the file is too big for it), Windows asks before deleting it permanently. Files the user doesn't confirm are kept.

On macOS, `Cmd` replaces `Ctrl`.

## Setup

### .NET SDK 10

Check whether it's installed:

```bash
dotnet --list-sdks
```

If there's no `10.x` entry, install the .NET 10 SDK from https://dotnet.microsoft.com/download. On macOS you can use `brew install dotnet`. On Linux, use your distro's package or Microsoft's install script.

## Build and run

```bash
dotnet run --project src/QuailImageSearch
```

## Building releases

`scripts\build.ps1` builds a release package for each platform into `artifacts\` (not checked in). Run it from the repository root in PowerShell; all platforms can be built on Windows.

```powershell
.\scripts\build.ps1
```

```powershell
.\scripts\build.ps1 -Platform win-x64,osx-arm64 -Version 1.0.1
```

| Platform | Package | Notes |
|---|---|---|
| `win-x64` | `QuailImageSearch-<version>-win-x64-setup.exe` and `.zip` | GPU acceleration via DirectML |
| `win-arm64` | `QuailImageSearch-<version>-win-arm64-setup.exe` and `.zip` | ARM Windows PCs, DirectML |
| `osx-arm64` | `QuailImageSearch-<version>-macos-arm64.zip` | Apple Silicon; contains `Quail Image Search.app` (Core ML) |
| `linux-x64` | `QuailImageSearch-<version>-linux-x64.tar.gz` | CPU; includes `install-desktop-entry.sh` to add it to the applications menu |

There's no Intel Mac build: ONNX Runtime, which runs the models, no longer ships a library for Intel Macs.

**What the script does** for each platform:
1. A self-contained Release publish, using `src/QuailImageSearch/Properties/PublishProfiles/<platform>.pubxml`. Users don't need .NET installed, and the package holds only that platform's libraries.
2. `tools/Packager` then:
   - removes debug symbols and import libraries (about 117 MB on Windows);
   - checks that the model engine, the program, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `licenses/` and the calibration images are present, and that no Debug-only package slipped in;
   - runs the privacy scan from [PRIVACY.md](PRIVACY.md): it fails if a library mentions telemetry or analytics (except the known, handled cases), and warns about unfamiliar web addresses;
   - packages it, setting Unix permissions correctly even when built on Windows.
3. On Windows platforms, it builds the installer from the same checked folder (see below).
4. Finally it writes `artifacts\SHA256SUMS.txt`.

**Windows installer:** `packaging/windows/QuailImageSearch.iss`, built with [Inno Setup 6](https://jrsoftware.org/isdl.php). If Inno Setup isn't installed, the script warns and builds only the zips. The installer:
- installs for the current user without admin rights (into `%LOCALAPPDATA%\Programs\Quail Image Search`), or for all users if the user chooses that;
- adds a Start menu shortcut, an optional desktop shortcut, and an entry in Settings > Apps for uninstalling;
- doesn't use the network;
- on uninstall, asks before deleting the app-data folder (models, indexes, settings) and keeps it by default. Silent uninstalls (`/VERYSILENT`) keep it unless `/DELETEDATA` is passed.

**Options:** `-Version` (default: `<Version>` in the project file), `-BundleId` (the macOS bundle id, default `com.chasegrozdina.quailimagesearch`), `-ReadyToRun` (faster start-up; roughly 30–50% bigger), and `-NoInstaller` (Windows zips only).

**Sizes** (1.0.0): about 64–70 MB per package; the Windows installers are about 51–54 MB.

**Signing** isn't set up yet, so `-Sign` stops with an explanation.
- Unsigned Windows packages and installers show a SmartScreen "unknown publisher" warning (More info > Run anyway).
- On macOS, open the unsigned app with right-click > Open the first time.
- Proper signing needs a Windows code-signing certificate, and an Apple Developer ID with notarization (done on a Mac).

## Built-in models (ONNX)

### The model list

The list is data, not code: `src/QuailImageSearch/Services/Models/models.json`, embedded in the app. Each entry has:
- the Hugging Face repo and a pinned commit;
- the three files to download, with exact sizes and SHA-256 hashes: `vision_model.onnx`, `text_model.onnx` and `tokenizer.json`;
- the model family;
- the image preprocessing: size, resize mode (`shortest_edge` + center crop, or `exact`), `bicubic` or `bilinear` filtering, mean and std;
- the text conventions: max length, and pad length and id;
- the license.

Regenerate it with `python tools/generate_model_catalog.py`. That script re-pins each repo to its current commit and fetches exact file sizes and SHA-256 hashes. `--add-hashes` only fills in missing hashes, keeping the pinned revisions. When the list is loaded, entries whose id, repo, revision, file paths or hashes don't look valid are dropped, since they become folder names and download URLs.

The list is designed to also come from the internet. `ModelCatalog.RefreshAsync(url)` downloads a list, validates it and caches it as `%APPDATA%\QuailImageSearch\model-catalog.json`. The cached copy is used instead of the built-in one on the next start. Entries for model families this version can't run are skipped. This isn't wired to the UI or to a URL yet; for now the list is local only.

### Supported families

| Family | Models | Tokenizer | Image preprocessing | Text input |
|---|---|---|---|---|
| `clip` | OpenAI CLIP | CLIP byte-level BPE | short side to 224, center crop, bicubic, CLIP mean/std | unpadded |
| `clip` | MobileCLIP | CLIP byte-level BPE | short side to 256 (224 for B), center crop, **bilinear**, no normalization | padded to 77 with id 0 |
| `siglip` | SigLIP | SentencePiece Unigram (lowercase, ASCII punctuation removed) | squashed to size x size, bicubic, mean/std 0.5 | padded to 64 with `</s>` |

Adding another CLIP-compatible model is just a new entry in `models.json`. A new family needs its tokenizer and conventions in `ClipModel` and `ClipTokenizer.cs`.

Raw scores sit in very different ranges per model: CLIP bunches around 0.15–0.35, while SigLIP sits around −0.05–0.15. They're rescaled per tool; see "Score calibration".

Models are downloaded to `%APPDATA%\QuailImageSearch\models\<id>` (`~/.config/QuailImageSearch/models` on Linux and macOS).

### GPU acceleration

The downloaded models use the GPU by default: Preferences > General > **Use GPU** is set to **Auto**.
- **Windows:** DirectML, which works with any DirectX 12 graphics card (NVIDIA, AMD, Intel). The app uses ONNX Runtime's DirectML package.
- **macOS:** Core ML.
- **Linux:** CPU only for now.

**How the device is chosen in Auto,** for each model:
1. The app reads the graphics cards and their real video memory from DXGI; Windows' WMI caps the reported memory at 4 GB. It picks the card with the most memory.
2. **8-bit models** always run on the CPU. DirectML can't run them; loading one crashed the app in testing.
3. The model's image encoder has to fit in video memory, with room to spare.
4. **Test run:** the image encoder is loaded on the GPU and a test image is run through it. If anything fails, the model uses the CPU.
5. **Crash guard:** if the app ever crashes while loading a model onto the GPU (a native crash can't be caught), a marker left in `%APPDATA%\QuailImageSearch\gpu-probe` makes that model use the CPU from then on. Setting Use GPU to **On** retries it.

**Text stays on the CPU:** only the image encoder runs on the GPU. A single short query is faster on the CPU (under 10 ms), and it halves the video memory used.

**Where you see the device:** each tab's status line shows it, for example "8 results from SigLIP B/16 (224) in 0.4s · GPU (NVIDIA GeForce RTX 3080)" or "· CPU (8-bit models run on the CPU)". Indexing progress shows it too, and the debug log records the device and the reason it was chosen. **On** skips the memory check, and **Off** always uses the CPU. Changing the setting reloads models on their next search.

**Batches and fixed input shape:** on the GPU, images are embedded in batches set by Preferences > General > **GPU batch size** (8, 16, 32, 64 or 128; default 32, which fits cards with about 4 GB; models with inputs above 256 px use half). The GPU session is compiled for exactly that input shape, and a smaller last batch is padded. If the GPU fails on a batch (for example, out of video memory), the batch size is halved and the batch retried, and the debug log says so. On the CPU, batches of 16 are used. JPEGs are decoded at a reduced scale (1/2, 1/4 or 1/8) when the result is still at least 4x the model's input size.

**Overlapped decoding:** during indexing the next two batches of images are decoded on all CPU cores while the current one runs through the model. The debug log shows, per indexing run, how long was spent waiting for decoding, running the model and saving.

**Speed on an RTX 3080** (Ryzen 9 3900X): full indexing of 1,024 JPEGs of about 2.5 MP, before (batches of 16) and after batches of 64 with a fixed shape:

| Model | CPU (model only) | GPU before | GPU now |
|---|---|---|---|
| CLIP B/32 | 57 images/s | 136 images/s | 294 images/s (decoding is now the limit) |
| SigLIP B/16 224 | 11 images/s | 94 images/s | 187 images/s (the GPU is the limit) |

Embeddings from the fixed-shape batches match the earlier ones to within float rounding (cosine similarity 0.9999999), and CPU and GPU embeddings agree to 5 decimal places, so indexes and score calibration stay valid.

### Managing indexes

Preferences > Models shows the total disk space used by all search indexes, with a **Delete all indexes** button. Each model's card also shows that model's index size, with a **Delete index** button. You're asked to confirm first.

- Deleting an index keeps the downloaded model. Folders are re-indexed the next time you search them with that model.
- An index that's in use by a running search isn't deleted; you'll see a message to try again when the search finishes.
- The same applies if another copy of the app has the index open.
- **Deleting a model** asks whether to also delete its search index. The box is ticked by default.
- **Indexes without an installed model** are listed in a highlighted box at the top of the Models page, with a Delete button for each and one for all of them. That covers models you deleted while keeping their index, and models that are no longer in the model list at all.

How a search works:
1. **Index:** the folder is scanned recursively, skipping hidden folders and `@eaDir`, `node_modules` and `.git`. Only new or changed images are embedded, detected by size and modified time. Deleted files are removed from the index. Unreadable files are recorded, so they aren't retried until they change. Embeddings are stored per model in `%APPDATA%\QuailImageSearch\indexes\<model>.sqlite`, keyed by full path, so a parent folder's index also covers its subfolders. Changing the model or preprocessing version rebuilds the index automatically. Cancelling keeps what's been embedded so far.
2. **Search:** the query is embedded, and every image under the folder is ranked by cosine similarity. The raw score is then rescaled to 0–100 (see "Score calibration").

Accuracy checks:
- For all three families, the tokenizers produce the same token ids as Hugging Face's `tokenizers` library, including for accents, CJK text, emoji and truncation.
- Scores match a Pillow, `tokenizers` and onnxruntime reference pipeline to within about 0.001–0.002. The preprocessing uses a port of Pillow's antialiased bicubic and bilinear resizing.

Indexing speed on a Ryzen 9 3900X (CPU only, images per second):

| Model | Images/s |
|---|---|
| CLIP B/32 | 32 |
| CLIP B/32 8-bit | 39 |
| CLIP B/16 | 12 |
| MobileCLIP S0 | 39 |
| MobileCLIP S2 | 21 |
| MobileCLIP B | 12 |
| SigLIP 224 | 9 |
| SigLIP 224 8-bit | 12 |
| SigLIP 384 | 3 |

A search on an indexed folder takes 10–40 ms, plus 1–2 s to load the model the first time.

## Score calibration

Every tool's raw scores are mapped to 0–100 so that one Min score means the same thing whichever tool a tab uses:
- **0** is the median score of an image against *other* images' captions, i.e. a typical unrelated pair.
- **100** is the 90th percentile score of an image against *its own* caption, i.e. a strong match.
- The mapping is linear between those anchors and clamped to 0–100.

**Calibration set:** 64 public-domain or CC0 photos shipped with the app in `src/QuailImageSearch/Calibration`, with varied subjects and a caption each. `manifest.json` records each image's caption, Wikimedia Commons source page, author and license. `tools/build_calibration_set.py` rebuilds the set.

**How it runs:** calibration searches the set once per caption through the tool's normal search, so it works the same for every kind of tool.
- **Built-in models** are calibrated automatically at startup and right after they're downloaded. That takes a few seconds per model, and a model that hasn't been calibrated yet is calibrated before its first search.
- **Command-line tools** with "Calibrate automatically" ticked are calibrated at startup if a tab or the default tool uses them, otherwise on their first search. Calibration runs the tool once per test image, so allow a minute or two.
- **Other command-line tools** are calibrated only when you click Calibrate. Until then their raw scores are shown, and new tabs start with Min score 0. Editing a tool clears its calibration.
- **Stored anchors** live in `%APPDATA%\QuailImageSearch\calibration.json`, per tool. Each is tied to the model revision and preprocessing version (or the tool's definition), so a changed model is recalibrated automatically.
- **Status:** Preferences shows each tool's calibration status with a Recalibrate button: on the model cards and in the command-line tool editor.

**What's stored:** saved sessions keep raw scores and are rescaled with the current calibration when they're shown. Min scores saved by older versions (like 0.22) are reset to the 0–100 default once.

## Search tools

Every search goes through an adapter (`ISearchTool`). The adapter takes the query, the folder and the max results, and returns a list of file paths with scores. It also writes what it ran to the debug log. Adapters are listed in `ToolRegistry`.

- **Downloaded models** (built in): see "Built-in models" above. `EmbeddingModelTool` is the adapter.
- **Command-line tools** let you plug in any program that prints results line by line, without writing code. **They're switched off for now:** the Preferences > Command-line tools page is hidden, and saved tools aren't offered as search tools (if one was the default, the recommended model is used instead), but saved tools stay in `preferences.json`. To turn them back on, set `Features.CommandLineTools` to `true` in `src/QuailImageSearch/Services/Features.cs`. When on:
  - **Executable:** a path, or a command on `PATH`, plus optional fallback locations to try if it isn't found, such as an installer's default folder (`%VARIABLES%` and `~` are expanded). It's run by its full path. Windows batch files (`.bat`, `.cmd`) are refused, because Windows runs them through `cmd.exe`, where a folder name or query containing `&` or `"` could run commands.
  - **Arguments:** a template using `{query}`, `{folder}` and `{top}`. Each placeholder is passed as a single argument, so a query containing spaces or quotes is safe. The tool runs with the searched folder as its working directory.
  - **Output pattern:** a regex applied to each line of stdout. It needs a named group `(?<path>...)`; `(?<score>...)` is optional. Lines that don't match, and paths that aren't existing image files, are skipped and counted in the log. Relative paths are resolved against the folder, and lines with no score get the "score when the line has none" value.
  - **Encoding:** output is read as UTF-8, and a leading byte-order mark is ignored. Python-based tools are run with `PYTHONUTF8=1`, so non-ASCII paths come through intact.

To add a built-in adapter in code, implement `ISearchTool` (see `EmbeddingModelTool` and `CommandLineTool`) and register it in `ToolRegistry`.

Each tab's tool is saved in `session.json`. A tab whose tool no longer exists uses the default tool.

## Project layout

```
src/QuailImageSearch/Services/Tools/         ISearchTool, ToolRegistry, CommandLineTool (generic adapter for command-line tools),
                                        EmbeddingModelTool (indexing + search for downloaded models)
src/QuailImageSearch/Services/Models/        models.json (model list), ModelCatalog, ModelManager (downloads),
                                        ClipModel (ONNX + preprocessing), ClipTokenizer.cs (CLIP BPE + SigLIP Unigram),
                                        PillowResize, EmbeddingIndex (SQLite)
tools/generate_model_catalog.py         Regenerates models.json from the Hugging Face API
tools/make_icons.ps1                    Regenerates logo.png, app.ico and app.icns in Assets from icon/icon.svg (uses Edge, offline)
src/QuailImageSearch/Services/Preferences    Preferences model (default tool, defaults, command-line tools, migration)
src/QuailImageSearch/Services/SessionStore   session.json / preferences.json storage
src/QuailImageSearch/Services/FileActions    Open, show in folder, clipboard, drag-out, move to Recycle Bin/Trash
src/QuailImageSearch/Services/ImageLoader    Decoding for thumbnails and the preview (EXIF orientation, size limits)
src/QuailImageSearch/Services/ImageRotation  Lossless rotation (JPEG orientation tag, PNG pixels)
src/QuailImageSearch/Services/Models/        (also) Prefetch (decoding ahead while indexing), RateMeter (indexing speed)
src/QuailImageSearch/ViewModels/             MainViewModel (tabs), SearchTabViewModel (tool, query, results, selection),
                                        ImageResultViewModel (tile, thumbnail cache), PreviewViewModel,
                                        PreferencesViewModel
src/QuailImageSearch/Views/                  MainWindow (menu, tabs, log), SearchTabView (search bar + grid), PreviewWindow,
                                        PreferencesWindow, WelcomeDialog, ConfirmDialog, FileCommands
```

## Known limitations / next steps

- Avalonia has no virtualizing wrap panel. The grid is therefore a virtualized list of rows, and the number of columns follows the window width.
- Command-line tools start a new process per search, so a tool that loads a model reloads it every time. There's no indexing progress in the UI for them; their output appears in the debug log. Built-in models keep their model loaded and show progress in the tab.
- GPU acceleration covers Windows (DirectML) and macOS (Core ML). Linux runs on the CPU; CUDA could be added with ONNX Runtime's GPU package. The Core ML path hasn't been tested on a Mac yet.
- The built-in model's search scans all embeddings under the folder. That's fine up to hundreds of thousands of images; beyond that, a vector index would be needed.
- HEIC, AVIF and RAW files aren't indexed by the built-in models.
- Linux "Show in folder" opens the containing folder but doesn't select the file.
- Delete on Linux moves files to `~/.local/share/Trash`, even from other drives (the file is copied there). On macOS it moves them to `~/.Trash`, where Finder's "Put Back" isn't available, and macOS may require Full Disk Access.
- Images over 100 megapixels (after JPEG's reduced-size decoding) are skipped when indexing and show no thumbnail, so a huge PNG can't use up memory. Symbolic links and junctions inside a searched folder aren't followed.
- Running two copies of the app at once is allowed, but whichever closes last decides what's saved in `session.json`.

## Your data

The app follows the privacy rules in [PRIVACY.md](PRIVACY.md): nothing leaves your computer, there's no analytics or telemetry, and the network is only used to download models when you ask.

The app used to be called Image Search, with its data in an `ImageSearch` folder. On the first start under the new name, if `QuailImageSearch` doesn't exist yet, that folder is copied to it, so downloaded models, indexes, settings and the session carry over. The old folder is left as it was; delete it once you no longer need it.

Everything the app stores is in the app-data folder (`%APPDATA%\QuailImageSearch` on Windows, `~/.config/QuailImageSearch` elsewhere): downloaded models, the search indexes (full file paths plus an embedding per image, which roughly describes its content), `session.json` (tabs, queries and results), `preferences.json` and `calibration.json`. None of it is encrypted, and none of it leaves your computer. One caveat on Windows: GPU acceleration uses Microsoft's DirectML, which can log diagnostic events that Windows may send to Microsoft, according to your Windows "Diagnostic data" setting. Setting Preferences > Use GPU to Off avoids it. Uninstalling the app doesn't remove it; delete that folder to remove it all.

## License

Quail Image Search is released under the ISC license; see [LICENSE](LICENSE). The libraries it ships with, the bundled calibration images and the downloadable models have their own licenses, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) with the full texts in [licenses/](licenses/). Two need attention: DirectML (Windows GPU support) is under Microsoft's own redistributable license terms rather than an open-source license, and Apple's MobileCLIP models are for non-commercial research only.
