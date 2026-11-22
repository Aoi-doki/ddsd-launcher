#!/usr/bin/env bash
# Builds out/DDSD Launcher.exe (a Windows .NET 4 program) with Mono's C# compiler.
set -e
cd "$(dirname "$0")"
mkdir -p out
# The two fonts (Riffic Free, Aller) are not redistributed in this repo. Put RifficFree-Bold.ttf and Aller_Rg.ttf in res/
# to embed them; without them the launcher falls back to the system sans-serif.
FONTS=""
for f in RifficFree-Bold.ttf Aller_Rg.ttf; do [ -f "res/$f" ] && FONTS="$FONTS -resource:res/$f,$f"; done
mcs -nologo -target:winexe -optimize+ -out:"out/DDSD Launcher.exe" \
  -win32icon:res/icon.ico \
  -sdk:4.5 -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll \
  -reference:System.Web.Extensions.dll -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  -resource:res/ddsd_logo.png,ddsd_logo.png \
  $FONTS \
  -resource:res/monikas_waltz.mp3,monikas_waltz.mp3 \
  src/Launcher.cs src/Mods.cs src/ModHub.cs src/Music.cs src/Beta.cs src/News.cs
ls -la "out/DDSD Launcher.exe"
