# FIH Custom Map Editor v0.15.0

## What's New

### Multiplayer Fixes
- **Cannons for Clients**: Fixed placed cannons not launching joined players and leaving them without collisions; the host now hands each placed network object its settings before spawning it, as the game does, so pads, cannons, breaking platforms and glass get them on every client.
- **Client Teleports**: Fixed spawn, checkpoint and "TP Player Onto It" teleports being undone on joined players; they now tell the network they are a jump, like the game's own teleport.
- **Collisions After the Editor**: Fixed the player losing collisions when the editor (F6) was opened during a cannon launch and closed afterwards.
- **Scene Change With the Editor Open**: Fixed fly mode staying on after changing level or joining a lobby with the editor open, which could leave the player without collisions.
- **Large Maps in Co-Edit**: Fixed big maps not reaching the other players; sync batches larger than one packet are now split instead of dropped.
- **Stale Network Objects**: Fixed clients adopting an unrelated network object after a level change.

### Teleport to Players
- **Lobby Teleport**: Added a PLAYERS list in Multiplayer with one button per player in the session to teleport next to them (editor only, never during a Play run).

### Interface Redesign
- **Sidebar Navigation**: Replaced the row of seven tabs with a sidebar of sections grouped by purpose: Catalog, Pads & Cannons, Selection, Objects, Course, Level, Save & Load, Multiplayer, Times and Settings.
- **Split Tools Tab**: Moved the old TOOLS content into Course (spawn, goal, checkpoints, voice lines, soccer), Level (level edits, map base), Save & Load (Play and wipe actions) and Multiplayer.
- **Section Layout**: Added a title and one-line description to every section and labelled blocks inside Selection, Course, Level and Multiplayer.
- **New Look**: Replaced the default boxes with a dark theme of rounded panels, accent-coloured toggles and hover and press states on every button, slider, field and scrollbar.
- **Matching Windows**: Applied the same frame and panels to the Maps Hub (F7), the leaderboard and the in-game HUD.
- **Bigger Catalog Page**: Increased the catalog grid to three rows now that Pads & Cannons has its own section.

### Architecture & Core Refactor
- New `GuiTheme` (palette, nine-sliced rounded panels, text styles); the editor no longer changes the shared `GUI.skin`, so other mods' windows keep their look.
- `GuiWindowHelper.ContentOffset` lets a section draw inside a `GUI.BeginGroup` while the manual hit tests stay aligned.
- `FlyController.Exit` restores the game's own rule (collisions on, kinematic only while `PlayerNetworked.IsComposedFrozen`) instead of the state observed when fly started.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Extract the zip into BepInEx/plugins/FIHMapEditor/ (or copy FIHMapEditor.dll there).
3. Press F6 inside any level to open the Map Editor or F7 for the Maps Hub.
