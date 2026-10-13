# FIH Custom Map Editor v0.13.0

## What's New

### Full Game Support
- **Release Build**: Rebuilt against the full Steam release of Flipping Is Hard (v2.4.005) instead of the demo/playtests.
- **Network Interactables**: Every multiplayer interactable of the game can be placed and is spawned by the host through the game's own spawners, so FishNet replicates it to every player: boost pads, jelly pads (CandyLand), cannons, touch-breaking platforms, glass and high-force glass.
- **Per-Object Settings**: Breaking platforms (delay, broken time), glass (reset time) and high-force glass (impulse, reset time) have their own settings, saved with the map.
- **Gameplay Objects**: Ziplines, conveyor belts, swing bars, swing hooks, fans, suck pipes, hammers, spinners, moving platforms, magnets, rings, auto-breaking platforms and jump resets work when placed, using the game's own behaviour.
- **Native Pads & Jellies**: Scene boost pads and jelly pads use the game's pad (sound, animation seen by other players) and their force/angle can be edited live.

### Editing Gameplay Objects
- **Frozen While Editing**: Placed mechanics hold still at their placed pose in the editor and start running in Play mode or with the editor closed, so moving, rotating and duplicating them works.
- **Own Routes**: Copies of cars, spline platforms, ziplines and pipes follow their own copy of the route instead of racing back to the level's original.
- **Swing Hooks**: Hooks hang from their new position after being moved.
- **Whole Objects**: Picking a visual piece of a gameplay object (a bar's mesh, a fan's blades) selects and copies the whole object.
- **Combined Levels**: Objects from merged-mesh levels (the Fridge, The Grid...) can be selected, duplicated and are visible when placed.
- **Player Release**: Entering Play, toggling the editor or restarting a run releases the player from ziplines, swing bars, hooks and pipes, like the game does before a teleport.
- **Clean Copies**: Placed copies no longer bring level story/fog/weather/progress triggers along, jump resets stay collision-free and oversized invisible collision boxes are no longer added.

### Pads & Cannons Panel
- **Redesigned Panel**: Pick Boost Pad, Jelly Pad or Cannon and shape the launch with sliders.
- **Guided Auto-Aim**: 1. stand at the launch spot, 2. stand at the landing spot, 3. create — no STAMP needed.
- **Selected Object Sliders**: The selected mechanic's settings are sliders applied when released.
- **Visible States**: Sliders show their fill and active toggles/tabs are clearly highlighted across the whole editor.

### Co-Edit
- **Host-Owned Network Objects**: Clients see and edit network objects placed by the host and can place their own (spawned by the host).
- **Fixed Phantom Deletes**: A client no longer makes the host delete pads and cannons.
- **Visual Sync**: Every copy gets its own stable sync id, so pad animations and zipline effects reach the right object for every player.

### Catalog
- **Complete Cloud Catalog**: The community catalog download no longer stops at 1000 entries.
- **All Ziplines Listed**: Differently shaped copies of one gameplay object are separate entries, including objects in disabled level sections.
- **Cleaner List**: Analytics volumes and loose pieces of gameplay objects are no longer listed.

### Fixes
- **STAMP**: STAMP places a single copy and then turns off.
- **Cannon Catalog Error**: Fixed "Cannon is not in the catalog" when creating cannons.

### Architecture
- Map format bumped to v13 (breaking platform and glass settings); older maps load unchanged.
- New `NetworkMechanics` table for the game's spawner-backed interactables and `PlayerAttachments` for releasing the player.
- `Unity.Splines` and `Unity.Mathematics` interop added to `setup-libs.ps1`; `build.bat` deploys to the full game.

### Known Issues
- **Addressables Catalog**: The game's binary Addressables catalog is not read yet; the catalog lists objects from the loaded level.
- **Help Beam**: The placeable network help beam may do nothing on its own.
- **Unmodded Players**: Players without the mod only see network objects at the position they were spawned.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
