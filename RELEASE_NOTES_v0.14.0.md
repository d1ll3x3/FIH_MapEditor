# FIH Custom Map Editor v0.14.0

## What's New

### Catalog Performance
- **Background Catalog Scan**: Replaced the one-frame catalog sweep with one spread over frames, removing the freeze when the editor (F6) opens for the first time, when the CATALOG tab opens and during the warm-up rescans.
- **Faster Scans**: Replaced per-object reflection with native type lookups and three full object listings with one, and each catalog entry is now checked for pads and cannons only once.
- **Cached Catalog First**: Displayed the catalog from previous sessions while the level is still being scanned.

### Thumbnails
- **Smooth Scrolling**: Stopped thumbnail rendering while the CATALOG list is scrolled and for cards scrolled out of view.
- **No GPU Stall**: Moved the read of each new thumbnail from the GPU two frames after its render, so it no longer waits for the GPU.
- **Spread Disk Loads**: Limited cached thumbnails loaded from disk to two per frame when the scrollbar jumps across pages.
- **Disk Index**: Replaced per-card file checks, repeated several times per frame, with a list of cached thumbnails read once.
- **Background Caching**: Slowed the caching of off-screen thumbnails to one per second, with on-screen cards first.

### Low Performance Mode
- **Low Performance Toggle**: Added a "Low performance" switch in KEYS that disables new thumbnail rendering, automatic catalog rescans and the community catalog download; cached and bundled thumbnails still show.
- **Persistent Setting**: Kept the switch through Reset Defaults and config migrations.

### Architecture & Core Refactor
- `ObjectCatalog.Scan()` is now a resumable routine stepped by `UpdateScan()` on a 4 ms frame budget (`BeginScan()` for background scans); `Scan()` still completes synchronously for map loads and the manual Rescan. Categories and click-picking data are swapped in only when a scan finishes.
- New `[PERF]` log lines report the work time of each scan phase and every thumbnail render slower than 100 ms.

### Known Issues
- **Scan Spikes**: Each scan still has short spikes from Unity's global object searches (all colliders, renderers and loaded objects), which cannot be split across frames.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
