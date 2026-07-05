# FIH Custom Map Editor

Custom map editor mod for **Flipping Is Hard Demo** (BepInEx 6 IL2CPP). Fly around the
level, clone the game's own assets (including prefabs loaded from the game files) to
build your own courses, save them as shareable files and race them with a custom spawn,
goal zone and timer.

## Installation

1. Install [BepInEx 6 (IL2CPP)](https://github.com/BepInEx/BepInEx) into the game and run it once.
2. Download `FIHMapEditor.dll` from the [latest release](https://github.com/d1ll3x3/FIH_MapEditor/releases)
   and copy it to `BepInEx/plugins/FIHMapEditor/`.
3. Maps are stored in `BepInEx/plugins/FIHMapEditor/Maps/*.fihmap.json`.

## Controls

All main keys are rebindable from the **KEYS** tab of the menu.

| Key (default) | Action |
|---|---|
| `F6` | Open / close the editor (inside a level) |
| `C` | Toggle between fly camera and free cursor (menu / selection) |
| `F7` | Maps Hub (load / delete / new map) |
| `P` | Toggle Editor ↔ Play mode |
| `R` (in Play) | Restart the run |
| `WASD` + `Space`/`Ctrl` (+`Shift` boost) | Fly |
| Left click (free cursor) | Select object / place with Stamp |
| Arrows / `PgUp` `PgDn` | Move the selected object |
| `[` `]` | Rotate Y by the degrees set in the SELECT tab |
| `+` `-` | Scale by the value set in the SELECT tab |
| `Ctrl+D` / `Del` | Duplicate / delete |

The menu header has two buttons: **Mouse placement ON/OFF** (master switch for Stamp
mode, so menu clicks never place objects) and **Back to camera** (locks the cursor
again to move the camera).

## Modes

- **Editor**: fly freely, scan the scene and the game files into the object catalog
  (CATALOG tab → Rescan), place objects with PLACE/STAMP and edit them with the menu
  or the keyboard. The SELECT tab has numeric boxes for exact rotation degrees and
  scale values.
- **Play**: if the map has a spawn you appear there; the timer starts on your first
  movement and stops when you enter the goal zone. Best time per map is saved locally.

## Catalog sources

- **Scene objects**: everything visible in the level, with or without colliders
  (collider-less decoration is listed under the *Decor* category and picked by
  bounding box when clicked).
- **Game files**: prefab assets Unity has loaded from the game's data files appear
  under the *GameFiles* category, even if they are not present in the current level.

## Map base

- **Overlay**: build on top of the original level.
- **Blank (empty canvas)**: hides the original level geometry (renderers and colliders
  only — nothing critical) and creates a starting platform to build from scratch.

## Sharing maps

A map is a single `.fihmap.json` file. Send it to someone, they drop it into their
`Maps` folder and load it from the Maps Hub (`F7`). Objects are re-created by cloning
the assets from their own copy of the game — the file contains no game content.

## Building from source

```
setup-libs.bat   # copies the game's interop DLLs into lib\ (not distributed)
build.bat        # builds and deploys to the game
```

Requires the .NET 6 SDK. `build.bat` assumes the game at
`I:\SteamLibrary\steamapps\common\Flipping is Hard Demo` — edit it if your path differs.

## License

This project is licensed under the GNU Affero General Public License v3.0 (AGPL-3.0).
See [LICENSE](LICENSE) for the full text.
