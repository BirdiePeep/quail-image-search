# Tag probe training (removed; how to bring it back)

A "Training" page in Preferences used to let the app learn **custom tags** for a downloaded model, then search by those tags. It worked, but was removed on 2026-09-30 to keep the app simpler for now. The full code is archived in [`training/`](training/), and this document explains how it worked and how to restore or rebuild it.

## What it did

A **linear probe** is one small logistic-regression classifier per tag, trained on a model's *frozen* image embeddings. The model itself isn't changed. For searching by tags, a probe is much more accurate than typing the tag as a text query, because each classifier has learned from thousands of tagged examples what its tag looks like.

1. In Preferences > Training you picked a **dataset folder**, a **model** (e.g. SigLIP B/16), **Min images per tag** (default 100) and **Epochs** (default 10), then pressed **Train**.
2. When it finished, the tool dropdown gained a **"*model* · tags"** search tool, and the Training page listed each trained probe with its accuracy and a Delete button.

## Dataset expectations

A folder (subfolders included) of **Parquet** files in the layout Hugging Face uses for image datasets:
- one row per image;
- the encoded image (JPEG/PNG) in a binary column, usually the `bytes` field of an `image` struct (`image.path` holds the original file name);
- the tags in a text column, named `tags`, `text`, `tag_string` or `caption` (checked in that order), either **space-separated** (`species:wolf black_fur rating:safe`) or as a **list of strings**. Tags may carry a category prefix (`species:`, `meta:`, `rating:`).

The **Check** button counted the images and showed which columns would be used, reading only the Parquet file footers.

## Design

**1. Embedding the dataset (resumable cache).**
- Every image is embedded once with the chosen model, on the GPU when available, using the same GPU settings, batch size and decode prefetch as folder indexing.
- The embeddings are cached **next to the dataset** in `.imagesearch-features\<model id>\`: one `<file>.f16` (half-precision floats, row-major) and one `<file>.tags` (one line of tags per image) per Parquet file, plus `version.txt` (the model's embedding version and dimension).
- A Parquet file counts as done once its `.tags` file exists (they're written as `.tmp` and renamed), so a cancelled or failed run continues where it stopped.
- A different model version wipes the cache. Unreadable images are skipped.
- The cache is about 430 MB for 280,000 images at 768 dimensions.

**2. Training.**
- Tags with at least *Min images per tag* training images are used, most frequent first.
- 1 image in 20 is held out for validation, chosen by a multiplicative hash of the row number, so the split is deterministic.
- Embeddings are standardized per dimension with the training set's mean and standard deviation.
- Each tag's weights start at 0 and its bias at the tag's base rate (logit of its frequency).
- Training uses Adam (lr 1e-3, cosine decay to 0, decoupled weight decay 1e-4) with batches of 1,024.
- The loss is binary cross-entropy, computed stably from the logit.
- Tags are split into parallel blocks. Each tag's loss depends only on its own weights, so each tag's forward pass, gradient and Adam update happen in one pass over the batch, with no full gradient matrix held in memory.
- Tags are stored as integer ids, not strings, to save memory.

**3. Evaluation.**
- Each tag's **average precision** is measured on the held-out images.
- The mean over tags with held-out examples is shown. The Training page also listed the best and worst tags, and the debug log listed the most common tags' scores.

**4. Saved probe.**
- One file per model: `%APPDATA%\QuailImageSearch\probes\<model id>.probe`.
- Contents: the magic string `ISPROBE1`, a length-prefixed JSON header (model id, embedding version, dimension, dataset folder, image counts, settings, mean AP, date, and per-tag stats), then float32 arrays: mean, 1/std, weights (tags × dimension, tag-major) and bias.
- Size: about 20 MB for 6,800 tags.

**5. Searching** (`TagProbeTool`, tool id `probe:<model id>`, named "*model* · tags"):
- The query is tags separated by spaces or commas.
- A leading `-` excludes a tag.
- A tag can be typed without its category prefix when that's unambiguous (`wolf` → `species:wolf`).
- An unknown tag gives an error with suggestions.
- The score is the **lowest** per-term probability (p, or 1 − p for an excluded tag) × 100, so every term has to match.
- Scores are already 0–100, so the tool declared a fixed scale (`ISearchTool.FixedScale`) instead of being calibrated.
- It reused the model tool's index: `EmbeddingModelTool.SearchAsync(request, label, makeScorer, …)` with a scorer that standardizes each stored embedding and applies the probe.
- The probe only applies to embeddings from the same model version.

**6. Background run.**
- Training was owned by a `ProbeTrainer` (one run at a time) that kept going when Preferences was closed.
- Progress showed in Preferences and in the main window's status bar: images/s over the last 3 s, and time left from a 30 s average.

## Measured numbers (RTX 3080, Ryzen 9 3900X)

- **Embedding:** about 183 images/s with SigLIP B/16 (224), batch 64 with a fixed input shape. That's roughly 25 minutes for 280,000 images.
- **Training:** a synthetic dataset of 279,296 images, 768 dimensions and 12,000 tags took about 40 s per epoch using 0.8 GB of memory, so 10 epochs take about 7 minutes. On that synthetic data the mean average precision was 0.93 after 2 epochs.

## How it was tested

- A small synthetic Parquet dataset built from the app's calibration images, in both layouts (the struct `image` + space-separated `text`, and a list-of-strings `tags` column), with one corrupt image. That checked reading, GPU embedding, skipping bad images, resuming, training, and tag queries (including `-` exclusion and prefix-less matching).
- A full-size synthetic feature cache: each image's embedding was the sum of its tags' random prototype vectors plus noise. That measured speed and memory, and confirmed the probes learn.
- **Parquet.Net quirks:**
  - It reports text and binary columns as `ReadOnlyMemory<char>` / `ReadOnlyMemory<byte>`.
  - List columns had to be read raw (`ReadRawColumnDataBaseAsync`, then rebuilt from the repetition and definition levels), because `ReadAsync` with repetition levels sizes a buffer by row count and fails on lists.

## What's archived

| Path | What |
|---|---|
| `training/src/Services/Probes/TagDataset.cs` | Parquet reading (column detection, row groups, list columns) |
| `training/src/Services/Probes/ProbeTrainer.cs` | Embedding cache, training loop, evaluation, progress |
| `training/src/Services/Probes/LinearProbe.cs` | Probe weights, inference, file format |
| `training/src/Services/Probes/ProbeStore.cs` | Saved probes per model (load, save, delete, change events) |
| `training/src/Services/Tools/TagProbeTool.cs` | The "· tags" search tool and query parsing |
| `training/training-shared-files.patch` | Everything the feature changed in shared files (see below) |

The patch re-adds:
- the Training tab (`PreferencesWindow.axaml`, plus the dataset folder picker in `.axaml.cs`) and its view model section (`PreferencesViewModel`: dataset/model/settings, Check/Train/Cancel/Delete, trained probe list, probe tools in the default-tool list);
- the `ProbeStore`/`ProbeTrainer` wiring in `App.axaml.cs`, `MainViewModel` (including the status bar progress) and `MainWindow.axaml.cs`;
- the probe tools in `ToolRegistry`;
- `Preferences.ProbeDatasetFolder`/`ProbeMinExamples`/`ProbeEpochs`;
- `ISearchTool.FixedScale` and its use in `ScoreCalibrator.ScaleFor`;
- `ClipModel.Preprocess(byte[])` (decoding images from memory);
- the `Parquet.Net` 6.1.0 package reference;
- the README sections.

## Restoring it

From the repository root:

```bash
git apply docs/archive/training/training-shared-files.patch
```

```bash
cp -r docs/archive/training/src/Services/Probes src/QuailImageSearch/Services/
```

```bash
cp docs/archive/training/src/Services/Tools/TagProbeTool.cs src/QuailImageSearch/Services/Tools/
```

Then build. This was verified the day it was removed: applying the patch and copying the files back into a copy of the tree reproduced the original files exactly, and the copy built. The patch was made against the code as it was then (uncommitted at the time). If the shared files have changed since, `git apply` may refuse some hunks; apply with `git apply --reject`, and merge the `.rej` parts by hand using the list above. The README was cut down on 2026-10-26, so its hunk will be rejected: add a short line to the README's feature list instead, and any technical details to `docs/technical-notes.md`.

Trained probes and embedding caches from before the removal still work after restoring: they're only read, never deleted by the removal. The probes live in `%APPDATA%\QuailImageSearch\probes\`, and the caches in `.imagesearch-features\` next to each dataset.
