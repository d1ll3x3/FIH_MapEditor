# FIH Custom Map Editor v0.15.2

## What's New

### Blank Base Mode
- **Other Players Hidden**: Fixed hiding the base map also hiding the other players' bodies and switching off their collisions; every player in the session is now left out of the blank, not only the local one.

### Editor Rendering Performance
- **Pad & Cannon Arc Previews**: Fixed the editor resampling every placed pad and cannon trajectory each frame (up to 100 raycasts per object); the arc is now recomputed only when the mechanic moves or its force, angle or landing target change, plus once a second.
- **Marker Lines**: Reduced the per-frame cost of goal, spawn, checkpoint, reset, voice, soccer and X-ray markers by sending points in a single call and skipping writes that did not change.
- **Selection Highlight**: Stopped recomputing the bounds of the selected objects every frame; they refresh when an object moves or every half second, which removes the cost on large level pieces and multi-selections.
- **3D Scoreboard**: Limited scoreboard redraws to score changes and gizmo moves.

### HUD & Menu
- **Play and Idle HUD**: Reduced the HUD to one draw per frame in Play mode and with the editor closed, instead of one per IMGUI event.
- **LIST Tab**: Built the filtered object list once per frame instead of on every IMGUI pass.

### Scene Lookups & Co-edit
- **Lobby Detection**: Replaced the scan of every MonoBehaviour in the scene with a direct `EHS.Steam.SteamManager` lookup, removing a periodic hitch while no lobby manager was found.
- **Player Lookup**: Replaced the same full scan with a `PlayerNetworked` type search while the level is loading.
- **Fly Cheat Guard**: Fixed builds without the game's fly cheat searching the whole scene on every editor frame.
- **Solo Editing**: Stopped serializing the whole map after each edit when no modded player is connected; a player who joins later still receives every change.

### Low Performance Mode
- **Catalog Scan Budget**: Lowered the background catalog scan to 1.5 ms per frame when Low performance mode is on, so it takes longer but stutters less.

### Architecture & Core Refactor
- `LineBox` caches its material, color property id, width and colors, and uploads positions with `SetPositions` through a reused `Il2CppStructArray<Vector3>`.
- New `VisualBoundsCache` keyed on the target's position, rotation and scale.
- `EditorConfig` reuses static `JsonSerializerOptions` for load and save.
- `PlacedObjectManager.SetMechanicsRunning` checks the frozen state before the native `Root` null check.
- Soccer kick sampling heights moved to a static array.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
