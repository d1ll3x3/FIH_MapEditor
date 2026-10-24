# FIH Custom Map Editor v0.15.1

## What's New

### Multiplayer Fixes
- **Pads and Cannons for Clients**: Fixed placed pads, cannons, breaking platforms and glass reaching joined players without their settings, which left cannons unaimed, unable to launch and switching the player's collisions off; the host now sends the settings after spawning, and clients fill in any that are missing from the co-edit data.

### Gameplay Fixes
- **Infinite Jump**: Fixed endless jump resets after the object under the player was deleted, undone, removed by another editor or respawned by the host; stale ground contacts are now cleared a few times per second instead of only after a map change.
- **Fans**: Fixed fans being treated as cannons, which showed the "[E] Launch cannon" prompt and stopped some of them from working.

### Performance
- **Pending Objects Stutter**: Fixed regular freezes while a map object could not be found yet (for example one placed by another editor from their own catalog); finding a game-file object no longer walks every loaded object.
- **Game Files Thumbnails**: Removed the same full object walk from thumbnail rendering of GameFiles entries.

### Removed
- **Legacy Pad & Cannon Simulation**: Removed the old locally simulated pads and cannons (hold timer, launch and landing circles, "Launch cannon" key); maps that still contain them load those objects as plain scenery. The game's own network pads and cannons from Pads & Cannons are unchanged.

### Architecture & Core Refactor
- `MechanicsController` and `MechanicType` deleted; `ComputeColliderBounds` moved to `ObjectCatalog`.
- `NetworkMechanics.PushReplicatedData` (host, `SyncVar.DirtyAll` after spawn) and `ApplyDataLocally` (client, `SyncVar.SetInitialValues` from the placement): FishNet leaves out of the spawn message any SyncVar set before the object's network initialisation.
- Prefab roots are indexed by name during the catalog's prefab pass.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
