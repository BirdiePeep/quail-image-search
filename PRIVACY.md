# Privacy rules

Quail Image Search tells its users, on the first-run welcome screen:

> Your data never leaves your computer, and no analytics or other information is collected. The network is only used to download image models when you ask. Models and searches run entirely locally.

These rules keep that promise true. Check every new feature, and every new or updated dependency, against them.

## Rules

1. **Nothing leaves the computer.** The user's images, file names, paths, folders, searches, results, indexes and embeddings, preferences and logs are never uploaded, synced or shared. The only exception is something the user explicitly does themselves, such as copying or dragging a file into another app.

2. **No analytics, telemetry, crash reporting or usage tracking**, in our code or in any dependency. If a library has telemetry, turn it off in code before it can run. For example, ONNX Runtime's `OrtEnv.DisableTelemetryEvents()` is called in `ClipModel`'s static constructor, before any model loads.

   **Known exception (disclosed): DirectML on Windows.** The app itself collects and sends nothing. But on Windows, GPU acceleration uses Microsoft's DirectML:
   - It has no network code, but it logs Windows diagnostic events (`Microsoft.Windows.AI.MachineLearning.Dml`).
   - Windows may send those to Microsoft, according to the PC's "Diagnostic data" setting.
   - Unlike ONNX Runtime, the app can't turn this off. Setting **Use GPU** to **Off** avoids it: models then run on the CPU and DirectML isn't loaded.
   - This is disclosed in the welcome screen (on Windows), next to the Use GPU setting in Preferences, and in the README's "Your data" section. The wording lives in `Services/PrivacyText.cs`.

3. **The network is used only for actions the user explicitly starts.** Today that means model downloads: `ModelManager`, started by an Install or Download button.
   - No background calls, update checks, license checks, or automatic online fetches of any kind.
   - The online model-list fetch (`ModelCatalog.RefreshAsync`) stays unused. If it's ever enabled, it must run only when the user asks.

4. **Models, indexing and search run locally.** This covers scoring, calibration, previews, rotation and any future processing. No cloud APIs or remote inference.

5. **External links open only when clicked, in the user's browser.** For example, a model's License or Source page. The app never loads web content itself: no remote fonts, images or pages.

6. **New dependencies are checked before they're added.** Look for telemetry, analytics and network use. Read the package's documentation on data collection, and scan the shipped libraries of a Release build (see the checklist). Debug-only packages, like `AvaloniaUI.DiagnosticsSupport`, must stay out of Release builds.

7. **What we tell users must stay true.** Keep the welcome text, the README (including "Your data") and these rules in step with the code. If a change would make a statement untrue, change the code, or change the text together with the code, never just one of them.

8. **Optional external command-line tools are separate programs; their behaviour is their own.** The user adds them themselves, and they may use the network. The app doesn't add any by default, and the docs say so.

Local data also stays in known places:
- The app's own files live only under the app-data folder (`AppPaths.DataDir`).
- Other files change only through an explicit user action (Delete, Rotate).
- Delete moves files to the Recycle Bin or Trash and never removes them permanently.

## Checklist for new features

- [ ] Does it send anything off the computer, or use the network? If so, only on an explicit user action, and described to the user (rules 1, 3).
- [ ] Search the diff for network use: `HttpClient`, `WebRequest`, `WebClient`, `Socket`, `TcpClient`, `UdpClient`, `Dns.`, and hard-coded `http://` / `https://` addresses. The only expected network code is the model download in `ModelManager`.
- [ ] Any new or updated package? Check its documentation for telemetry, and scan the Release build's libraries for "telemetry", "analytics" and web addresses (rules 2, 6). The last full scan, on 2026-09-30, found telemetry only in ONNX Runtime, which is turned off. `Avalonia.Themes.Simple` (added the same day) has no network code or telemetry. DirectML's diagnostics can't be turned off, so it's disclosed (see rule 2).
- [ ] Does any component collect or report anything that the app can't switch off? Disclose it the same way as DirectML: say so in the welcome screen, next to the setting that avoids it, in the README's "Your data" section and under rule 2. Offer a way to avoid it.
- [ ] Does all processing still run locally (rule 4)?
- [ ] Does it store data only under the app-data folder, and change the user's files only when asked (local data)?
- [ ] Are the welcome text, README and this file still accurate (rule 7)?
- [ ] Try it without touching real data: point `QUAILIMAGESEARCH_DATA_DIR` at an empty folder.
