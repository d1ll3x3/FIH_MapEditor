using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UnityEngine;

// The C# compiler (LangVersion=latest) emits System.Runtime.CompilerServices.
// NullableAttribute for null-conditional expressions and lambdas; .NET 6's
// runtime doesn't ship a constructor that matches the one emitted, so the
// IL2CPP metadata generator chokes. Suppress just for this file: every
// nullable here is already checked at runtime.
#pragma warning disable CS0656

namespace FIHMapEditor
{
    // ─────────────────────────────────────────────────────────────────────────────
    // SUPABASE SCHEMA — run this once in the Supabase SQL editor to create the
    // table + RLS policy + RPCs the sync uses. Everything is idempotent / safe to
    // re-run.
    //
    //   create table if not exists public.catalog_entries (
    //       id              bigserial primary key,
    //       scene_name      text not null,
    //       entry_key       text not null,
    //       display_name    text,
    //       source_path     text,
    //       category        text,
    //       size_x          real,
    //       size_y          real,
    //       size_z          real,
    //       has_collider    boolean,
    //       game_version    text not null default '',
    //       first_seen_at   timestamptz not null default now(),
    //       last_seen_at    timestamptz not null default now(),
    //       seen_count      integer not null default 1,
    //       unique (scene_name, entry_key, game_version)
    //   );
    //   create index if not exists idx_catalog_scene_ver
    //       on public.catalog_entries (scene_name, game_version, last_seen_at desc);
    //
    //   -- Pull RPC: returns all entries for a scene that match a set of game
    //   -- versions (so a v1.0 user gets v1.0 entries + any cross-version "wildcard"
    //   -- rows with game_version = ''). Filters out the [FIH] user-placed marker
    //   -- so we never share user-specific objects.
    //   create or replace function public.fetch_catalog(p_scene text, p_versions text[])
    //   returns setof public.catalog_entries
    //   language sql security definer as $$
    //       select * from public.catalog_entries
    //       where scene_name = p_scene
    //         and game_version = any(p_versions)
    //         and source_path not like '%[FIH]%';
    //   $$;
    //
    //   -- Push RPC: upsert per (scene, key, version). last_seen_at + seen_count++
    //   -- always; category/size take the latest non-null value (last-writer-wins on
    //   -- category, max on size). NOTE: must be plpgsql — the body is procedural
    //   -- (declare/loop/return), which `language sql` rejects with 42P13.
    //   create or replace function public.push_catalog(p_entries jsonb)
    //   returns integer language plpgsql security definer as $$
    //   declare
    //       n integer := 0;
    //       e jsonb;
    //   begin
    //       for e in select * from jsonb_array_elements(p_entries)
    //       loop
    //           insert into public.catalog_entries
    //               (scene_name, entry_key, display_name, source_path, category,
    //                size_x, size_y, size_z, has_collider, game_version,
    //                first_seen_at, last_seen_at, seen_count)
    //           values
    //               (e->>'scene_name', e->>'entry_key', e->>'display_name',
    //                e->>'source_path', e->>'category',
    //                (e->>'size_x')::real, (e->>'size_y')::real, (e->>'size_z')::real,
    //                (e->>'has_collider')::boolean, e->>'game_version',
    //                now(), now(), 1)
    //           on conflict (scene_name, entry_key, game_version) do update
    //               set last_seen_at = now(),
    //                   seen_count = public.catalog_entries.seen_count + 1,
    //                   display_name = coalesce(excluded.display_name, public.catalog_entries.display_name),
    //                   source_path  = coalesce(excluded.source_path,  public.catalog_entries.source_path),
    //                   category     = coalesce(excluded.category,     public.catalog_entries.category),
    //                   size_x       = greatest(excluded.size_x, coalesce(public.catalog_entries.size_x, 0)),
    //                   size_y       = greatest(excluded.size_y, coalesce(public.catalog_entries.size_y, 0)),
    //                   size_z       = greatest(excluded.size_z, coalesce(public.catalog_entries.size_z, 0)),
    //                   has_collider = public.catalog_entries.has_collider or excluded.has_collider;
    //           n := n + 1;
    //       end loop;
    //       return n;
    //   end $$;
    //
    //   -- RLS: anonymous can read (everyone needs the catalog to start the editor)
    //   -- and can insert/update (push is anonymous too, like maps). Abuse control
    //   -- is left for later; if needed we'll add a rate-limit middleware.
    //   alter table public.catalog_entries enable row level security;
    //   drop policy if exists "anon read catalog" on public.catalog_entries;
    //   create policy "anon read catalog" on public.catalog_entries
    //       for select to anon using (true);
    //   drop policy if exists "anon write catalog" on public.catalog_entries;
    //   create policy "anon write catalog" on public.catalog_entries
    //       for insert to anon with check (true);
    //   drop policy if exists "anon update catalog" on public.catalog_entries;
    //   create policy "anon update catalog" on public.catalog_entries
    //       for update to anon using (true) with check (true);
    //
    // ─────────────────────────────────────────────────────────────────────────────

    // Sync service for the ObjectCatalog. The catalog itself only writes to a local
    // file (catalog_cache_<scene>.json). This service makes those discoveries
    // communal: when YOU find a new entry, it gets pushed to Supabase; when YOU
    // open the editor on a scene for the first time, the community catalog for
    // that scene gets pulled and merged into your local cache.
    //
    // Threading: every network call runs on a background Task. The pull result is
    // handed back to the main thread via an Action<...> callback (EditorController's
    // RunOnMainThread), which is the only safe way to mutate ObjectCatalog state
    // (Unity requires main-thread access to any UnityEngine.Object).
    //
    // Stutter budget: zero. Network is silent (no spinners, no toasts). The main-
    // thread apply is just a Dictionary + List insert per row (~100 µs for 1000
    // rows), so even a worst-case 985-row pull is below the frame budget. If a
    // pull finishes while the user is editing, the GUI's ScanVersion invalidation
    // makes the new entries appear on the next render.
    public class CatalogSyncService
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        // Cross-version match list: a user on v1.2.3 also gets rows whose
        // game_version is "" (the "applies to all" wildcard). Kept short on
        // purpose — if the game ships a v2 we can add a major-version component.
        private static readonly string[] VersionMatchList = new[] { "" };   // own version injected at call time

        // Pull cache: at most one pull per scene per session. Prevents a
        // scan-storm from spamming the backend (the editor rescans a few times
        // during warm-up, see WARMUP_SCAN_DELAYS in EditorController).
        private readonly HashSet<string> _pulledThisSession = new HashSet<string>();

        // Push coalescing: if multiple Scan() calls within a few seconds all
        // discover new entries, we batch them into one POST. The
        // background-task-running flag also prevents two concurrent POSTs of the
        // same scene.
        private class PushBatch
        {
            public string Scene;
            public string GameVersion;
            public readonly List<CatalogEntry> Entries = new List<CatalogEntry>();   // lock(Entries) to touch
            public Task Running;
        }
        private readonly Dictionary<string, PushBatch> _pushBatches = new Dictionary<string, PushBatch>();
        private const float PUSH_BATCH_WINDOW = 1.5f;    // wait this long to coalesce

        public bool Configured => Supabase.Configured;

        // Main-thread dispatcher (EditorController.RunOnMainThread). REQUIRED for the
        // pull: its result mutates ObjectCatalog's Entries/_seen, which the main
        // thread iterates every frame — merging from a worker thread is a data race.
        public Action<Action> Dispatch;

        // ── Game version ──────────────────────────────────────────────────────
        // Application.version is the Unity PlayerSettings → Version value, which
        // the game sets to the human-readable build id (e.g. "0.12.0.5" for the
        // demo, "1.0.3" for release). Different versions of the same scene can
        // ship slightly different sizes/categories for the same object; storing
        // this string lets the pull pick the right variant per user.
        private static string _cachedVersion;
        public static string GameVersion
        {
            get
            {
                if (_cachedVersion == null)
                {
                    try { _cachedVersion = Application.version ?? ""; }
                    catch { _cachedVersion = ""; }
                    if (string.IsNullOrEmpty(_cachedVersion)) _cachedVersion = "unknown";
                }
                return _cachedVersion;
            }
        }

        // ── Push (on local discovery) ─────────────────────────────────────────

        // ObjectCatalog calls this after a Scan() that produced new entries. The
        // entries are queued; the actual POST happens on a background task after
        // a short batching window so a scan-storm coalesces into one request.
        // `applyOnMainThread` is the dispatcher (EditorController.RunOnMainThread);
        // the sync itself never touches Unity objects, but the caller (catalog)
        // does, so it needs to run on the right thread.
        public void OnEntriesDiscovered(string sceneName, IEnumerable<CatalogEntry> newEntries)
        {
            if (!Configured) return;
            if (string.IsNullOrEmpty(sceneName)) return;
            if (newEntries == null) return;

            // Filter out user-placed objects ([FIH] marker) — they're specific to
            // the user's session and have no value to the community catalog.
            var batch = GetOrAddBatch(sceneName);
            lock (batch.Entries)   // the push task snapshots this list from a worker thread
            {
                foreach (var e in newEntries)
                {
                    if (e == null) continue;
                    if (string.IsNullOrEmpty(e.SourcePath)) continue;
                    if (e.SourcePath.Contains("[FIH]")) continue;
                    // Asset-prefab and scene-original entries are both shareable.
                    batch.Entries.Add(e);
                }
            }
            batch.GameVersion = GameVersion;
            EnsurePushLoop(sceneName, batch);
        }

        private PushBatch GetOrAddBatch(string sceneName)
        {
            if (!_pushBatches.TryGetValue(sceneName, out var b))
            {
                b = new PushBatch { Scene = sceneName };
                _pushBatches[sceneName] = b;
            }
            return b;
        }

        private void EnsurePushLoop(string sceneName, PushBatch batch)
        {
            if (batch.Running != null && !batch.Running.IsCompleted) return;
            // Match the rest of the project's async-fire-and-forget pattern
            // (see LeaderboardService / OnlineMapService). Calling the async
            // method directly avoids the IL emit pattern that
            // Task.Run(async () => ...) triggers on the .NET 6 runtime
            // (missing NullableAttribute ctor).
            batch.Running = RunPushWindowAsync(sceneName, batch);
        }

        private async Task RunPushWindowAsync(string sceneName, PushBatch batch)
        {
            // Coalesce: wait a fixed window so other scans can pile in. No Unity API
            // past the first await — Time.* is main-thread-only and this continues
            // on a worker thread (reading it there throws under IL2CPP).
            await Task.Delay(TimeSpan.FromSeconds(PUSH_BATCH_WINDOW)).ConfigureAwait(false);

            // Snapshot + reset the queue atomically: anything added during the
            // wait will be picked up on a future push cycle, not lost.
            List<CatalogEntry> toSend;
            lock (batch.Entries)
            {
                if (batch.Entries.Count == 0) return;
                toSend = new List<CatalogEntry>(batch.Entries);
                batch.Entries.Clear();
            }

            await PushAsync(sceneName, batch.GameVersion, toSend).ConfigureAwait(false);
        }

        // Internal: send the upsert RPC. Public for tests; the normal entry point
        // is OnEntriesDiscovered above.
        public async Task<int> PushAsync(string sceneName, string gameVersion, List<CatalogEntry> entries)
        {
            if (!Configured || entries == null || entries.Count == 0) return 0;
            try
            {
                var payload = new List<Dictionary<string, object>>(entries.Count);
                foreach (var e in entries)
                {
                    payload.Add(new Dictionary<string, object>
                    {
                        ["scene_name"] = sceneName,
                        ["entry_key"] = MakeKey(e),
                        ["display_name"] = e.DisplayName ?? "",
                        ["source_path"] = e.SourcePath ?? "",
                        ["category"] = e.Category ?? "",
                        ["size_x"] = e.BoundsSize.x,
                        ["size_y"] = e.BoundsSize.y,
                        ["size_z"] = e.BoundsSize.z,
                        ["has_collider"] = e.HasCollider,
                        ["game_version"] = gameVersion ?? "",
                    });
                }
                var args = new Dictionary<string, object> { ["p_entries"] = payload };

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/push_catalog");
                Supabase.AddAuth(req);
                req.Content = new StringContent(JsonSerializer.Serialize(args, Json), Encoding.UTF8, "application/json");
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    MapEditorPlugin.Logger.LogWarning(
                        $"[CATSYNC] Push '{sceneName}' x{entries.Count} failed: HTTP {(int)resp.StatusCode} {Supabase.Trim(body)}");
                    return 0;
                }
                int n = 0;
                int.TryParse(body?.Trim().TrimEnd(';').Trim('"'), out n);
                MapEditorPlugin.Logger.LogInfo(
                    $"[CATSYNC] Pushed {entries.Count} entries for '{sceneName}' v{gameVersion} ({n} upserted).");
                return n;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATSYNC] Push error ({sceneName}): {ex.Message}");
                return 0;
            }
        }

        // Mirror of the client-side key (Name|FirstMesh) — the ObjectCatalog
        // already has this in its private Key format. Pulled out into a helper
        // here because the push payload needs the same key shape.
        public static string MakeKey(CatalogEntry e)
        {
            if (e == null) return "";
            // The catalog's own dedup key is DisplayName|meshName. We don't have
            // the mesh name on a live entry (it was set at scan time and dropped),
            // so we use DisplayName|SourcePath fragment for the cloud key. This
            // is unique enough because the catalog de-dupes by name+mesh, and
            // SourcePath is already a stable hierarchy path.
            string dn = e.DisplayName ?? "";
            string sp = e.SourcePath ?? "";
            // Take the last 64 chars of the path so the key stays a sane length
            // but still distinguishes objects that share a display name.
            string tail = sp.Length > 64 ? sp.Substring(sp.Length - 64) : sp;
            return dn + "|" + tail;
        }

        // ── Pull (on first scan of a scene) ───────────────────────────────────

        // Called by ObjectCatalog the first time it scans a scene in this session.
        // Returns immediately; the pull happens on a background task and the
        // result is fed to applyOnMainThread on the main thread.
        public void RequestPull(string sceneName, Action<List<CatalogEntry>> applyOnMainThread)
        {
            if (!Configured) return;
            if (string.IsNullOrEmpty(sceneName)) return;
            if (_pulledThisSession.Contains(sceneName)) return;
            _pulledThisSession.Add(sceneName);

            // Direct fire-and-forget — see EnsurePushLoop for why we don't use
            // Task.Run here. The method runs on whatever thread the first await
            // resumes on, which is the thread pool for any IO completion.
            _ = RunPullAsync(sceneName, applyOnMainThread);
        }

        private async Task RunPullAsync(string sceneName, Action<List<CatalogEntry>> apply)
        {
            var entries = await PullAsync(sceneName).ConfigureAwait(false);
            if (entries == null || entries.Count == 0 || apply == null) return;
            var dispatch = Dispatch;
            if (dispatch != null) dispatch(() => apply(entries));   // merge on the main thread
            else apply(entries);                                    // no dispatcher wired (tests)
        }

        public async Task<List<CatalogEntry>> PullAsync(string sceneName)
        {
            if (!Configured) return null;
            try
            {
                string ver = GameVersion;
                // Match the user's version + the wildcard "" (cross-version rows).
                var versions = new List<object> { ver, "" };

                var args = new Dictionary<string, object>
                {
                    ["p_scene"] = sceneName,
                    ["p_versions"] = versions,
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/fetch_catalog");
                Supabase.AddAuth(req);
                req.Content = new StringContent(JsonSerializer.Serialize(args, Json), Encoding.UTF8, "application/json");
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    MapEditorPlugin.Logger.LogWarning(
                        $"[CATSYNC] Pull '{sceneName}' failed: HTTP {(int)resp.StatusCode} {Supabase.Trim(body)}");
                    return null;
                }

                var raw = JsonSerializer.Deserialize<List<RawCatalogEntry>>(body, Json);
                if (raw == null || raw.Count == 0) return new List<CatalogEntry>();

                var list = new List<CatalogEntry>(raw.Count);
                foreach (var r in raw)
                {
                    if (r == null) continue;
                    if (string.IsNullOrEmpty(r.entry_key)) continue;
                    // Skip FIH-marked rows defensively (the RPC also filters, but
                    // the RLS-less environment might let some through).
                    if (!string.IsNullOrEmpty(r.source_path) && r.source_path.Contains("[FIH]")) continue;
                    list.Add(new CatalogEntry
                    {
                        DisplayName = r.display_name ?? "",
                        SourcePath = r.source_path ?? "",
                        Source = null,   // resolved lazily by GetLiveSource, same as the local cache
                        BoundsSize = new Vector3(r.size_x, r.size_y, r.size_z),
                        Category = string.IsNullOrEmpty(r.category) ? "Props" : r.category,
                        HasCollider = r.has_collider,
                    });
                }
                MapEditorPlugin.Logger.LogInfo(
                    $"[CATSYNC] Pulled {list.Count} catalog entries for '{sceneName}' (own ver + wildcard).");
                return list;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATSYNC] Pull error ({sceneName}): {ex.Message}");
                return null;
            }
        }

        // Raw row shape from the DB. Property names match the SQL columns (case-
        // insensitive parsing via JsonSerializerOptions above).
        private class RawCatalogEntry
        {
            [JsonPropertyName("scene_name")]     public string scene_name { get; set; }
            [JsonPropertyName("entry_key")]      public string entry_key { get; set; }
            [JsonPropertyName("display_name")]   public string display_name { get; set; }
            [JsonPropertyName("source_path")]    public string source_path { get; set; }
            [JsonPropertyName("category")]       public string category { get; set; }
            [JsonPropertyName("size_x")]         public float size_x { get; set; }
            [JsonPropertyName("size_y")]         public float size_y { get; set; }
            [JsonPropertyName("size_z")]         public float size_z { get; set; }
            [JsonPropertyName("has_collider")]   public bool has_collider { get; set; }
            [JsonPropertyName("game_version")]   public string game_version { get; set; }
        }

        // ── Reset ─────────────────────────────────────────────────────────────

        // Called by EditorController on scene change / game leave. Drops the
        // per-session pull cache (a new scene should pull its own) and any
        // pending push batches (those rows will be re-queued by the next Scan
        // anyway, no need to wait around).
        public void Reset()
        {
            _pulledThisSession.Clear();
            _pushBatches.Clear();
        }
    }
}
