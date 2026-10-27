# Third-party notices

Quail Image Search is licensed under the ISC license (see [LICENSE](LICENSE)). It includes, uses or can download the third-party components below, each under its own license. Full license texts that must accompany the binaries are in [licenses/](licenses/); release builds copy that folder, this file and LICENSE next to the program.

## Libraries shipped with the app

| Component | Version | License | Copyright |
|---|---|---|---|
| Avalonia (Avalonia, .Desktop, .Win32, .X11, .Native, .FreeDesktop, .FreeDesktop.AtSpi, .Skia, .HarfBuzz, .Remote.Protocol, .Themes.Fluent, .Themes.Simple, .Fonts.Inter) | 12.1.3 | MIT | © The AvaloniaUI Project |
| Inter font (inside Avalonia.Fonts.Inter) | 3.019 | SIL Open Font License 1.1 (the license is in the font's metadata) | © The Inter Project Authors (rsms.me/inter) |
| ANGLE (Avalonia.Angle.Windows.Natives) | 2.1.27548 | BSD-3-Clause, see licenses/ANGLE-LICENSE.txt | © The ANGLE Project Authors |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | © .NET Foundation and Contributors |
| MicroCom.Runtime | 0.11.6 | MIT | © Nikita Tsukanov |
| Tmds.DBus.Protocol (Linux only) | 0.94.1 | MIT | © Tom Deseyn |
| SkiaSharp, HarfBuzzSharp (+ native assets) | 3.119.4 / 8.3.1.3 | MIT, see licenses/SkiaSharp-HarfBuzzSharp-LICENSE.txt; the native Skia, HarfBuzz and their dependencies (BSD-3-Clause, the HarfBuzz "old MIT" license, FreeType, libpng, zlib and others) are listed in licenses/SkiaSharp-HarfBuzzSharp-native-THIRD-PARTY-NOTICES.txt | © Xamarin, Inc.; © Microsoft Corporation; Google; HarfBuzz authors |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.12 | MIT | © Microsoft Corporation |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | Apache-2.0, see licenses/Apache-2.0.txt | © SourceGear, LLC |
| SQLite (inside e_sqlite3) | 3.x | Public domain | D. Richard Hipp and contributors |
| ONNX Runtime (Microsoft.ML.OnnxRuntime.DirectML / Microsoft.ML.OnnxRuntime, .Managed) | 1.24.4 | MIT, see licenses/onnxruntime-LICENSE.txt; its own third-party components are in licenses/onnxruntime-ThirdPartyNotices.txt | © Microsoft Corporation |
| DirectML (Microsoft.AI.DirectML, Windows only) | 1.15.4 | **Microsoft Software License Terms** (not open source; redistribution is allowed inside applications on Windows): see licenses/DirectML-LICENSE.txt and licenses/DirectML-ThirdPartyNotices.txt | © Microsoft Corporation |
| System.Numerics.Tensors | 9.0.0 | MIT | © Microsoft Corporation |

Not shipped: `AvaloniaUI.DiagnosticsSupport` (with its dependencies `Microsoft.Extensions.*.Abstractions` and `Microsoft.IO.RecyclableMemoryStream`) and `Avalonia.BuildServices` are used only when building or in Debug builds.

The MIT license text that applies to the MIT components above (with each component's copyright line from the table):

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Bundled images

The 64 score-calibration images in `src/QuailImageSearch/Calibration/images` are public-domain (6) or CC0 (58) photos from Wikimedia Commons. `src/QuailImageSearch/Calibration/manifest.json` lists each image's source page, title, author and license; it ships next to the images.

## Downloadable models (not included; downloaded only when the user asks)

The image-text models are downloaded from Hugging Face by the user, and each keeps its own license. The Models page in Preferences shows every model's license with a link, and warns about MobileCLIP's restriction.

| Model | License |
|---|---|
| CLIP ViT-B/32 (full and 8-bit), CLIP ViT-B/16 (OpenAI) | MIT |
| SigLIP B/16 224 (full and 8-bit), SigLIP B/16 384 (Google) | Apache-2.0 |
| MobileCLIP-S0, -S2, -B (Apple) | **Apple AMLR: research use only, not for commercial use** |

## Optional external tools

Command-line tools are separate programs that the user installs and adds. They're not part of Quail Image Search and have their own licenses and behaviour.
