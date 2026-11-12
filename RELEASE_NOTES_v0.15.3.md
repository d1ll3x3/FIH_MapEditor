# FIH Custom Map Editor v0.15.3

## What's New

### Level Edits
- **Removed Geometry Coming Back**: Fixed hidden or moved level pieces being dropped from the map when their object was not found at load; the next save or autosave then wiped the edit for good, so removed floors reappeared.
- **Pending Level Edits**: Introduced a pending list for level edits whose object is not loaded yet; they stay in every save and are retried every 3 seconds in the editor, in Play and with the editor closed.
- **Respawned Objects**: Fixed edits being lost when the game destroys and recreates an edited level object; the recreated object gets the edit again.
- **All Loaded Scenes**: Extended level edit lookups from the active scene to every loaded scene.

### Co-edit
- **No Phantom Reverts**: Prevented a level edit that could not be found locally from being broadcast to the other editors as a revert.

### Architecture & Core Refactor
- `LevelEditManager` keeps unresolved `LevelEditData` in a pending list that `Snapshot` includes, mirroring the placed objects' pending list.
- `LevelEditManager.RetryPending` also moves records whose target was destroyed back to pending.
- `ObjectCatalog.ResolvePath` searches the active scene first, then every other loaded scene.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
