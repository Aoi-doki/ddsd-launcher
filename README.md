# DDSD Launcher

The Windows launcher and updater for [Doki Doki Second Draft](https://aoi-doki.github.io/ddsd/), a free fan mod of
Doki Doki Literature Club. It installs the mod next to the player's own copy of DDLC, checks for updates, and keeps
the mod up to date.

What it does:
- Downloads the mod release from https://github.com/Aoi-doki/ddsd-releases and checks its SHA-256 against `latest.json`.
- Installs into the player's own folder under `%LocalAppData%\DDSD`. It never needs administrator rights.
- Starts the game, shows the news feed, and manages optional mods.

It does not collect personal data, and it does not run anything it did not download from the release feed above.

## Build

The release exe is built by GitHub Actions (see `.github/workflows/build.yml`) with Mono's C# compiler, so every
published exe can be matched to a commit here.

```
sudo pacman -S mono      # or apt install mono-mcs
bash build.sh
```

## Licence

The source code is MIT (see `LICENSE`). Files in `res/` (logo, fonts, music) belong to the DDSD project or their own
authors, and are not covered by the MIT licence.
