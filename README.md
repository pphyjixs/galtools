# galtools

A Windows desktop app for managing visual novels and browsing their resources. Organize your game library, open supported archives, preview assets, and export them in common formats.

Built with C# and WPF on .NET Framework 4.8. The application is under active development; its current interface is in Simplified Chinese.

## Features

- **Game library** — manage game folders, launch executables, cover images, tags, and notes.
- **Resource browser** — navigate folders and archives with back/forward history, search, filters, and list or thumbnail views.
- **Preview and conversion** — preview images, play supported audio, convert images to PNG and audio to WAV or MP3.
- **Extraction** — save individual files or export an entire archive while preserving its directory structure.
- **Extensible backends** — use the bundled GARbro-Mod engine or add a dedicated archive handler.

Image previews are reduced to approximately 480p. PNG exports use the full decoded image. Archive compatibility depends on the format and game-specific encryption scheme; a recognized extension alone does not guarantee support.

## Getting started

**Requirements:** Windows 10/11 x64 with .NET Framework 4.8 or later.

Run `galtools.exe` from the repository root or an extracted portable package. Keep `galtools.exe.config` and the `runtime/` directory beside it. The required archive libraries are bundled; a separate GARbro installation is not needed.

1. Add a game to the library and select its launch executable.
2. Open the resource browser and choose a game folder.
3. Double-click a supported archive, then select an entry to preview or export it.

Some encrypted archives require a game scheme to be selected manually. Multi-part archives should be opened through their main file, such as `bg.paz`; accompanying parts must remain in the same directory.

Library data is stored locally in `data/games.xml` and excluded from Git.

## Build and package

Run these commands from the repository root:

```powershell
# Build the desktop app and GARbro-Mod bridge
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

# Also create a portable ZIP
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Package
```

The build uses the installed .NET Framework compiler and the bundled dependencies, without downloading packages. The portable package is written to `release/galtools-portable.zip`.

## Project structure

```text
src/
  Galtools.Desktop/         WPF interface, game library, backend adapters
  Galtools.GarbroModBridge/ Separate process hosting GARbro-Mod
  Galtools.FormatData/      Utilities for updating the format database
runtime/garbro-mod/         Bundled libraries, bridge, and Formats.dat
vendor/GARbro-Mod/          Vendored upstream source and license
scripts/                   Build, packaging, and maintenance scripts
docs/                      Design and extension documentation
tests/                     Developer smoke checks
```

The desktop app accesses archives through `IArchiveBackend`. GARbro-Mod runs in a bridge process, while the FUYINPAK handler implements the same interface directly. Existing archive formats can gain game support through scheme and key mappings in `Formats.dat`; new containers can be added as backend implementations.

See [Adding game and archive support](docs/ADDING-GAME-FORMATS.md) for the workflow and [UI design notes](docs/UI-DESIGN.md) for interface decisions. These documents are currently in Chinese.

## Credits and upstream projects

| Project | Role in galtools |
| --- | --- |
| [GARbro](https://github.com/morkt/GARbro) by morkt | Original resource browser and format library on which GARbro-Mod is based. |
| [GARbro-Mod](https://github.com/nanami5270/GARbro-Mod) | Bundled archive readers, image/audio decoders, and game encryption schemes. |
| [NAudio](https://github.com/naudio/NAudio) | Audio utilities, including MP3 encoding through Windows Media Foundation. |

GARbro-Mod is pinned to commit [`c23bd588ddfe7907150df215f8725fe25d5b41df`](https://github.com/nanami5270/GARbro-Mod/commit/c23bd588ddfe7907150df215f8725fe25d5b41df). Its source is stored under `vendor/GARbro-Mod/`, and its runtime libraries originate from the corresponding [upstream build](https://github.com/nanami5270/GARbro-Mod/actions/runs/38232032021). The bundled `Formats.dat` also includes galtools-specific mappings maintained by `scripts/update-formats.ps1`.

Third-party components retain their respective licenses. The GARbro license is preserved in [vendor/GARbro-Mod/LICENSE](vendor/GARbro-Mod/LICENSE).

## Feedback and contributions

Report bugs or request format support through this repository's Issues. Include the app version, archive format or extension, error message, and steps to reproduce. New format support should include its detection rules, required scheme data, and a brief verification result.
