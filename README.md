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
| `R` (in Play) | Retry from your last collected coin (timer keeps running) |
| `Shift+R` (in Play) | Full restart: back to spawn, timer and coins reset |
| `WASD` + `E`/`Q` up/down (+`Shift` boost) | Fly (all fly keys + speeds rebindable in KEYS) |
| Left click (free cursor) | Select object / place with Stamp / drag a gizmo axis |
| `1` `2` `3` (free cursor) | Gizmo mode: Move / Rotate / Scale |
| Arrows / `PgUp` `PgDn` | Move the selected object |
| `[` `]` | Rotate Y by the degrees set in the SELECT tab |
| `+` `-` | Scale by the value set in the SELECT tab |
| `Ctrl+D` / `Del` | Duplicate / delete (level objects are hidden, revertible) |
| `Ctrl+Z` / `Ctrl+S` | Undo last edit / save the map (letters rebindable in KEYS) |

The menu header has two buttons: **Mouse placement ON/OFF** (master switch for Stamp
mode, so menu clicks never place objects) and **Back to camera** (locks the cursor
again to move the camera).

**Gamepad**: fly with the left stick + `RT`/`LT` for up/down (`B`/`Circle` = boost);
in Play mode `X`/`Square` retries from the last coin and `LB+X` / `L1+Square` does a
full restart.

**UI scale**: if the menu or HUD reads too small (or too large) on your resolution, the
**KEYS** tab has a **UI scale** `-`/`+` control (0.5×–2×, like the trainer's HUD Scale)
that resizes the whole menu and HUD together.

## Modes

- **Editor**: fly freely, scan the scene and the game files into the object catalog
  (automatic — the catalog rescans itself as the game lazily loads content; the
  CATALOG tab's Rescan button forces one), place objects with PLACE/STAMP and edit
  them with the menu or the keyboard. The SELECT tab has numeric boxes for exact
  rotation degrees and scale values, a **Rot now** row showing the selection's current
  X/Y/Z angles (type exact values and hit Set), per-axis rotate and scale buttons, and
  a Blender/Unity-style **gizmo**: drag the colored axes on the selected object to
  move it, spin the rings to rotate (**hold Shift to snap to 15° steps**), or stretch
  an axis to scale (per-axis). The gizmo always draws on top of geometry, so the
  handles never sink into the object. Switch gizmo modes with `1`/`2`/`3` or the
  SELECT tab buttons.
  Selection ignores triggers and invisible colliders by default so you always pick
  what you see — flip **Pick invisible: ON** in the SELECT tab to grab kill zones and
  invisible walls too, and **Show invisible: ON** to draw wireframes around them
  (magenta = trigger, white = solid). Objects with collision always win the click;
  visual-only meshes are picked last.
- **Editing the original level**: with **Unlock: ON** you can select the game's own
  geometry and move / rotate / scale it (gizmo or menu) or delete it (`Del` hides it —
  nothing is ever destroyed). These level edits are saved in the map file, re-applied
  on load, and revertible one by one from the LIST tab or all at once from TOOLS.
- **Goal & spawn**: click the goal box or spawn marker to select it and edit it with
  the gizmo — move the goal, stretch its size per axis in Scale mode, rotate the spawn
  to change the starting view. Goal and reset-trigger boxes can also be **rotated**
  (gizmo Rotate mode) and detection respects the rotation. Reset triggers fire the
  instant the player's body touches them. `Del` removes the selected marker.
- **Grouping**: `Ctrl+Click` several placed objects to multi-select them, then hit
  **Group** in the SELECT tab. Grouped objects move/rotate/scale/duplicate/delete
  together, and clicking any single member — in the world or in the LIST tab —
  reselects the whole group from then on. **Ungroup** breaks it apart again. Groups
  are saved in the map file and synced in co-edit like any other object property.
- **Color**: the SELECT tab has Red/Blue/Green/Yellow presets plus a hex field (e.g.
  `FF8800`) for any color, with a live preview swatch. Works on a single object or a
  whole multi-selection at once; **Clear** removes the tint.
- **Checkpoints**: coin-style rings (Add Checkpoint Here) or **box checkpoints**
  (Add Box Checkpoint Here) — goal-style oriented boxes you can move/rotate/scale with
  the gizmo that activate the instant the player's body touches them. Checkpoint
  progression is forward-only: touching an earlier checkpoint never moves your respawn
  backwards.
- **Play**: if the map has a spawn you appear there; the timer starts on your first
  movement and stops when you enter the goal zone. Best time per map is saved locally.

In the CATALOG tab, click the **★** on any row to star it as a favorite: starred
objects always sort to the top, the **★ Favs** filter shows only them, and they are
remembered across sessions.

## Catalog sources

- **Scene objects**: everything visible in the level, with or without colliders
  (collider-less decoration is listed under the *Decor* category and picked by
  bounding box when clicked).
- **Hidden scene objects**: level objects that are disabled at scan time
  (phase-specific obstacles, pooled props) appear under the *Hidden* category and
  become visible when placed.
- **Game files**: prefab assets Unity has loaded from the game's data files appear
  under the *GameFiles* category, even if they are not present in the current level.
  Only assets Unity has actually loaded can be listed — unreferenced bundles on disk
  are not visible to the mod.
- **Invisible objects**: pure collider objects (invisible walls, volumes) appear under
  the *Invisible* category, measured by their colliders. Combine with **Show
  invisible** / **Pick invisible** to see and select them after placing.

The catalog is **additive**: scans run automatically while you edit and only ever add
entries, so nothing you've seen in the list disappears. Objects in a loaded map whose
source isn't available yet are kept as *pending* (never lost from the save) and appear
automatically as soon as their source loads.

## Soccer mode

Build a football pitch with a real physics ball and scoring goals, from the **Soccer**
section of the TOOLS tab:

- **Place Ball Here** drops the ball's kickoff / centre point where you stand. Only one
  ball per map; select it and scale it with the gizmo to size the sphere. In play mode
  the mod spawns a bouncy physics ball there that the player kicks around.
- **Add Goal (Team A / Team B)** places a goal box (blue = A, red = B) — move, rotate and
  scale it with the gizmo like a reset zone. Select a goal to flip its team in the SELECT
  tab. When the ball enters a team's goal, the **other** team scores.
- The play HUD shows the scoreboard `A  n - m  B` with a GOAL! flash. The ball returns to
  centre after each goal (and if it falls out of bounds). `R` / Shift+`R` resets the match.

Note: the ball is simulated **locally** on each client (like all mod objects), so in
multiplayer each player sees their own ball — the co-edit sync shares the *layout* (ball
point, goals), not the live physics.

## Voice lines

Play your own recordings when a player walks into a zone, from the **Voice lines**
section of the TOOLS tab:

- **Add Voice Trigger Here** places a violet box — move, rotate and scale it with the
  gizmo like a reset zone. It plays only in **Play mode**, for the player who walks in,
  through the game's voice channel (the in-game voice volume applies).
- Select it and, in the SELECT tab, pick its clip with `<` / `>`, or **Browse…** for any
  file on your PC (it is copied into the `Audio` folder). **Preview** plays it; the toggle
  switches between **Once per run** and **Every time you enter**.
- Clips are `.ogg`, `.mp3`, `.wav` or `.flac`, up to **1 MB** each (about a minute of
  voice in OGG). Files dropped into `BepInEx/plugins/FIHMapEditor/Audio` also appear after
  **Rescan Audio Folder**.
- **Only the map's owner** can add, move, change or delete voice triggers — also in
  co-edit, where other players see and hear them but can't edit them.
- Publishing the map uploads its clips too; players who download it get them
  automatically (the backend setup below includes the clip store).

## Map base

- **Overlay**: build on top of the original level.
- **Wipe Level (clean space)**: hides every game asset — all renderers and colliders
  except the player, your placed objects and the UI — leaving a truly empty space to
  build from scratch. Fully revertible with the Overlay button or `Ctrl+Z`.

## Saving

`Ctrl+S` saves instantly (creates a file from the map name if you never saved).
The editor also autosaves every 30 seconds while there are unsaved changes, plus
whenever you close the editor or enter Play mode. The two most recent autosaves are
kept in the Maps Hub (`⟲ AUTOSAVE` and `⟲ AUTOSAVE (older)`).

## Co-edit (multiplayer)

If another player joins your game with the mod installed, you build together —
**automatically, nothing to enable**. Modded players in the lobby find each other on
their own; edits sync over Steam a moment after you stop editing. The HUD banner and
the TOOLS tab show the sync status, and a **Send map now** button forces an immediate
sync. Updates received while someone is in Play mode apply when they return to the
editor. The sync rides its own Steam P2P channel — it does not touch the game's own
networking.

Sync is per-object, not a whole-map snapshot: editing your own object never touches
anyone else's, so two, three or more people can build in different corners of the map
at once without stutter and without a slower edit rolling back a newer one. Each
object, checkpoint, reset trigger, level edit, spawn and goal is its own
independently-versioned unit — the newest edit to a given thing always wins, and a
stale update that arrives late is simply ignored instead of undoing someone else's work.

## Global leaderboard (TIMES tab)

Every map has a stable id, and reaching the goal in Play mode offers to upload your run
to a shared online leaderboard — a small confirmation panel appears with **Upload** /
**Skip**. If the run isn't a new personal best it warns you first (**Yes, upload** /
**Cancel**), since uploading overwrites your existing time for that map. The **TIMES**
tab shows the ranking for the current map — one row per player, fastest first, your own
row highlighted — pulled live from the backend, so you see everyone's times even if you
never played together (speedrun.com style). Only your best time per map is kept.

The player auto-repairs on every restart (fixes a broken phone), so a botched attempt
never leaves you stuck.

### Enabling / hosting the leaderboard

The leaderboard needs a small free backend that every copy of the mod points at. It
uses [Supabase](https://supabase.com) (free tier):

1. Create a Supabase project. In the SQL editor, run:

   ```sql
   create table public.times (
     map_id       text        not null,
     steam_id     int8        not null,
     player_name  text        not null,
     time_seconds float8      not null,
     updated_at   timestamptz not null default now(),
     primary key (map_id, steam_id)
   );

   alter table public.times enable row level security;

   -- Anyone may read the board.
   create policy "read times" on public.times for select using (true);

   -- Writes go only through the RPC below, which keeps the best time per player.
   create or replace function public.submit_time(
     p_map_id text, p_steam_id int8, p_name text, p_seconds float8)
   returns void language plpgsql security definer as $$
   begin
     insert into public.times (map_id, steam_id, player_name, time_seconds, updated_at)
     values (p_map_id, p_steam_id, p_name, p_seconds, now())
     on conflict (map_id, steam_id) do update
       set time_seconds = excluded.time_seconds,
           player_name  = excluded.player_name,
           updated_at   = now()
       where excluded.time_seconds < public.times.time_seconds;
   end; $$;

   grant execute on function public.submit_time to anon;
   ```

   And, for the online map library, the `maps` table plus its RPCs:

   ```sql
   create table public.maps (
     map_id          text        primary key,
     name            text        not null,
     author_steam_id int8        not null default 0,
     author_name     text        not null default 'player',
     editable        bool        not null default true,
     object_count    int         not null default 0,
     data            text        not null,       -- map JSON, gzip+base64
     owner_token     text        not null,       -- secret: update/delete your own map
     downloads       int8        not null default 0,
     created_at      timestamptz not null default now(),
     updated_at      timestamptz not null default now()
   );

   alter table public.maps enable row level security;
   revoke select on public.maps from anon;
   grant select (map_id, name, author_steam_id, author_name, editable,
                 object_count, data, downloads, created_at, updated_at)
     on public.maps to anon;
   create policy "read maps" on public.maps for select using (true);

   create or replace function public.upload_map(
     p_map_id text, p_name text, p_author_steam_id int8, p_author_name text,
     p_editable bool, p_object_count int, p_data text, p_owner_token text)
   returns text language plpgsql security definer as $$
   declare existing_token text;
   begin
     select owner_token into existing_token from public.maps where map_id = p_map_id;
     if existing_token is null then
       insert into public.maps (map_id, name, author_steam_id, author_name,
                                editable, object_count, data, owner_token)
       values (p_map_id, p_name, p_author_steam_id, p_author_name,
               p_editable, p_object_count, p_data, p_owner_token);
       return 'inserted';
     elsif existing_token = p_owner_token then
       update public.maps set
         name = p_name, author_name = p_author_name, author_steam_id = p_author_steam_id,
         editable = p_editable, object_count = p_object_count, data = p_data,
         updated_at = now()
       where map_id = p_map_id;
       return 'updated';
     else
       return 'forbidden';
     end if;
   end; $$;
   grant execute on function public.upload_map to anon;

   create or replace function public.fetch_map(p_map_id text)
   returns text language plpgsql security definer as $$
   declare d text;
   begin
     update public.maps set downloads = downloads + 1 where map_id = p_map_id
       returning data into d;
     return d;
   end; $$;
   grant execute on function public.fetch_map to anon;

   create or replace function public.delete_map(p_map_id text, p_owner_token text)
   returns text language plpgsql security definer as $$
   declare existing_token text;
   begin
     select owner_token into existing_token from public.maps where map_id = p_map_id;
     if existing_token is null then return 'not_found';
     elsif existing_token = p_owner_token then
       delete from public.maps where map_id = p_map_id; return 'deleted';
     else return 'forbidden';
     end if;
   end; $$;
   grant execute on function public.delete_map to anon;
   ```

   And, for voice lines, the clip store. Clips are addressed by the SHA-256 of their
   bytes; only a map's owner (its `owner_token`) can attach clips to it, the server
   re-hashes every upload, and the 1 MB cap is enforced in the database itself:

   ```sql
   create table public.voice_clips (
     hash       text        primary key check (hash ~ '^[0-9a-f]{64}$'),
     data       bytea       not null check (octet_length(data) <= 1048576),
     created_at timestamptz not null default now()
   );

   -- Which maps use which clip: the ownership check for uploads, and what makes a clip
   -- downloadable. Deleting a map drops its links.
   create table public.map_voice_clips (
     map_id text not null references public.maps(map_id) on delete cascade,
     hash   text not null references public.voice_clips(hash),
     primary key (map_id, hash)
   );

   -- No direct table access at all: everything goes through the RPCs below.
   alter table public.voice_clips enable row level security;
   alter table public.map_voice_clips enable row level security;
   revoke all on public.voice_clips, public.map_voice_clips from anon, authenticated;

   -- p_data = null asks "attach an already-stored clip": 'ok', or 'missing' so the
   -- client sends the bytes. Returns ok / missing / not_found / forbidden / bad_hash /
   -- bad_data / too_large.
   create or replace function public.upload_voice_clip(
     p_map_id text, p_owner_token text, p_hash text, p_data text)
   returns text language plpgsql security definer set search_path = public as $$
   declare existing_token text; v_bytes bytea;
   begin
     select owner_token into existing_token from public.maps where map_id = p_map_id;
     if existing_token is null then return 'not_found'; end if;
     if existing_token <> p_owner_token then return 'forbidden'; end if;
     if p_hash !~ '^[0-9a-f]{64}$' then return 'bad_hash'; end if;

     if p_data is null then
       if not exists (select 1 from public.voice_clips where hash = p_hash) then
         return 'missing';
       end if;
     else
       begin
         v_bytes := decode(p_data, 'base64');
       exception when others then
         return 'bad_data';
       end;
       if octet_length(v_bytes) > 1048576 then return 'too_large'; end if;
       if encode(sha256(v_bytes), 'hex') <> p_hash then return 'bad_hash'; end if;
       insert into public.voice_clips (hash, data) values (p_hash, v_bytes)
         on conflict (hash) do nothing;
     end if;

     insert into public.map_voice_clips (map_id, hash) values (p_map_id, p_hash)
       on conflict do nothing;
     return 'ok';
   end; $$;
   revoke all on function public.upload_voice_clip(text, text, text, text) from public;
   grant execute on function public.upload_voice_clip(text, text, text, text) to anon;

   -- After a re-upload, drop links to clips the map no longer uses.
   create or replace function public.prune_map_voice_clips(
     p_map_id text, p_owner_token text, p_keep text[])
   returns text language plpgsql security definer set search_path = public as $$
   declare existing_token text;
   begin
     select owner_token into existing_token from public.maps where map_id = p_map_id;
     if existing_token is null then return 'not_found'; end if;
     if existing_token <> p_owner_token then return 'forbidden'; end if;
     delete from public.map_voice_clips
       where map_id = p_map_id and not (hash = any(coalesce(p_keep, '{}')));
     return 'ok';
   end; $$;
   revoke all on function public.prune_map_voice_clips(text, text, text[]) from public;
   grant execute on function public.prune_map_voice_clips(text, text, text[]) to anon;

   -- Base64 clip, or null when no published map uses that hash.
   create or replace function public.fetch_voice_clip(p_hash text)
   returns text language sql stable security definer set search_path = public as $$
     select encode(c.data, 'base64') from public.voice_clips c
     where c.hash = p_hash
       and exists (select 1 from public.map_voice_clips m where m.hash = c.hash);
   $$;
   revoke all on function public.fetch_voice_clip(text) from public;
   grant execute on function public.fetch_voice_clip(text) to anon;
   ```

   Clips no map links to any more are never served, but stay stored; to reclaim the space:
   `delete from public.voice_clips c where not exists (select 1 from public.map_voice_clips m where m.hash = c.hash);`

2. Put your project URL and **anon** key into the mod: bake them into `Supabase.cs`
   (`BAKED_URL` / `BAKED_ANON_KEY`) before building — so every user shares one global
   board and map library — or set `SupabaseUrl` / `SupabaseAnonKey` in
   `BepInEx/config/com.flippingishard.mapeditor.json` for a personal/test backend.
   Set `SubmitTimesOnline` to `false` to browse without uploading your times.

Times are reported by the client, so they are trust-based (no video moderation like real
speedrun.com); the RPC only accepts improvements and the anon key can do nothing else.

## Online map library (Maps Hub → Online)

The Maps Hub (`F7`) has a **Local ⇄ Online** switch. In **Online** you browse maps the
whole community has uploaded to the shared backend, **download** them (one click loads
the map), and **publish** your current map with the upload row. When uploading you
choose whether others can **edit** it or only **play** it:

- **Play-only** maps load with the editor locked — you can enter Play and race them, but
  not move/delete objects or save them as your own (the HUD shows `🔒 PLAY-ONLY`). This
  is respected by the mod but, since the map data is downloaded, it's a trust-based
  restriction, not encryption.
- An **editable** map keeps its original identity when you edit it: uploading it again
  is rejected unless you are its owner, so the original is never overwritten.

Only you can update or delete your own uploads: the first time you publish a map, the
mod stores a secret owner token locally. Deleting the mod config loses it (you'd re-upload
as a new map).

Publishing also uploads the map's voice-line clips (checked against your owner token and
re-hashed by the server); downloading a map fetches any clip you don't have.

The online library uses the same Supabase backend as the leaderboard (see below) — run
the same setup once and both work.

## Sharing maps (files)

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

The `FIHMapEditor.slnx` solution includes the runtime plugin, Unity-independent core,
and test suite. See [REFACTOR_AND_ADDITIONS.md](REFACTOR_AND_ADDITIONS.md) for a tour of
the refactor and recent runtime fixes, then [ARCHITECTURE.md](ARCHITECTURE.md) for the
dependency rules to follow when adding new features.

## License

This project is licensed under the GNU Affero General Public License v3.0 (AGPL-3.0).
See [LICENSE](LICENSE) for the full text.
