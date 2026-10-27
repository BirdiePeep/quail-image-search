# Quail Image Search

A desktop app for searching folders of images by describing what's in them ("a dog on a beach", "red car at night"). It runs on Windows, macOS and Linux, and everything happens on your computer.

## How it works

1. **Download a model.** On first start the app offers the recommended model (SigLIP B/16). Others (OpenAI CLIP, Apple MobileCLIP, Google SigLIP) are in Preferences > Models.
2. **Pick a folder and type a search.** The first search of a folder indexes it, which takes a while; after that, searches take a fraction of a second and only new or changed images are indexed.
3. **Browse the results.** Results are scored 0–100 (100 is a strong match) and shown as thumbnails. Raise **Min score** to hide weak matches.

You can also search by image: right-click a result and choose **Find similar**, or drop an image on the search bar.

## Features

- Tabs, each with its own folder, search and model (`Ctrl+T` / `Ctrl+W`)
- Copy (`Ctrl+C`), drag out, open, show in folder, or delete (to the Recycle Bin/Trash) the selected images
- Preview window (double-click) with arrow-key browsing and rotation (`R`) that fixes wrongly rotated photos
- Uses the GPU when available (Windows and macOS), and the CPU otherwise
- Your tabs and results are restored the next time you open the app

On macOS, `Cmd` replaces `Ctrl`.

## Download and install

Download the file for your computer from the [latest release](https://github.com/BirdiePeep/quail-image-search/releases/latest). You don't need to install anything else.

| Computer | File | How to install |
|---|---|---|
| Windows | `…-win-x64-setup.exe` (`win-arm64` for ARM PCs) | Run it. No admin rights needed. |
| Mac (Apple Silicon) | `…-macos-arm64.zip` | Unzip it and move **Quail Image Search** to Applications. |
| Linux | `…-linux-x64.tar.gz` | Extract it and run `QuailImageSearch` from that folder. To add it to the applications menu, run `./install-desktop-entry.sh` there. |

The Windows and Mac downloads aren't signed yet, so the first time you open the app:
- **Windows** shows "Windows protected your PC": click **More info**, then **Run anyway**.
- **macOS** says it can't check the app: right-click it, choose **Open**, then **Open** again.

There are also `.zip` versions for Windows that run without installing. Intel Macs aren't supported.

## Building from source

### .NET SDK 10

Check whether it's installed:

```bash
dotnet --list-sdks
```

If there's no `10.x` entry, install the .NET 10 SDK from https://dotnet.microsoft.com/download. On macOS you can use `brew install dotnet`. On Linux, use your distro's package or Microsoft's install script.

### Build and run

```bash
dotnet run --project src/QuailImageSearch
```

To build release packages, see [docs/releasing.md](docs/releasing.md). For notes on how the app works inside, see [docs/technical-notes.md](docs/technical-notes.md).

## Your data

The app follows the privacy rules in [PRIVACY.md](PRIVACY.md): nothing leaves your computer, there's no analytics or telemetry, and the network is only used to download models when you ask.

Everything the app stores is in the app-data folder (`%APPDATA%\QuailImageSearch` on Windows, `~/.config/QuailImageSearch` elsewhere): downloaded models, the search indexes (full file paths plus an embedding per image, which roughly describes its content), `session.json` (tabs, queries and results), `preferences.json` and `calibration.json`. None of it is encrypted, and none of it leaves your computer. One caveat on Windows: GPU acceleration uses Microsoft's DirectML, which can log diagnostic events that Windows may send to Microsoft, according to your Windows "Diagnostic data" setting. Setting Preferences > Use GPU to Off avoids it. Uninstalling the app doesn't remove the app-data folder; delete it to remove it all.

## License

Quail Image Search is released under the ISC license; see [LICENSE](LICENSE). The libraries it ships with, the bundled calibration images and the downloadable models have their own licenses, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) with the full texts in [licenses/](licenses/). Two need attention: DirectML (Windows GPU support) is under Microsoft's own redistributable license terms rather than an open-source license, and Apple's MobileCLIP models are for non-commercial research only.
