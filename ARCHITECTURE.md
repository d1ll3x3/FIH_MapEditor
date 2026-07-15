# Architecture

The editor is split into a Unity-independent core and game-facing runtime adapters. New code
should depend inward: runtime and UI may use Core, while Core must not reference Unity,
BepInEx, Steamworks, EHS, or Supabase.

## Projects

- `FIHMapEditor.csproj` builds the BepInEx IL2CPP plugin.
- `Core/FIHMapEditor.Core.csproj` builds map state, migrations, and pure editing services.
- `Tests/FIHMapEditor.Core.Tests.csproj` tests Core without launching the game.

## Boundaries

- `MapSession` owns mutable map-facing state for the current editing session.
- `IMapRepository` owns local persistence; `FileMapRepository` is its production adapter.
- `MarkerEditingService` performs pure spawn, goal, checkpoint, reset, and soccer mutations.
- `IMultiplayerEditorContext` is the only editor surface the Steam transport may access.
- `IEditorFeature` is the supported extension point for optional runtime features.
- `MapMigrations.Normalize` is the single place for backwards-compatible data backfills.

`EditorController` remains the composition layer during migration. Do not add new persistence,
marker mutation, multiplayer transport, or feature lifecycle logic to it. Put that behavior
behind the boundaries above and expose only a forwarding command when the legacy GUI needs one.

## Adding a feature

Implement `IEditorFeature` and register it before or after editor initialization:

```csharp
MapEditorApi.RegisterFeature(new MyFeature());
```

Features receive `EditorFeatureContext`, an intentionally small service surface. Exceptions in
one extension are logged without stopping the editor update loop.

## Verification

Run `FIH: Set up game references` once on a new checkout. Then use `Ctrl+Shift+B` for the plugin
build and `FIH: Run core tests` for the Unity-independent suite.
