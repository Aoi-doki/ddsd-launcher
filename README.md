# DDSD Launcher

The Windows launcher and updater for [Doki Doki Second Draft](https://aoi-doki.github.io/ddsd/), a free fan mod of
Doki Doki Literature Club. It installs the mod next to the player's own copy of DDLC, checks for updates, and keeps
the mod up to date.

What it does:
- Downloads the mod release from https://github.com/Aoi-doki/ddsd-releases and checks its SHA-256 against `latest.json`.
- Installs into `%LocalAppData%\DDSD` on Windows and `~/.local/share/DDSD` on Linux. It never needs administrator rights.
- Starts the game, shows the news feed, and manages optional mods.

It does not collect personal data, and it does not run anything it did not download from the release feed above.

## Build

Launcher 2 is a [Tauri 2](https://tauri.app) app: a Rust backend (`src-tauri/`) and a TypeScript interface (`ui/`).
It runs on Windows and Linux. The old C# launcher (Windows only) is kept in `legacy-csharp/`.

```
npm ci
npx tauri build --no-bundle      # binary in src-tauri/target/release/
cd src-tauri && cargo test       # DDSD_TEST_ZIP=<ddsd-game.zip> also tests unpacking a real release
```

On Linux you need `libwebkit2gtk-4.1-dev` and `libgtk-3-dev`. GitHub Actions builds both platforms on every `v*` tag
(`.github/workflows/build.yml`), so every published exe can be matched to a commit here.

Env vars: `DDSD_HOME` (install folder), `DDSD_FEED` (a folder or URL with `latest.json`, for testing).

## Licence

The source code is MIT (see `LICENSE`). Files in `ui/public/` and `legacy-csharp/res/` (logo, icon, music) belong to the DDSD project or their own
authors, and are not covered by the MIT licence.
