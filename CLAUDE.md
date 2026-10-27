# Quail Image Search

A local desktop app (C# / Avalonia) for searching image folders by text; see README.md.

Read [docs/technical-notes.md](docs/technical-notes.md) before changing models, indexing, scoring, GPU code or search tools; keep it up to date when those change. Release builds are covered in [docs/releasing.md](docs/releasing.md).

## Privacy rules: read PRIVACY.md before adding features

Every change must follow [PRIVACY.md](PRIVACY.md). In short:
- Nothing leaves the user's computer.
- No analytics or telemetry, in our code or in dependencies; turn off any a dependency has.
- The network is used only for actions the user explicitly starts (model downloads).
- Models, indexing and search run locally.

## Upkeep

- Check new features and new or updated packages with the checklist in PRIVACY.md.
- Keep the welcome text, README and docs/ true.
- If a change can't follow the rules, stop and ask rather than weakening them.
