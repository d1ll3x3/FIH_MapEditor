# FIH Custom Map Editor v0.13.2

## What's New

### Level Object Editing (Unlock)
- **Leftover Combined Mesh**: Fixed moved or hidden level objects of mesh-combined levels leaving their old mesh behind, which could not be picked and had no collision.
- **Selection Box on Click**: Fixed the selection box of mesh-combined level objects only appearing after the object was moved.
- **Rotated Pieces**: Fixed the selection box of rotated mesh-combined level objects being measured as if unrotated.

### Placed Copies
- **LOD Flicker**: Fixed copies of mesh-combined level objects drawing all of their LODs at once.

### Architecture & Core Refactor
- Combined pieces are now detected from the game's `EHS.MeshCombiner` component (falling back to the `_Combined` root name) and its baked `<combiner>_LOD<n>` children, matching `MeshCombiner.GetOrCreateLODGameObject` and `DeactivateOriginalRenderers`; `MeshCombinerIgnore` branches are respected. The detection is shared by `ObjectCatalog`, selection, the highlight and `LevelEditManager`.

### Known Issues
- **Edited Level Performance**: A combiner zone with edited pieces is drawn piece by piece, which costs more draw calls until those edits are reverted.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
