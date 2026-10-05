# MediaForge

MediaForge is a Windows 11 WPF application for turning DVD/Blu-ray sources into Plex-ready MP4 movie and TV files using **MakeMKV → HandBrakeCLI → Plex organization**.

## Current revision

**v1.0 development build — workflow/UI foundation**

This build continues the v0.9.9 Icon/BAML baseline and adds several of the final v1.0 workflow features.

### Implemented in this revision

- Correct workflow labeling: **Disc → MakeMKV → HandBrake → Plex**.
- Forging Progress panel with anvil/disc visual and the active job's real progress percentage.
- Workflow panel now scrolls vertically so controls remain reachable on smaller windows.
- Processing Queue columns can be enabled/disabled from Settings and are persisted locally.
- MakeMKV Settings includes an **Download MakeMKV** button to the official MakeMKV download page.
- MakeMKV disc imports use the optical disc volume name for the rip folder when available.
- Rip folders are collision-safe: `COSMOS_D7`, `COSMOS_D7 (2)`, etc.
- All titles from a single disc remain together in that disc folder.
- Closing the live console hides that console window instead of cancelling/closing the underlying MediaForge workflow.
- The MediaForge executable continues to use `Assets/MediaForge.ico` as its application icon while the WPF window/taskbar icon uses the embedded PNG workaround that avoids the previous BAML/ICO converter failure.
- README documentation is consolidated into this single root `README.md`.

## Workflow

```text
Optical Disc
     ↓
   MakeMKV
     ↓
 MKV Workspace
     ↓
Identification / Review
     ↓
Chapter / Episode Mapping
     ↓
Processing Queue
     ↓
 HandBrakeCLI
     ↓
    MP4
     ↓
Plex Organizer
     ↓
Movies / TV Shows
```

## Queue columns

Settings can control these Processing Queue columns:

- Status
- Source Name
- Type
- Title
- Stage
- Progress
- Ripped Size
- Final Size
- Saved

The configuration is stored in the local MediaForge settings file.

## Disc rip folders

When MakeMKV reports a disc name such as `COSMOS_D7`, MediaForge creates:

```text
<Configured MKV Folder>\COSMOS_D7\
```

If that folder already exists, MediaForge creates `COSMOS_D7 (2)`, then `(3)`, and so on. This keeps every title from one physical disc together while avoiding accidental overwrites from repeat imports.

If no usable disc name is reported, MediaForge falls back to a timestamped `disc-yyyyMMdd-HHmmss` folder.

## Live console behavior

The live console displays actual MakeMKV/HandBrakeCLI output and the command being executed. Closing the console window only hides it; it does **not** request cancellation of the active process. Use **Stop Current** in the main MediaForge window when the underlying operation should actually be stopped.

## Requirements

- Windows 11 x64
- .NET 8 SDK for development/building
- MakeMKV with `makemkvcon64.exe`
- HandBrakeCLI
- A HandBrake preset configured in Settings
- Optional TMDB API Read Access Token for identification

## Build / publish

From the repository root:

```powershell
dotnet publish src\MediaForge\MediaForge.csproj -c Release -r win-x64 --self-contained true
```

The project is configured for a self-contained Windows x64 single-file executable and uses `Assets\MediaForge.ico` as the executable application icon.

## First-run setup

Open **Settings** and configure:

1. MakeMKV CLI path.
2. HandBrakeCLI path.
3. HandBrake preset.
4. TMDB token if automatic identification is desired.
5. MKV rip folder.
6. Temporary encoding folder.
7. Final Movies folder.
8. Final TV Shows folder.
9. Processing Queue columns.

## Typical use

1. Insert a DVD/Blu-ray.
2. Click **Import Disc**.
3. Select and scan the optical drive.
4. Select the titles to rip.
5. Rip the selected titles into the disc-named MKV workspace.
6. Review or automatically identify the resulting jobs.
7. Correct TV metadata manually when necessary.
8. Add jobs to the processing queue.
9. Monitor the Forging Progress indicator and Live Console.
10. HandBrake creates MP4 output.
11. MediaForge organizes identified output into Plex-compatible Movie/TV folders.

Existing MKVs can also be imported directly with **Import Files**.

## Current identification direction

MediaForge is being developed toward multi-source identification using disc volume name, MakeMKV title information, filenames/source folders, runtime, chapters, video/audio characteristics, TMDB metadata, artwork and additional title imagery. Automatic confidence scoring and review are part of the v1.0 identification work.

## Multi-episode sources

The v1.0 architecture is intended to support a single MKV containing multiple TV episodes by mapping chapter ranges to separate MediaForge jobs. HandBrakeCLI chapter selection will be used so a large source does not need to be encoded repeatedly in its entirety.

Example:

```text
Naruto.mkv
 ├─ chapters 1-2 → S01E01
 ├─ chapters 3-4 → S01E02
 ├─ chapters 5-6 → S01E03
 └─ chapters 7-8 → S01E04
```

Automatic mapping still requires additional implementation and review UI before it should be considered complete.

## Space management

Disk-space checking is part of the remaining v1.0 work. The intended behavior is to estimate the MKV source requirement, encoded output requirement, and safety buffer separately for the temporary and final drives before starting large operations.

## Installer / distribution

The target release model is a self-contained Windows x64 application packaged with a normal Windows installer. The installer should provide:

- MediaForge.exe
- MediaForge application assets
- Desktop and Start Menu shortcuts using `MediaForge.ico`
- First-run configuration
- MakeMKV/HandBrake detection
- TMDB configuration

MakeMKV and its licensing/download requirements remain separate from MediaForge.

## Project structure

- `src/MediaForge/MainWindow.xaml` — main queue/workflow UI
- `src/MediaForge/MainWindow.xaml.cs` — queue orchestration and UI state
- `src/MediaForge/Services/MakeMkvService.cs` — MakeMKV discovery, scanning and ripping
- `src/MediaForge/Services/HandBrakeService.cs` — HandBrakeCLI integration
- `src/MediaForge/Services/WorkflowService.cs` — encoding and Plex organization
- `src/MediaForge/Services/JobDatabase.cs` — local job persistence
- `src/MediaForge/Services/SettingsService.cs` — local application settings
- `src/MediaForge/Services/TmdbService.cs` — TMDB integration
- `src/MediaForge/Views/DiscWindow.xaml` — disc import/rip UI
- `src/MediaForge/Views/IdentifyWindow.xaml` — identification/review UI
- `src/MediaForge/Views/SettingsWindow.xaml` — configuration UI
- `src/MediaForge/Assets/MediaForge.ico` — executable application icon
- `src/MediaForge/Assets/MediaForgeTaskbar.png` — WPF window/taskbar icon resource

## Notes

This is a development build toward v1.0. Features described as remaining or intended are not represented as complete merely by being documented here. Future releases should update this README rather than creating additional versioned README files.

## v1.0 Workflow UI update

This revision adds the requested identification and workflow UI improvements:

- **File Name** is shown separately from editable **Source Name**. The actual source path remains authoritative for encoding.
- Identification cleans common DVD/Blu-ray filename noise such as `S1`, `D1`, `S01E02`, and title markers such as `A_t01`, `BX_t02`, `IX_t03`, etc.
- The main queue now has one **Identify** button. Automatic identification opens first, while the same Identify window provides search and an integrated **Manual TV Entry** section.
- **Episode preview** displays the TMDB episode still when available.
- **Chapter Start / End** can be assigned per source. HandBrake receives `--chapters start-end` when both values are supplied.
- The Import Disc window includes an **Eject** button for the selected optical drive.
- Forging progress is **orange**, with an animated spinning disc and moving hammer.
- **Dark mode / light mode** is persisted in Settings and can also be toggled from the main window.

> Build note: the source package is statically validated here (XAML parsing and archive integrity). The assistant environment does not have the .NET SDK installed, so a Windows `dotnet publish` was not performed in this environment.
