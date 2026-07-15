# Refactor and Runtime Additions

This document describes the structural refactor of the FIH Custom Map Editor and the
runtime fixes added alongside it. It is intended for contributors who need to understand
where new code belongs and why several game-specific systems require special handling.

For player controls and installation, see [README.md](README.md). For the short dependency
rules, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Why the project was refactored

The original editor placed most state, persistence, multiplayer, marker editing, UI, and
Unity lifecycle behavior in `EditorController`. That made small changes risky because code
for unrelated systems shared the same class and could mutate the same collections directly.

The refactor keeps `EditorController` as the runtime composition layer while moving reusable
logic behind smaller boundaries:

```text
GUI and game callbacks
        |
        v
EditorController (composition and Unity mode lifecycle)
        |
        +-- Core: session, migrations, marker commands, feature contracts
        +-- Runtime: files, Steam multiplayer, Unity/game adapters
        +-- Managers: placement, play mode, level edits, blank canvas
```

The dependency direction is inward. `Core` must remain independent of Unity, BepInEx,
Steamworks, EHS game assemblies, and Supabase so it can be tested without launching the game.

## New project structure

### Unity-independent Core

- `Core/Maps/MapSession.cs` owns the mutable state of the current map: metadata, markers,
  soccer data, dirty state, and load report.
- `Core/Maps/MapMigrations.cs` normalizes older map files and supplies missing collections and
  stable IDs. Backward-compatibility backfills should be added here.
- `Core/Editing/MarkerEditingService.cs` performs spawn, goal, checkpoint, reset-zone, ball,
  soccer-goal, and scoreboard mutations.
- `Core/Features/` contains the supported extension API. Optional features implement
  `IEditorFeature` and are isolated so one feature exception does not stop the editor loop.

### Runtime adapters

- `Runtime/Persistence/IMapRepository.cs` defines local map persistence.
- `Runtime/Persistence/FileMapRepository.cs` implements that contract using `.fihmap.json`
  files and autosave slots.
- `Runtime/Multiplayer/IMultiplayerEditorContext.cs` limits the surface available to the
  Steam multiplayer synchronizer instead of exposing the entire controller.

### Tests and workspace support

- `Tests/FIHMapEditor.Core.Tests.csproj` runs without Unity and currently covers session
  reset behavior, marker operations, and map migration.
- `FIHMapEditor.slnx` opens the plugin, Core, and tests together in VS Code-compatible .NET
  tooling.
- `.vscode/tasks.json` provides reference setup, build, and test tasks.

## Extension points

New optional runtime behavior should implement `IEditorFeature`:

```csharp
public sealed class ExampleFeature : IEditorFeature
{
    public void Initialize(EditorFeatureContext context)
    {
        // Store only the small services exposed by the context.
    }

    public void Update()
    {
        // Called from the editor's guarded update loop.
    }

    public void ModeChanged(EditorMode oldMode, EditorMode newMode) { }
    public void MapApplied(MapFile map) { }
}

MapEditorApi.RegisterFeature(new ExampleFeature());
```

Do not add new persistence, marker mutation, multiplayer transport, or feature lifecycle logic
directly to `EditorController`. Add it to the appropriate service and expose a small forwarding
method only when the legacy GUI needs one.

## Runtime additions and fixes

### Reliable placed-object collision

Placed clones retain or rebuild their usable colliders. Collider registration is updated when
objects are created, hidden, restored, or destroyed so the game's ground registry does not keep
references to obsolete map geometry.

When a collider is destroyed while the player is standing on it, Unity may omit
`OnCollisionExit`. The game can then leave that collider in
`EHS.GroundContact.contactCacheByCollider`, permanently reporting the player as grounded and
allowing repeated jumps in mid-air. `GroundContactFix` solves this by:

1. Waiting two frames after a map swap because Unity destruction is deferred.
2. Removing only destroyed or deliberately hidden collider entries.
3. Preserving live contacts.
4. Recalculating ground state and resetting the jump state once.

Avoid replacing this with a full cache clear or a per-frame forced-grounded workaround; those
approaches interfere with the game's own movement and respawn state.

### Functional network interactables

Cannons and boost pads are not ordinary local prefabs. Stamping a networked interactable now
resolves and creates its matching network spawner, such as `NetworkSpawner_Cannon.prefab`, so
FishNet creates the functional network object. Cloning `NetworkedInteractable_Cannon.prefab`
directly only reproduces its appearance and bypasses required network initialization.

Spawner data is applied before the network object is created, allowing configurable properties
such as cannon and boost strength to reach the spawned interactable. Variant identity is kept so
the Candyland and other appearances are not replaced by the default asset.

### Blank-canvas map mode

Wipe Level now targets vanilla level content rather than indiscriminately disabling the entire
scene. It preserves systems required for a playable scene, including:

- sky, lighting, weather, and visual environment;
- player, camera, UI, bootstrap, network, and manager objects;
- objects and colliders placed by the map editor.

Vanilla renderers and colliders are hidden through reversible state tracking. Returning to
Overlay restores their original state.

### Respawn systems

There are two distinct respawn mechanisms:

- Native hazards such as LowGround use the game's `RespawnOnTouch` component. Cloned and restored
  hazards are rebound to the live `PostBootstrapGame` and `RespawnPipesZones` services so the
  native pipe-respawn sequence can run.
- **Add Reset Trigger Here** creates editor-owned oriented-box data. `PlayModeController` checks
  these volumes while Play mode is active and teleports to the active checkpoint or map spawn.

Custom reset triggers remain active after the run reaches its goal. Checkpoint progression stops
at the finish, but safety volumes continue working. When a native hazard completes its animation,
the editor hands the final destination back to the custom checkpoint/spawn system, allowing both
types to coexist.

### Read-only downloaded maps

Non-editable community maps no longer gain access to the editor menu. Loading one records the
player's current position and facing direction, applies the map, and always enters Play mode—even
when the download started while the editor was Off. The latter is important because editor-owned
reset triggers run through `PlayModeController`, while native hazards run independently.

Pressing `F6` while playing a read-only download:

1. Leaves Play mode.
2. Despawns everything loaded for the downloaded map.
3. Restores vanilla level edits and blank-canvas state.
4. Clears downloaded markers and session data.
5. Returns the player to the recorded pre-download position and yaw.

Editable maps retain the normal Play-to-Editor `F6` behavior.

### Scene and map cleanup

Leaving the game scene explicitly wipes placed runtime objects before manager caches are reset.
Whole-map swaps preserve unresolved objects in the save and retry them when their source asset
becomes available. This prevents a temporarily unavailable catalog source from silently deleting
content from a map.

## Adding future functionality

Use the following ownership guide:

| Change | Correct location |
|---|---|
| Map data shape or compatibility default | `MapData.cs` and `MapMigrations` |
| Current-map state | `MapSession` |
| Pure marker mutation | `MarkerEditingService` |
| Local file behavior | `IMapRepository` / `FileMapRepository` |
| Optional update or lifecycle behavior | `IEditorFeature` |
| Steam synchronization | Multiplayer runtime behind `IMultiplayerEditorContext` |
| Unity spawning, collision, or game-component binding | Runtime manager/adapter |
| Buttons and presentation only | `Gui/` |

When fixing a Unity lifecycle problem, prefer restoring the game's expected state transition over
continually forcing a field. In particular, network spawning, collision exits, ground contact,
and respawn bootstrapping all have ordering requirements that are easy to break with a visually
correct local clone.

## Building and verification

From PowerShell in the repository root:

```powershell
dotnet build FIHMapEditor.csproj --configuration Debug --no-restore
dotnet test Tests\FIHMapEditor.Core.Tests.csproj --configuration Debug --no-restore
```

The plugin output is:

```text
bin\Debug\net6.0\FIHMapEditor.dll
```

Deploy it to the game's BepInEx plugin directory only after the build and tests pass. A package
resolution warning for the available `Samboy063.Cpp2IL.Core` prerelease may appear in the current
local setup; it does not by itself indicate a compile failure.

