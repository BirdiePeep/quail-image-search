# Technical notes

Things that aren't obvious from the code and help when working on it. For what the app does, see the [README](../README.md); for the privacy rules every change must follow, see [PRIVACY.md](../PRIVACY.md); for building release packages, see [releasing.md](releasing.md).

## Models

The model list is data: `src/QuailImageSearch/Services/Models/models.json`, embedded in the app. Each entry pins a Hugging Face repo to a commit, lists the three files (`vision_model.onnx`, `text_model.onnx`, `tokenizer.json`) with sizes and SHA-256 hashes, and records the preprocessing and text conventions. Downloads are verified against those hashes.

- Regenerate it with `python tools/generate_model_catalog.py` (re-pins commits and refetches hashes; `--add-hashes` only fills in missing ones).
- Entries that don't validate are dropped on load, because ids and paths become folder names and URLs.
- `ModelCatalog.RefreshAsync(url)` can fetch a list from the internet, but it isn't wired up. Connecting it is a network feature, so it must go through PRIVACY.md.

Adding a CLIP-compatible model is just a new entry. A new family needs its tokenizer and conventions in `ClipModel` and `ClipTokenizer.cs`. The conventions differ in ways that silently break results if wrong:

| Family | Models | Tokenizer | Image preprocessing | Text input |
|---|---|---|---|---|
| `clip` | OpenAI CLIP | CLIP byte-level BPE | short side to 224, center crop, bicubic, CLIP mean/std | unpadded |
| `clip` | MobileCLIP | CLIP byte-level BPE | short side to 256 (224 for B), center crop, **bilinear**, no normalization | padded to 77 with id 0 |
| `siglip` | SigLIP | SentencePiece Unigram (lowercase, ASCII punctuation removed) | squashed to size x size, bicubic, mean/std 0.5 | padded to 64 with `</s>` |

Correctness reference: tokenizers match Hugging Face `tokenizers` exactly, and scores match a Pillow + `tokenizers` + onnxruntime pipeline to about 0.001–0.002 (`PillowResize` ports Pillow's antialiased resizing). Re-check against that pipeline after changing preprocessing.

## Indexing and search

- Folders are scanned recursively, skipping hidden folders, `@eaDir`, `node_modules` and `.git`; symlinks and junctions aren't followed. Images over 100 MP are skipped.
- Embeddings are stored per model in `indexes/<model>.sqlite`, keyed by full path, so a parent folder's index covers its subfolders. Changes are detected by size and modified time; unreadable files are recorded so they aren't retried until they change.
- Changing the model revision or the preprocessing version rebuilds the index automatically, so bump the version when preprocessing changes.
- Search is a brute-force cosine scan of every embedding under the folder: fine up to hundreds of thousands of images, after which a vector index would be needed.
- HEIC, AVIF and RAW aren't supported.

## Score calibration

Raw scores sit in very different ranges per model (CLIP ~0.15–0.35, SigLIP ~−0.05–0.15), so each tool's scores are mapped linearly to 0–100 and clamped:
- **0** = median score of an image against *other* images' captions (an unrelated pair);
- **100** = 90th percentile score of an image against *its own* caption (a strong match).

The anchors come from 64 captioned public-domain/CC0 photos in `src/QuailImageSearch/Calibration` (`manifest.json` records sources and licenses; `tools/build_calibration_set.py` rebuilds it). Calibration runs through the tool's normal search, at startup and after a download, and is stored in `calibration.json` tied to the model revision and preprocessing version. Sessions store raw scores and rescale on display.

Image-to-image searches use a separate 0 anchor (the median similarity between calibration photos).

## GPU

Only the image encoder runs on the GPU (DirectML on Windows, Core ML on macOS); text encoding stays on the CPU, where a single query takes under 10 ms. In **Auto** mode, per model:

1. Pick the card with the most video memory, read from DXGI (WMI caps it at 4 GB).
2. **8-bit models always use the CPU:** loading one on DirectML crashed the app.
3. The image encoder must fit in video memory, then a test image is run; any failure falls back to the CPU.
4. **Crash guard:** a native crash while loading onto the GPU can't be caught, so a marker left in `gpu-probe` (in the data folder) sends that model to the CPU from then on. Setting Use GPU to **On** retries it.

GPU sessions are compiled for a fixed batch shape (Preferences > GPU batch size, default 32; half for inputs above 256 px); the last batch is padded, and on failure the batch size is halved and retried. While one batch runs, the next two are decoded on all cores. CPU and GPU embeddings agree to 5 decimal places, so indexes and calibration are shared.

The Core ML path hasn't been tested on a Mac. Linux is CPU only; CUDA could be added with ONNX Runtime's GPU package.

## Search tools

Every search goes through `ISearchTool` (query, folder, max results → paths with scores), registered in `ToolRegistry`. `EmbeddingModelTool` handles downloaded models.

`CommandLineTool` lets users plug in any program that prints one result per line. **It's switched off** via `Features.CommandLineTools` in `Services/Features.cs`; saved tools stay in `preferences.json`. When re-enabling it, keep its safety rules: placeholders (`{query}`, `{folder}`, `{top}`) are passed as single arguments, executables run by full path, and `.bat`/`.cmd` are refused because `cmd.exe` would interpret `&` and `"` in queries or folder names.

## Data folder

- Set `QUAILIMAGESEARCH_DATA_DIR` to use a separate, empty data folder, e.g. to test the first-run experience without touching your models and indexes.
- The app used to be called Image Search. On first start, if `QuailImageSearch` doesn't exist, the old `ImageSearch` folder is copied to it.
- Two copies of the app can run at once; whichever closes last decides what's saved in `session.json`. Deleting an index in use by a search or another copy is refused.

## Platform quirks

- Avalonia has no virtualizing wrap panel, so the results grid is a virtualized list of rows whose column count follows the window width.
- Linux "Show in folder" opens the folder but can't select the file.
- Delete on Linux copies files from other drives into `~/.local/share/Trash`; on macOS, Finder's "Put Back" doesn't work and Full Disk Access may be needed. On Windows, drives without a Recycle Bin keep the file and report it.
- Rotation edits only the EXIF Orientation tag for JPEGs; 8-bit PNGs are re-encoded losslessly with metadata kept; other formats can't be rotated.
