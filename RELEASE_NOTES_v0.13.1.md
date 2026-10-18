# FIH Custom Map Editor v0.13.1

## What's New

### Level Object Editing (Unlock)
- **Combined Level Pieces**: Fixed moving, rotating and scaling original level objects with Unlock ON in mesh-combined levels of the full game, where the object moved but its visible mesh stayed in place.
- **Hiding Combined Pieces**: Fixed hiding original objects of mesh-combined levels, which previously left them visible.

### Architecture & Core Refactor
- `LevelEditManager` now draws a level built with the game's `EHS.MeshCombiner` from its own pieces while any of them is edited (baked `*_LOD<n>_Combined` meshes off, piece renderers and LODGroups on) and recombines it when the last edit is reverted. Pieces under "Ignore Combine" or `MeshCombinerIgnore` are left untouched.

### Known Issues
- **Edited Level Performance**: A combined level with edited pieces is drawn piece by piece, which costs more draw calls until those edits are reverted.
- **Combined Collision**: If a piece's collision lives in a combined chunk mesh, its collision stays at the original position when the piece is moved.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
