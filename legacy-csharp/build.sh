#!/usr/bin/env bash
# Builds out/DDSD Launcher.exe (a Windows .NET 4 program) with Mono's C# compiler.
set -e
cd "$(dirname "$0")"
mkdir -p out
# The two fonts (Riffic Free, Aller) are not redistributed in this repo. Put RifficFree-Bold.ttf and Aller_Rg.ttf in legacy-csharp/res/
# to embed them; without them the launcher falls back to the system sans-serif.
FONTS=""
for f in RifficFree-Bold.ttf Aller_Rg.ttf; do [ -f "legacy-csharp/res/$f" ] && FONTS="$FONTS -resource:legacy-csharp/res/$f,$f"; done
mcs -nologo -target:winexe -optimize+ -out:"out/DDSD Launcher.exe" \
  -win32icon:legacy-csharp/res/icon.ico \
  -sdk:4.5 -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll \
  -reference:System.Web.Extensions.dll -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  -resource:legacy-csharp/res/ddsd_logo.png,ddsd_logo.png \
  $FONTS \
  -resource:legacy-csharp/res/monikas_waltz.mp3,monikas_waltz.mp3 \
  legacy-csharp/src/Launcher.cs legacy-csharp/src/Mods.cs legacy-csharp/src/ModHub.cs legacy-csharp/src/Music.cs legacy-csharp/src/Beta.cs legacy-csharp/src/News.cs
ls -la "out/DDSD Launcher.exe"
