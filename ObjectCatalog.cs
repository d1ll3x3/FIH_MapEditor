using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.SceneManagement;

namespace FIHMapEditor
{
    public class CatalogEntry
    {
        public string DisplayName;
        public string SourcePath;     // stable hierarchy path (see BuildPath)
        public string Category;
        public Vector3 BoundsSize;
        public GameObject Source;     // live reference; re-resolved by path when destroyed
        public bool HasCollider;      // false = pure decoration (SM meshes, VFX...)
        // Source already inspected for a game mechanic (scans skip it until it changes).
        public GameObject MechanicCheckedSource;
    }

    // A collider-less scene object: physics raycasts can't hit it, so clicking it is
    // resolved against this cached AABB instead. Originals never move → bounds cached.
    public class DecorPickable
    {
        public GameObject Root;
        public Bounds Bounds;
    }

    // Scans the loaded level scene plus the prefab assets Unity has loaded from the
    // game files, and builds the catalog of clonable objects.
    public class ObjectCatalog
    {
        public const string CLONE_MARKER = "[FIH]";

        // SourcePath prefix for entries that come from prefab assets (game files)
        // instead of the scene hierarchy.
        public const string ASSET_PREFIX = "asset://";
        public const string ADDRESSABLE_PREFIX = "addressable://";
        public const string ASSET_CATEGORY = "GameFiles";

        private readonly Dictionary<string, IResourceLocation> _addressableLocations =
            new Dictionary<string, IResourceLocation>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> _addressableHandles =
            new Dictionary<string, AsyncOperationHandle<GameObject>>(StringComparer.OrdinalIgnoreCase);
        private float _addressableRetryAt = -1f;
        private bool _addressableDiscoveryComplete;

        public List<CatalogEntry> Entries { get; } = new List<CatalogEntry>();
        public ObjectPreviewRenderer Previews { get; }
        public List<string> Categories { get; } = new List<string>();
        public List<DecorPickable> ColliderlessRoots { get; } = new List<DecorPickable>();
        public bool HasScanned { get; private set; }
        // Bumped on every Scan()/Clear() so GUI-side caches of Entries can invalidate.
        public int ScanVersion { get; private set; }

        // CatalogSyncService hook. Set by EditorController after construction; the
        // catalog uses it to push newly discovered entries and to pull the
        // community catalog for this scene the first time it scans. Null = sync
        // disabled (test rigs, singleplayer with no Supabase config).
        public CatalogSyncService Sync;

        // First-scan-per-session latch. The Scan loop fires multiple times during
        // warm-up (EditorController schedules 3 rescan passes at 10/25/60s). We
        // only want the cloud pull once — set on the very first Scan(), reset
        // by Reset() when the user leaves the game scene.
        public bool HasScannedPreviously { get; private set; }

        // Called by the sync service from a background task when a community
        // catalog pull finishes. The result is added to the seen-dict + entries
        // list (only if the key is new — never overwrite a local entry, the
        // local one is always fresher). Then we bump ScanVersion so the GUI
        // re-renders and rebuild categories. Safe to call from any thread
        // because the only Unity access is reading SceneManager (caller does
        // the dispatch through ApplyOnMainThread).
        public void ApplyPulledEntries(List<CatalogEntry> pulled)
        {
            if (pulled == null || pulled.Count == 0) return;
            int added = 0;
            try
            {
                // Dedup the pull against the LOCAL catalog by SourcePath, NOT by the
                // cloud key. Locally scanned/cached entries are keyed in _seen by
                // Name|MeshName, but the pull can only re-derive Name|PathFragment
                // (MakeCloudKey) — the mesh name was never stored in the cloud — so the
                // two keys never match for the same object and every object the user
                // already has would be added a second time (the catalog would double on
                // the first scan of every session once the cloud table is seeded).
                // SourcePath is the object's real hierarchy identity and is stored on
                // both sides, so it dedups correctly (and collapses intra-pull dups too).
                var knownPaths = new HashSet<string>();
                foreach (var ee in Entries)
                    if (!string.IsNullOrEmpty(ee.SourcePath)) knownPaths.Add(ee.SourcePath);

                foreach (var e in pulled)
                {
                    if (e == null) continue;
                    if (IsFihNamed(e.DisplayName)) continue;
                    // Other players' older clients pushed loose pieces of gameplay objects
                    // and analytics volumes; the local scan no longer lists either.
                    if (IsGameplayObjectPiece(e.SourcePath) || IsAnalyticsPath(e.SourcePath)) continue;
                    if (!string.IsNullOrEmpty(e.SourcePath))
                    {
                        // Already have this object (or a duplicate within the pull).
                        if (!knownPaths.Add(e.SourcePath)) continue;
                    }
                    else if (_seen.ContainsKey(MakeCloudKey(e)))
                    {
                        continue;   // no path to match on — fall back to the cloud key
                    }
                    _seen[MakeCloudKey(e)] = e;
                    Entries.Add(e);
                    added++;
                }
                if (added > 0)
                {
                    // Re-derive categories so the GUI shows the new entries in
                    // the right tab. Sort again so the list stays stable.
                    Categories.Clear();
                    foreach (var ee in Entries)
                        if (!Categories.Contains(ee.Category)) Categories.Add(ee.Category);
                    Categories.Sort();
                    Entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
                    ScanVersion++;
                    MapEditorPlugin.Logger.LogInfo(
                        $"[CATALOG] Community pull merged {added} new entries (total {Entries.Count}).");
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] ApplyPulledEntries error: {ex.Message}");
            }
        }

        // Cloud key derivation: mirrors CatalogSyncService.MakeKey so the
        // seen-dict lookup is symmetric — what was pushed is what gets merged
        // back. We have to keep both in lockstep.
        public static string MakeCloudKey(CatalogEntry e)
        {
            if (e == null) return "";
            string dn = e.DisplayName ?? "";
            string sp = e.SourcePath ?? "";
            string tail = sp.Length > 64 ? sp.Substring(sp.Length - 64) : sp;
            return dn + "|" + tail;
        }

        // Scans are ADDITIVE within a scene session: the game loads assets and spawns
        // objects lazily, so later scans discover things the first one couldn't see.
        // An entry, once found, is never dropped — only Clear() (scene change) resets.
        private readonly Dictionary<string, CatalogEntry> _seen = new Dictionary<string, CatalogEntry>();

        // Audit trail of the LAST scan's scene passes: why each visited root was
        // cataloged or skipped. Written to catalog_audit.txt on manual Rescan, so
        // "object X is missing" reports stop being guesswork.
        private readonly List<string> _auditSkip = new List<string>();

        // Disk cache: entries accumulated in previous sessions, per scene. Sources are
        // left null and re-resolved lazily by path (GetLiveSource) — exactly how live
        // entries already survive their source being destroyed. This is what makes the
        // catalog start at its FULL size instead of re-discovering content every run.
        [Serializable]
        private class CachedEntry
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string Category { get; set; }
            public float[] Size { get; set; }
            public bool HasCollider { get; set; }
        }

        private static string CacheFileFor(string sceneName)
            => System.IO.Path.Combine(BepInEx.Paths.PluginPath, "FIHMapEditor",
                $"catalog_cache_{MapSerializer.SanitizeFileName(sceneName)}.json");

        private void LoadCache()
        {
            try
            {
                string file = CacheFileFor(SceneManager.GetActiveScene().name ?? "unknown");
                if (!File.Exists(file)) return;
                var cached = JsonSerializer.Deserialize<List<CachedEntry>>(File.ReadAllText(file));
                if (cached == null) return;
                int loaded = 0;
                foreach (var c in cached)
                {
                    if (string.IsNullOrEmpty(c.Key) || _seen.ContainsKey(c.Key)) continue;
                    if (IsFihNamed(c.Name)) continue;
                    // Older sessions listed loose pieces of gameplay objects and keyed
                    // their variants by name only; both are re-found by the current scan.
                    if (IsGameplayObjectPiece(c.Path) || IsAnalyticsPath(c.Path)) continue;
                    if (IsGameplayObjectName(c.Name) && !c.Key.Contains(SHAPE_KEY_MARK)) continue;
                    var entry = new CatalogEntry
                    {
                        DisplayName = c.Name,
                        SourcePath = c.Path,
                        Source = null,   // re-resolved on demand by GetLiveSource
                        BoundsSize = VecUtil.ToVector3(c.Size),
                        Category = string.IsNullOrEmpty(c.Category) ? "Props" : c.Category,
                        HasCollider = c.HasCollider,
                    };
                    _seen[c.Key] = entry;
                    Entries.Add(entry);
                    loaded++;
                }
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Cache loaded: {loaded} entries from previous sessions.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Cache load failed: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var list = new List<CachedEntry>(_seen.Count);
                foreach (var kv in _seen)
                {
                    var e = kv.Value;
                    list.Add(new CachedEntry
                    {
                        Key = kv.Key,
                        Name = e.DisplayName,
                        Path = e.SourcePath,
                        Category = e.Category,
                        Size = VecUtil.ToArray(e.BoundsSize),
                        HasCollider = e.HasCollider,
                    });
                }
                string file = CacheFileFor(SceneManager.GetActiveScene().name ?? "unknown");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllText(file, JsonSerializer.Serialize(list));
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Cache save failed: {ex.Message}");
            }
        }

        private readonly GameObjectFinder _finder;

        // Anything bigger than this on any axis is level-chunk scale, listed under "Large".
        private const float LARGE_BOUNDS = 60f;

        public ObjectCatalog(GameObjectFinder finder)
        {
            _finder = finder;
            Previews = new ObjectPreviewRenderer(this);
        }

        public void Clear()
        {
            Entries.Clear();
            Categories.Clear();
            ColliderlessRoots.Clear();
            _seen.Clear();
            _prefabRoots = null;
            _scanRoutine = null;
            _scanFinished = null;
            HasScanned = false;
            // Also reset the first-scan latch so the next scene gets its own
            // community pull (instead of inheriting "we already pulled this
            // session" from a different scene).
            HasScannedPreviously = false;
            ScanVersion++;
        }

        // Cheap change signals for "should we rescan?" checks (no full scan needed).
        public int LastScanColliderCount { get; private set; } = -1;
        public float LastScanTime { get; private set; } = -999f;

        // A full sweep visits thousands of objects in the release game's levels, which
        // froze the game for whole seconds. Background scans run a few milliseconds per
        // frame instead; Scan() still finishes synchronously for callers that need the
        // result right away (map loads, the manual Rescan).
        private IEnumerator<bool> _scanRoutine;
        private Action<int> _scanFinished;
        private const double SCAN_FRAME_BUDGET_MS = 4.0;
        public bool IsScanning => _scanRoutine != null;

        // collectAudit builds the skip-reason trail (BuildPath for every skipped root —
        // thousands of string builds). Only the manual Rescan wants that; automatic
        // scans must stay as light as possible (they already caused editor stutter).
        public void Scan(bool collectAudit = false)
        {
            // A background scan in flight is finished first: it already is a full sweep.
            if (_scanRoutine != null)
            {
                StepScan(double.MaxValue);
                if (!collectAudit) return;
            }
            _scanRoutine = ScanRoutine(collectAudit);
            StepScan(double.MaxValue);
        }

        // Starts a background scan (no-op while one runs). onFinished gets the number of
        // entries the scan added.
        public void BeginScan(Action<int> onFinished = null)
        {
            if (_scanRoutine != null) return;
            _scanFinished = onFinished;
            _scanRoutine = ScanRoutine(false);
        }

        // Called every frame by EditorController.
        public void UpdateScan()
        {
            if (_scanRoutine != null) StepScan(SCAN_FRAME_BUDGET_MS);
        }

        private void StepScan(double budgetMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed.TotalMilliseconds < budgetMs)
                {
                    if (_scanRoutine.MoveNext()) continue;
                    _scanRoutine = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                _scanRoutine = null;
                _scanFinished = null;
                MapEditorPlugin.Logger.LogError($"[CATALOG] Scan error: {ex}");
            }
        }

        // One sweep of every source. Yields once per visited object so StepScan can stop
        // on its frame budget; Categories and ColliderlessRoots are only swapped in at
        // the end, so the GUI and click-picking never see a half-built state.
        private IEnumerator<bool> ScanRoutine(bool collectAudit)
        {
            // Wall time includes the frames between slices; the work itself is in the
            // per-phase sums below.
            var phase = new System.Diagnostics.Stopwatch();

            // First scan of this scene session: seed with everything previous sessions
            // ever found, so the catalog starts complete instead of re-discovering.
            if (!HasScanned && Entries.Count == 0)
            {
                LoadCache();
                // Show the cached entries while the sweep runs in the background.
                ScanVersion++;
            }

            if (collectAudit) _auditSkip.Clear();
            LastScanTime = Time.unscaledTime;
            int entriesBefore = Entries.Count;
            var colliderless = new List<DecorPickable>();

            // Snapshot the keys we already know so we can identify the new ones
            // after the scan for the cloud-push hook. Cheap (one HashSet copy).
            // We capture only when sync is configured — otherwise this allocates
            // for nothing.
            HashSet<string> keysBeforeScan = null;
            string sceneName = null;
            if (Sync != null && Sync.Configured)
            {
                keysBeforeScan = new HashSet<string>(_seen.Keys);
                sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }

            var playerRoot = _finder.FindPlayer()?.transform?.root;
            var seen = _seen; // dedupe key → entry, persistent across rescans
            var visitedRoots = new HashSet<int>();
            var wrapperCache = new Dictionary<int, int>();     // transform id → renderable child count

            phase.Start();
            var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            LastScanColliderCount = colliders.Length;
            phase.Stop();
            foreach (var col in colliders)
            {
                yield return true;
                phase.Start();
                ScanColliderRoot(col, playerRoot, seen, visitedRoots, wrapperCache, collectAudit);
                phase.Stop();
            }
            double collidersMs = phase.Elapsed.TotalMilliseconds;

            // Renderer-only pass: pure decoration (no collider anywhere) never shows
            // up in the collider sweep and physics raycasts can't select it. Catalog
            // it here and remember each instance's AABB for bounds-based picking.
            phase.Start();
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            phase.Stop();
            foreach (var r in renderers)
            {
                yield return true;
                phase.Start();
                ScanRendererRoot(r, playerRoot, seen, visitedRoots, wrapperCache, colliderless, collectAudit);
                phase.Stop();
            }
            double renderersMs = phase.Elapsed.TotalMilliseconds - collidersMs;

            // The inactive, gameplay-object and prefab passes all need every loaded
            // GameObject; one FindObjectsOfTypeAll serves the three of them.
            var allObjects = new List<(GameObject go, bool inScene)>();
            var passes = new[]
            {
                Guarded(CollectAllGameObjects(allObjects), "Object enumeration"),
                Guarded(ScanInactiveSceneObjects(allObjects, seen, playerRoot), "Inactive-object scan"),
                Guarded(ScanGameplayObjects(allObjects, seen, playerRoot), "Gameplay-object scan"),
                Guarded(ScanAddressablesPass(seen), "Addressables scan"),
                Guarded(ScanPrefabAssets(allObjects, seen), "Prefab-asset scan"),
                Guarded(ScanMechanicsAssemblies(seen), "Mechanics-assembly scan"),
            };
            foreach (var pass in passes)
            {
                using var it = pass.GetEnumerator();
                while (true)
                {
                    phase.Start();
                    bool more = it.MoveNext();
                    phase.Stop();
                    if (!more) break;
                    yield return true;
                }
            }
            phase.Start();
            PruneFihNamedEntries();

            // The editor catalog is asset-backed once Addressables discovery has
            // completed. Scene hierarchy scanning remains above only to support
            // legacy source resolution/auditing during migration; those transient
            // entries are not offered for new placement.
            if (_addressableDiscoveryComplete)
                PruneCatalogToAddressables();
            phase.Stop();

            // Interactables (boost pads, cannons...) get their own category so they
            // are easy to find. Subtree-only check: assembly PIECES (visual mesh,
            // trigger...) must not clutter Mechanics — the full assembly entry from
            // ScanMechanicsAssemblies is the one to use. Each source is inspected once.
            // A snapshot: a cloud pull may add entries between slices.
            foreach (var e in Entries.ToArray())
            {
                if (NetworkMechanics.IsMechanicAssetName(e.DisplayName))
                {
                    e.Category = "Mechanics";
                    continue;
                }
                if (e.Source == null || e.MechanicCheckedSource == e.Source) continue;
                yield return true;
                phase.Start();
                if (e.Source != null)
                {
                    e.MechanicCheckedSource = e.Source;
                    if (HasMechanicComponent(e.Source)) e.Category = "Mechanics";
                }
                phase.Stop();
            }
            double otherMs = phase.Elapsed.TotalMilliseconds - collidersMs - renderersMs;

            phase.Start();
            Entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));

            Categories.Clear();
            foreach (var e in Entries)
                if (!Categories.Contains(e.Category)) Categories.Add(e.Category);
            Categories.Sort();
            ColliderlessRoots.Clear();
            ColliderlessRoots.AddRange(colliderless);
            ScanVersion++;

            HasScanned = true;
            var counts = new Dictionary<string, int>();
            foreach (var e in Entries)
                counts[e.Category] = counts.TryGetValue(e.Category, out int n) ? n + 1 : 1;
            var summary = new StringBuilder();
            foreach (var cat in Categories)
                summary.Append($"{cat}: {counts[cat]}  ");
            MapEditorPlugin.Logger.LogInfo(
                $"[CATALOG] Scan: {Entries.Count} unique objects (+{Entries.Count - entriesBefore} new, " +
                $"{colliders.Length} colliders examined). {summary}");

            // Persist growth AND source upgrades (an upgraded path must replace the
            // hidden-variant path stored by an earlier session).
            if (Entries.Count != entriesBefore || _sourcesUpgraded)
            {
                _sourcesUpgraded = false;
                SaveCache();
            }

            // Cloud sync hooks: only run when the service is configured AND
            // we're in a scene worth cataloguing (filters out the loading
            // screen scene where scans also fire as a side effect of
            // editor-open warm-up).
            if (Sync != null && Sync.Configured && keysBeforeScan != null
                && !string.IsNullOrEmpty(sceneName)
                && sceneName.StartsWith("Scene_Game", StringComparison.Ordinal))
            {
                // 1) Push: any keys we didn't have before this scan are new.
                //    Cheap because the seen-dict already has the full
                //    post-scan state and we just diff against the snapshot.
                int newCount = 0;
                foreach (var key in _seen.Keys)
                {
                    if (!keysBeforeScan.Contains(key)) newCount++;
                }
                if (newCount > 0)
                {
                    var newEntries = new List<CatalogEntry>(newCount);
                    foreach (var key in _seen.Keys)
                    {
                        if (keysBeforeScan.Contains(key)) continue;
                        if (_seen.TryGetValue(key, out var e) && e != null)
                            newEntries.Add(e);
                    }
                    Sync.OnEntriesDiscovered(sceneName, newEntries);
                    MapEditorPlugin.Logger.LogInfo(
                        $"[CATALOG] {newEntries.Count} new entries queued for cloud push (scene '{sceneName}').");
                }

                // 2) Pull: only the first time we see this scene in this
                //    session. The service tracks "pulled this session" so
                //    warm-up rescans don't spam the backend. Schedule via
                //    the applyOnMainThread callback so the merge lands on
                //    the right thread. Low performance mode skips the
                //    community catalog: it doubles the list and its previews.
                if (!HasScannedPreviously && !EditorConfig.Settings.LowPerformanceMode)
                {
                    HasScannedPreviously = true;
                    Sync.RequestPull(sceneName, ApplyPulledEntries);
                }
            }
            phase.Stop();

            // The first thing to read in a "the editor stutters" log: how much work each
            // phase was, independent of how many frames it was spread over.
            MapEditorPlugin.Logger.LogInfo(
                $"[PERF] Catalog scan work: colliders {collidersMs:0}ms ({colliders.Length}), " +
                $"renderers {renderersMs:0}ms ({renderers.Length}), other passes {otherMs:0}ms " +
                $"({allObjects.Count} loaded objects), total {phase.Elapsed.TotalMilliseconds:0}ms.");

            var finished = _scanFinished;
            _scanFinished = null;
            finished?.Invoke(Entries.Count - entriesBefore);
        }

        private void ScanColliderRoot(Collider col, Transform playerRoot, Dictionary<string, CatalogEntry> seen,
            HashSet<int> visitedRoots, Dictionary<int, int> wrapperCache, bool collectAudit)
        {
            if (col == null) return;
            var t = col.transform;
            if (t == null) return;

            var root = FindCandidateRoot(t, wrapperCache);
            if (root == null) return;
            if (!visitedRoots.Add(root.GetInstanceID())) return;
            if (!IsValidCandidate(root, playerRoot, out string why))
            {
                if (collectAudit) _auditSkip.Add($"{why,-24} {BuildPath(root)}");
                return;
            }

            // Nothing gets skipped for being invisible anymore: an object whose
            // renderers are disabled still measures via its meshes, and a pure
            // collider object (invisible wall, volume) measures via its
            // colliders and lands in the "Invisible" category.
            bool invisible = false;
            var bounds = ComputeBounds(root.gameObject);
            if (bounds.size == Vector3.zero)
                bounds = ComputeAssetBounds(root.gameObject);
            if (bounds.size == Vector3.zero)
            {
                bounds = ObjectCatalog.ComputeColliderBounds(root.gameObject);
                if (bounds.size == Vector3.zero)
                {
                    if (collectAudit) _auditSkip.Add($"{"zero-bounds",-24} {BuildPath(root)}");
                    return; // truly nothing measurable
                }
                invisible = true;
            }

            string display = CleanName(root.name);
            string meshName = FirstMeshName(root.gameObject);
            string key = EntryKey(display, meshName, root.gameObject);

            if (seen.TryGetValue(key, out var dup))
            {
                UpgradeSourceIfBetter(dup, root.gameObject);
                if (collectAudit) _auditSkip.Add($"{$"dedup -> \"{dup.DisplayName}\"",-24} {BuildPath(root)}");
                return;
            }

            var entry = new CatalogEntry
            {
                DisplayName = display,
                SourcePath = BuildPath(root),
                Source = root.gameObject,
                BoundsSize = bounds.size,
                Category = invisible ? "Invisible" : Categorize(display, bounds.size),
                HasCollider = true,
            };
            seen[key] = entry;
            Entries.Add(entry);
        }

        private void ScanRendererRoot(Renderer r, Transform playerRoot, Dictionary<string, CatalogEntry> seen,
            HashSet<int> visitedRoots, Dictionary<int, int> wrapperCache, List<DecorPickable> colliderless,
            bool collectAudit)
        {
            if (r == null) return;
            var t = r.transform;
            if (t == null) return;

            var root = FindCandidateRoot(t, wrapperCache);
            if (root == null) return;
            if (!visitedRoots.Add(root.GetInstanceID())) return;
            if (!IsValidCandidate(root, playerRoot, out string why))
            {
                if (collectAudit) _auditSkip.Add($"{why,-24} {BuildPath(root)}");
                return;
            }

            var bounds = ComputeBounds(root.gameObject);
            if (bounds.size == Vector3.zero)
            {
                if (collectAudit) _auditSkip.Add($"{"zero-bounds",-24} {BuildPath(root)}");
                return;
            }

            bool hasCollider = root.GetComponentInChildren<Collider>(true) != null;
            if (!hasCollider)
                colliderless.Add(new DecorPickable { Root = root.gameObject, Bounds = bounds });

            string display = CleanName(root.name);
            string key = EntryKey(display, FirstMeshName(root.gameObject), root.gameObject);
            if (seen.TryGetValue(key, out var dup))
            {
                UpgradeSourceIfBetter(dup, root.gameObject);
                if (collectAudit) _auditSkip.Add($"{$"dedup -> \"{dup.DisplayName}\"",-24} {BuildPath(root)}");
                return;
            }

            seen[key] = new CatalogEntry
            {
                DisplayName = display,
                SourcePath = BuildPath(root),
                Source = root.gameObject,
                BoundsSize = bounds.size,
                Category = hasCollider ? Categorize(display, bounds.size) : "Decor",
                HasCollider = hasCollider,
            };
            Entries.Add(seen[key]);
        }

        // Runs one pass and stops it — not the whole scan — on an exception, the way
        // each pass used to catch its own errors.
        private static IEnumerable<bool> Guarded(IEnumerable<bool> pass, string name)
        {
            using var it = pass.GetEnumerator();
            while (true)
            {
                bool more;
                try { more = it.MoveNext(); }
                catch (Exception ex)
                {
                    MapEditorPlugin.Logger.LogWarning($"[CATALOG] {name} failed: {ex.Message}");
                    yield break;
                }
                if (!more) yield break;
                yield return true;
            }
        }

        private static IEnumerable<bool> CollectAllGameObjects(List<(GameObject go, bool inScene)> into)
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            foreach (var obj in all)
            {
                yield return true;
                var go = obj?.TryCast<GameObject>();
                if (go == null) continue;
                into.Add((go, go.scene.IsValid()));
            }
        }

        private IEnumerable<bool> ScanAddressablesPass(Dictionary<string, CatalogEntry> seen)
        {
            ScanAddressableAssets(seen);
            yield break;
        }

        // The game's interactables, by type: a native lookup instead of reflecting the
        // name of every MonoBehaviour in the subtree.
        private static bool HasMechanicComponent(GameObject go)
            => go.GetComponentInChildren<EHS.Interactables.BoostPad.NetworkedInteractableBoostPad>(true) != null
            || go.GetComponentInChildren<EHS.Interactables.Cannon.NetworkedInteractableCannon>(true) != null
            || go.GetComponentInChildren<EHS.InteractableCannonVisuals>(true) != null;

        // Third scan source: scene objects that were DISABLED at scan time (phase-specific
        // obstacles, pooled props, despawned pickups). FindObjectsOfType never returns
        // them, so without this pass they simply look "missing" from the catalog.
        // Cloning re-activates the clone root, so they place as visible objects.
        private IEnumerable<bool> ScanInactiveSceneObjects(List<(GameObject go, bool inScene)> all,
            Dictionary<string, CatalogEntry> seen, Transform playerRoot)
        {
            int added = 0;
            foreach (var (go, inScene) in all)
            {
                if (!inScene) continue;                          // assets: separate pass
                yield return true;
                if (go == null || go.activeInHierarchy) continue; // active: already scanned
                var t = go.transform;
                // Only the topmost node of each disabled subtree.
                if (t.parent != null && !t.parent.gameObject.activeInHierarchy) continue;
                if (t.root.name == "FIH_SpawnRoot" || t.root.name == "FIH_MapObjectsRoot") continue;
                if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                if (!IsValidCandidate(t, playerRoot, requireActive: false)) continue;
                if (go.GetComponentInChildren<Renderer>(true) == null) continue;

                // Renderer.bounds is meaningless while disabled; measure the meshes.
                var bounds = ComputeAssetBounds(go);
                if (bounds.size == Vector3.zero) continue;

                string display = CleanName(go.name);
                string key = EntryKey(display, FirstMeshName(go), go);
                if (seen.ContainsKey(key)) continue;             // active twin already listed

                var entry = new CatalogEntry
                {
                    DisplayName = display,
                    SourcePath = BuildPath(t),
                    Source = go,
                    BoundsSize = bounds.size,
                    Category = "Hidden",
                    HasCollider = go.GetComponentInChildren<Collider>(true) != null,
                };
                seen[key] = entry;
                Entries.Add(entry);
                added++;
            }
            if (EditorConfig.VerboseLogs)
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Inactive-object scan added {added} hidden entries.");
        }

        // The game's gameplay objects (SceneObject_*: ziplines, swing bars, fans, movers...)
        // share one name and mesh per kind, but each instance can have a different shape
        // (a zipline's length, a route's course). Their key carries the measured size so
        // differently shaped ones stay separate entries.
        private const string SHAPE_KEY_MARK = "|~";

        private static bool IsGameplayObjectName(string name)
            => name != null && name.StartsWith("SceneObject_", StringComparison.OrdinalIgnoreCase);

        // A path that goes THROUGH a SceneObject_ node: one piece of a gameplay object.
        private static bool IsGameplayObjectPiece(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int at = path.IndexOf("/SceneObject_", StringComparison.OrdinalIgnoreCase);
            if (at < 0 && !path.StartsWith("SceneObject_", StringComparison.OrdinalIgnoreCase)) return false;
            int from = at < 0 ? 0 : at + 1;
            return path.IndexOf('/', from) >= 0;
        }

        // Invisible analytics volumes (AnalyticsAllZones/...) only measure where players
        // go; they are not placeable objects.
        private static bool IsAnalyticsPath(string path)
            => !string.IsNullOrEmpty(path) && path.StartsWith("AnalyticsAllZones", StringComparison.OrdinalIgnoreCase);

        private static string EntryKey(string display, string meshName, GameObject go)
        {
            string key = display + "|" + meshName;
            if (!IsGameplayObjectName(display)) return key;
            var size = ComputeAssetBounds(go).size;
            return $"{key}{SHAPE_KEY_MARK}{Mathf.RoundToInt(size.x)}x{Mathf.RoundToInt(size.y)}x{Mathf.RoundToInt(size.z)}";
        }

        // Every gameplay object of the level, active or not. The collider and renderer
        // passes reach them only when the click-style climb happens to land on their
        // root, and a disabled level section hides all of its objects from them.
        private IEnumerable<bool> ScanGameplayObjects(List<(GameObject go, bool inScene)> all,
            Dictionary<string, CatalogEntry> seen, Transform playerRoot)
        {
            int added = 0;
            foreach (var (go, inScene) in all)
            {
                if (!inScene) continue;
                yield return true;
                if (go == null || !IsGameplayObjectName(go.name)) continue;
                var t = go.transform;
                if (t.root.name == "FIH_SpawnRoot" || t.root.name == "FIH_MapObjectsRoot") continue;
                if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                if (!IsValidCandidate(t, playerRoot, requireActive: false)) continue;
                if (go.GetComponentInChildren<Renderer>(true) == null) continue;

                var bounds = ComputeAssetBounds(go);
                if (bounds.size == Vector3.zero) continue;
                if (bounds.size.x > 120f || bounds.size.y > 120f || bounds.size.z > 120f) continue;

                string display = CleanName(go.name);
                string key = EntryKey(display, FirstMeshName(go), go);
                if (seen.ContainsKey(key)) continue;

                var entry = new CatalogEntry
                {
                    DisplayName = display,
                    SourcePath = BuildPath(t),
                    Source = go,
                    BoundsSize = bounds.size,
                    Category = "Mechanics",
                    HasCollider = go.GetComponentInChildren<Collider>(true) != null,
                };
                seen[key] = entry;
                Entries.Add(entry);
                added++;
            }
            if (EditorConfig.VerboseLogs)
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Gameplay-object scan added {added} entries.");
        }

        // Mechanics pass: the game's interactables (boost pads, cannons) are multi-part
        // assemblies — visual mesh, interact trigger and area as SIBLINGS. The generic
        // root heuristics catalog those pieces separately, so cloning one piece gives a
        // broken half-cannon. This pass catalogs the whole assembly as one entry.
        private IEnumerable<bool> ScanMechanicsAssemblies(Dictionary<string, CatalogEntry> seen)
        {
            // Found by type (pads, cannons, cannon visuals): a native search instead of
            // reflecting the type name of every MonoBehaviour in the scene.
            var anchors = new List<Transform>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<EHS.Interactables.BoostPad.NetworkedInteractableBoostPad>())
                if (c != null) anchors.Add(c.transform);
            foreach (var c in UnityEngine.Object.FindObjectsOfType<EHS.Interactables.Cannon.NetworkedInteractableCannon>())
                if (c != null) anchors.Add(c.transform);
            foreach (var c in UnityEngine.Object.FindObjectsOfType<EHS.InteractableCannonVisuals>())
                if (c != null) anchors.Add(c.transform);

            int added = 0;
            foreach (var anchor in anchors)
            {
                yield return true;
                if (anchor == null) continue;

                // Assembly root: climb while the parent is still cannon-sized. Level
                // chunk containers fail the bounds guard and stop the climb.
                var root = anchor;
                while (root.parent != null)
                {
                    var parentBounds = ComputeBounds(root.parent.gameObject);
                    if (parentBounds.size == Vector3.zero) break;
                    if (parentBounds.size.x > 30f || parentBounds.size.y > 30f || parentBounds.size.z > 30f) break;
                    root = root.parent;
                }

                var bounds = ComputeBounds(root.gameObject);
                if (bounds.size == Vector3.zero) continue;

                string display = CleanName(root.name);
                string key = EntryKey(display, FirstMeshName(root.gameObject), root.gameObject);
                if (seen.ContainsKey(key)) continue;

                var entry = new CatalogEntry
                {
                    DisplayName = display,
                    SourcePath = BuildPath(root),
                    Source = root.gameObject,
                    BoundsSize = bounds.size,
                    Category = "Mechanics",
                    HasCollider = root.GetComponentInChildren<Collider>(true) != null,
                };
                seen[key] = entry;
                Entries.Add(entry);
                added++;
            }
            if (EditorConfig.VerboseLogs)
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Mechanics-assembly scan added {added} entries.");
        }

        // Second scan source: prefab assets loaded from the game files. These are not in
        // any scene — the level references them, Unity keeps them in memory, and
        // Resources.FindObjectsOfTypeAll sees them. Only prefabs Unity has actually
        // loaded appear here; unreferenced bundles on disk do not.
        private IEnumerable<bool> ScanPrefabAssets(List<(GameObject go, bool inScene)> all,
            Dictionary<string, CatalogEntry> seen)
        {
            int added = 0;
            var roots = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var (go, inScene) in all)
            {
                if (inScene) continue;                           // scene objects: already scanned
                yield return true;
                if (go == null || go.transform.parent != null) continue;   // prefab roots only
                roots.TryAdd(go.name, go);
                if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                if (go.name.Contains(CLONE_MARKER)) continue;
                if (!IsValidPrefabCandidate(go)) continue;

                var bounds = ComputeAssetBounds(go);
                if (bounds.size == Vector3.zero) continue;

                string display = CleanName(go.name);
                string key = EntryKey(display, FirstMeshName(go), go);
                if (seen.ContainsKey(key)) continue;             // same object exists in the scene

                var entry = new CatalogEntry
                {
                    DisplayName = display,
                    SourcePath = ASSET_PREFIX + go.name,
                    Source = go,
                    BoundsSize = bounds.size,
                    Category = ASSET_CATEGORY,
                    HasCollider = go.GetComponentInChildren<Collider>(true) != null,
                };
                seen[key] = entry;
                Entries.Add(entry);
                added++;
            }
            _prefabRoots = roots;
            if (EditorConfig.VerboseLogs)
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Prefab-asset scan added {added} entries from game files.");
        }

        private bool IsValidPrefabCandidate(GameObject go)
        {
            if (go.GetComponentInChildren<Renderer>(true) == null) return false;
            if (go.GetComponentInChildren<Camera>(true) != null) return false;
            if (go.GetComponentInChildren<AudioListener>(true) != null) return false;
            // The network interactables carry a world-space "press to interact" hint
            // canvas; it is part of them, not a UI root.
            if (go.GetComponentInChildren<Canvas>(true) != null
                && NetworkMechanics.KindOf(go) == NetworkMechanicKind.None) return false;
            return !CarriesPlayerOrNetworkManager(go);
        }

        // Levels built with the game's MeshCombiner (the Fridge, The Grid... and most of
        // the full game) draw their static pieces from merged meshes and switch each
        // piece's own renderer and LODGroup off. Those pieces are still real objects;
        // what stays out of the merge (MeshCombinerIgnore, "Ignore Combine" nodes) keeps
        // its renderers on.
        public static bool IsCombinedLevelPiece(Transform t) => FindCombineScope(t) != null;

        // The MeshCombiner a piece was merged by, or the "_Combined" level root when the
        // level carries no combiner component. Null for anything that draws itself.
        public static Transform FindCombineScope(Transform t)
        {
            if (t == null) return null;
            Transform scope = null;
            var combiner = t.GetComponentInParent<global::EHS.MeshCombiner.MeshCombiner>(true);
            if (combiner != null) scope = combiner.transform;
            else if (t.root.name.IndexOf("_Combined", StringComparison.OrdinalIgnoreCase) >= 0) scope = t.root;
            if (scope == null || IsIgnoredByCombiner(t, scope) || IsCombinedOutput(t, scope)) return null;
            return scope;
        }

        private static bool IsIgnoredByCombiner(Transform t, Transform scope)
        {
            for (var current = t; current != null && current != scope; current = current.parent)
            {
                string name = current.name;
                if (name.IndexOf("Ignore Combine", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("IgnoreCombine", StringComparison.OrdinalIgnoreCase) >= 0
                    || current.GetComponent<global::EHS.MeshCombiner.MeshCombinerIgnore>() != null) return true;
            }
            return false;
        }

        // The combiner bakes each LOD level into a direct child named
        // "<combiner name>_LOD<n>" (MeshCombiner.GetOrCreateLODGameObject). Only the
        // suffix is checked: a scene instance of the combiner may have been renamed.
        public static bool IsCombinedOutput(Transform t, Transform scope)
        {
            for (var current = t; current != null && current != scope; current = current.parent)
                if (current.parent == scope && HasLodSuffix(current.name)) return true;
            return false;
        }

        private static bool HasLodSuffix(string name)
        {
            int lod = name.LastIndexOf("_LOD", StringComparison.Ordinal);
            if (lod < 0 || lod + 4 == name.Length) return false;
            for (int i = lod + 4; i < name.Length; i++)
                if (!char.IsDigit(name[i])) return false;
            return true;
        }

        // What the player actually sees of an object: its enabled renderers, or for a
        // combined level piece the meshes its (disabled) renderers would draw.
        public static Bounds ComputeVisualBounds(GameObject go)
        {
            var bounds = ComputeBounds(go);
            if (bounds.size != Vector3.zero || !IsCombinedLevelPiece(go.transform)) return bounds;
            return ComputeAssetBounds(go);
        }

        // Renderer.bounds is unreliable on inactive prefab assets; approximate from the
        // shared meshes instead.
        private static Bounds ComputeAssetBounds(GameObject go)
        {
            bool has = false;
            Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
            try
            {
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    Accumulate(mf.sharedMesh.bounds, mf.transform, ref bounds, ref has);
                }
                // Skinned meshes have no MeshFilter; without this they'd measure as zero.
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    Accumulate(smr.sharedMesh.bounds, smr.transform, ref bounds, ref has);
                }
            }
            catch { }
            return has ? bounds : new Bounds(Vector3.zero, Vector3.zero);

            // All 8 corners: a rotated piece of a combined level is measured as placed.
            static void Accumulate(Bounds local, Transform t, ref Bounds bounds, ref bool has)
            {
                Vector3 c = local.center, e = local.extents;
                for (int xi = -1; xi <= 1; xi += 2)
                    for (int yi = -1; yi <= 1; yi += 2)
                        for (int zi = -1; zi <= 1; zi += 2)
                        {
                            Vector3 corner = t.TransformPoint(c + new Vector3(xi * e.x, yi * e.y, zi * e.z));
                            if (!has) { bounds = new Bounds(corner, Vector3.zero); has = true; }
                            else bounds.Encapsulate(corner);
                        }
            }
        }

        // Re-find a prefab asset by name (used when a stored map references an asset
        // entry, or when Unity unloaded and re-loaded assets between sessions).
        // Prefab roots by name, refreshed by every catalog scan (its prefab pass walks
        // them anyway). A lookup used to enumerate every loaded object — ~250k in the
        // release game — and pending map objects retried that every few seconds, which
        // stuttered the game for as long as one of them couldn't be found.
        private Dictionary<string, GameObject> _prefabRoots;

        private GameObject FindPrefabAssetByName(string name)
        {
            _prefabRoots ??= BuildPrefabRootIndex();
            return _prefabRoots.TryGetValue(name, out var go) && go != null ? go : null;
        }

        // Only before the first scan of a scene; scans keep the index fresh after that.
        private static Dictionary<string, GameObject> BuildPrefabRootIndex()
        {
            var roots = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            try
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>()))
                {
                    var go = obj?.TryCast<GameObject>();
                    if (go == null || go.scene.IsValid() || go.transform.parent != null) continue;
                    roots.TryAdd(go.name, go);
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Prefab index build failed: {ex.Message}");
            }
            return roots;
        }

        // The game keeps HIDDEN duplicates of level objects (disabled templates,
        // pooling). If the dedup happened to store one of those as an entry's Source,
        // every placement clones the hidden variant — wrong renderers (invisible) and
        // wrong physic materials (a GroundBlock that doesn't slide). Whenever a scan
        // sees an ACTIVE, VISIBLE instance of the same object, upgrade the entry to
        // point at the real thing.
        private bool _sourcesUpgraded;

        private void UpgradeSourceIfBetter(CatalogEntry entry, GameObject candidate)
        {
            try
            {
                if (candidate == null || !candidate.activeInHierarchy) return;
                if (entry.SourcePath != null && (entry.SourcePath.StartsWith(ASSET_PREFIX)
                    || entry.SourcePath.StartsWith(ADDRESSABLE_PREFIX))) return;

                bool currentBad = entry.Source == null
                    || !entry.Source.activeInHierarchy
                    || ComputeBounds(entry.Source).size == Vector3.zero;   // renderers disabled
                if (!currentBad) return;
                if (ComputeBounds(candidate).size == Vector3.zero) return; // candidate no better

                entry.Source = candidate;
                entry.SourcePath = BuildPath(candidate.transform);
                entry.HasCollider = candidate.GetComponentInChildren<Collider>(true) != null;
                _sourcesUpgraded = true;
                if (EditorConfig.VerboseLogs)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[CATALOG] '{entry.DisplayName}': source upgraded to a live visible instance ({entry.SourcePath}).");
            }
            catch { }
        }

        // Climb from a collider to the object's logical root. The climb rule lives in
        // SelectionSystem.CanClimbInto so the catalog and click-selection always agree
        // on what "one object" means (LOD sets and SM_*/collision splits included).
        private Transform FindCandidateRoot(Transform t, Dictionary<int, int> wrapperCache)
        {
            var root = t;
            while (root.parent != null)
            {
                var p = root.parent;
                if (p.GetComponent<Renderer>() != null || p.GetComponent<Collider>() != null)
                {
                    root = p;
                    continue;
                }
                if (SelectionSystem.CanClimbInto(p, wrapperCache))
                {
                    root = p;
                    continue;
                }
                break;
            }
            return SelectionSystem.PromoteToMechanicRoot(root);
        }

        private bool IsValidCandidate(Transform root, Transform playerRoot, bool requireActive = true)
            => IsValidCandidate(root, playerRoot, out _, requireActive);

        // The out reason feeds the catalog audit ("why is object X not listed?").
        private bool IsValidCandidate(Transform root, Transform playerRoot, out string reason, bool requireActive = true)
        {
            reason = null;
            if (root == null) { reason = "null-root"; return false; }
            var go = root.gameObject;
            if (requireActive && !go.activeInHierarchy) { reason = "inactive"; return false; }
            if (go.name.Contains(CLONE_MARKER)) { reason = "own-clone"; return false; }
            if (go.name == "FIH_MapObjectsRoot" || go.name == "FIH_SpawnRoot") { reason = "mod-helper"; return false; }
            if (playerRoot != null && root.root == playerRoot) { reason = "player-rig"; return false; }
            if (root.root.name.StartsWith("AnalyticsAllZones", StringComparison.OrdinalIgnoreCase))
                { reason = "analytics-volume"; return false; }

            // Skip anything that is (or carries) gameplay-critical machinery.
            if (go.GetComponentInChildren<Camera>(true) != null) { reason = "has-camera"; return false; }
            if (go.GetComponentInChildren<AudioListener>(true) != null) { reason = "has-audiolistener"; return false; }
            if (go.GetComponentInChildren<Canvas>(true) != null) { reason = "has-canvas(UI)"; return false; }

            // Skip other players / networked characters
            if (CarriesPlayerOrNetworkManager(go)) { reason = "networked-player"; return false; }

            return true;
        }

        // A native lookup by type: reflecting the name of every MonoBehaviour under every
        // candidate root was one of the slowest parts of a scan.
        private static bool CarriesPlayerOrNetworkManager(GameObject go)
            => go.GetComponentInChildren<EHS.PlayerNetworked>(true) != null
            || go.GetComponentInChildren<FishNet.Managing.NetworkManager>(true) != null;

        // Union of collider bounds (triggers included); renderer bounds as fallback.
        public static Bounds ComputeColliderBounds(GameObject go)
        {
            bool has = false;
            Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
            try
            {
                foreach (var c in go.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled) continue;
                    if (!has) { bounds = c.bounds; has = true; }
                    else bounds.Encapsulate(c.bounds);
                }
            }
            catch { }
            return has ? bounds : ComputeBounds(go);
        }

        public static Bounds ComputeBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(false);
            bool has = false;
            Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return has ? bounds : new Bounds(go.transform.position, Vector3.zero);
        }

        private static string FirstMeshName(GameObject go)
        {
            try
            {
                var mf = go.GetComponentInChildren<MeshFilter>(true);
                if (mf != null && mf.sharedMesh != null) return mf.sharedMesh.name;
            }
            catch { }
            return "";
        }

        // Strip "(1)" style clone suffixes so identical objects dedupe together.
        public static string CleanName(string name)
        {
            name = name.Trim();
            int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && name.EndsWith(")"))
            {
                string inner = name.Substring(paren + 2, name.Length - paren - 3);
                bool digits = inner.Length > 0;
                foreach (char c in inner) if (!char.IsDigit(c)) { digits = false; break; }
                if (digits) name = name.Substring(0, paren);
            }
            return name;
        }

        // Addressables entries are not loaded yet, so only their name can tell.
        private static string CategorizeAsset(string displayName)
            => NetworkMechanics.IsMechanicAssetName(displayName) ? "Mechanics" : ASSET_CATEGORY;

        private static string Categorize(string name, Vector3 size)
        {
            if (size.x > LARGE_BOUNDS || size.y > LARGE_BOUNDS || size.z > LARGE_BOUNDS)
                return "Large";
            string n = name.ToLowerInvariant();
            if (n.Contains("ramp") || n.Contains("slope")) return "Ramps";
            if (n.Contains("platform") || n.Contains("floor") || n.Contains("ground")) return "Platforms";
            if (n.Contains("wall") || n.Contains("fence") || n.Contains("barrier")) return "Walls";
            return "Props";
        }

        // ── Stable hierarchy paths ──────────────────────────────────────────

        // "Environment/Chunk_03/Ramp_Wood_L#2" — #n disambiguates same-named siblings.
        public static string BuildPath(Transform t)
        {
            var sb = new StringBuilder();
            BuildPathRecursive(t, sb);
            return sb.ToString();
        }

        private static void BuildPathRecursive(Transform t, StringBuilder sb)
        {
            if (t.parent != null)
            {
                BuildPathRecursive(t.parent, sb);
                sb.Append('/');
            }
            sb.Append(t.name);
            int index = SiblingIndexAmongSameName(t);
            if (index > 0) sb.Append('#').Append(index);
        }

        private static int SiblingIndexAmongSameName(Transform t)
        {
            var parent = t.parent;
            int index = 0;
            if (parent == null)
            {
                var scene = t.gameObject.scene;
                var roots = scene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r.transform == t) return index;
                    if (r.name == t.name) index++;
                }
                return 0;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == t) return index;
                if (child.name == t.name) index++;
            }
            return 0;
        }

        // Resolve a stored path back to a scene object. Fallback chain:
        // exact path → path ignoring #indices → catalog entry by leaf name.
        public GameObject ResolveSource(string path, string sourceName)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith(ADDRESSABLE_PREFIX))
            {
                var asset = LoadAddressable(path.Substring(ADDRESSABLE_PREFIX.Length));
                if (asset != null) return asset;
                path = null;
            }
            if (!string.IsNullOrEmpty(path) && path.StartsWith(ASSET_PREFIX))
            {
                var asset = FindPrefabAssetByName(path.Substring(ASSET_PREFIX.Length));
                if (asset != null) return asset;
                path = null; // fall through to the name-based catalog lookup
            }

            if (!string.IsNullOrEmpty(path))
            {
                var exact = ResolvePath(path, exactIndices: true);
                if (exact != null) return exact;

                var loose = ResolvePath(path, exactIndices: false);
                if (loose != null) return loose;
            }

            if (!string.IsNullOrEmpty(sourceName))
            {
                foreach (var e in Entries)
                {
                    if (e.DisplayName == CleanName(sourceName))
                    {
                        var src = GetLiveSource(e);
                        if (src != null) return src;
                    }
                }
            }
            return null;
        }

        // Preferred asset source. Enumerating locations does not load bundle contents;
        // individual prefab templates are loaded lazily when placed or previewed.
        private void ScanAddressableAssets(Dictionary<string, CatalogEntry> seen)
        {
            try
            {
                int added = 0;
                var gameObjectType = Il2CppType.Of<GameObject>();
                var locatorEnumerable = Addressables.ResourceLocators
                    .TryCast<Il2CppSystem.Collections.IEnumerable>();
                var locatorEnumerator = locatorEnumerable?.GetEnumerator();
                while (locatorEnumerator != null && locatorEnumerator.MoveNext())
                {
                    var locator = locatorEnumerator.Current?.TryCast<
                        UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator>();
                    if (locator == null) continue;
                    var map = locator.TryCast<ResourceLocationMap>();
                    // Binary catalogs (catalog.bin, the release game) have no ResourceLocationMap.
                    // Querying them through IResourceLocator.Locate crashed the game natively
                    // on the first scan, so they are skipped until a safe path is found.
                    if (map == null || map.Locations == null) continue;

                    // ResourceLocationMap.AllLocations uses SelectMany over native
                    // UnsafeLists. Its boxed IL2CPP enumerator throws a false
                    // "collection modified" error. Enumerate dictionary keys, then
                    // index each IList directly instead.
                    var keyEnumerable = map.Locations.Keys
                        .TryCast<Il2CppSystem.Collections.IEnumerable>();
                    var keyEnumerator = keyEnumerable?.GetEnumerator();
                    while (keyEnumerator != null && keyEnumerator.MoveNext())
                    {
                        var locationKey = keyEnumerator.Current;
                        if (locationKey == null || !map.Locations.TryGetValue(locationKey, out var locations)
                            || locations == null) continue;
                        var indexedLocations = locations.TryCast<Il2CppSystem.Collections.IList>();
                        var locationCollection = locations.TryCast<Il2CppSystem.Collections.ICollection>();
                        if (indexedLocations == null || locationCollection == null) continue;
                        for (int i = 0; i < locationCollection.Count; i++)
                        {
                            var location = indexedLocations[i]?.TryCast<IResourceLocation>();
                            if (AddAddressableLocation(location, gameObjectType, seen)) added++;
                        }
                    }
                }
                MapEditorPlugin.Logger.LogInfo(
                    $"[CATALOG] Addressables discovered {_addressableLocations.Count} prefab location(s), +{added} catalog entries.");
                _addressableDiscoveryComplete = _addressableLocations.Count > 0;
                _addressableRetryAt = -1f;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Addressables scan failed: {ex.Message}");
                // Addressables can still be registering locations during scene startup.
                // Retry from Update after the native collection has settled.
                _addressableRetryAt = Time.unscaledTime + 2f;
            }
        }

        private bool AddAddressableLocation(IResourceLocation location, Il2CppSystem.Type gameObjectType,
            Dictionary<string, CatalogEntry> seen)
        {
            if (location == null || location.ResourceType == null) return false;
            string typeName = location.ResourceType.FullName ?? location.ResourceType.Name;
            if (typeName != gameObjectType.FullName && typeName != "UnityEngine.GameObject") return false;

            string address = location.PrimaryKey;
            if (string.IsNullOrWhiteSpace(address) || _addressableLocations.ContainsKey(address)) return false;
            _addressableLocations[address] = location;
            string display = Path.GetFileNameWithoutExtension(address.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(display)) display = address;
            display = CleanName(display);
            if (display.IndexOf("FIH", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string key = "addressable|" + address;
            if (seen.ContainsKey(key)) return false;

            foreach (var candidate in Entries)
            {
                if (candidate.DisplayName != display || (candidate.SourcePath != null
                    && candidate.SourcePath.StartsWith(ADDRESSABLE_PREFIX))) continue;
                candidate.SourcePath = ADDRESSABLE_PREFIX + address;
                candidate.Source = null;
                candidate.Category = CategorizeAsset(display);
                seen[key] = candidate;
                _sourcesUpgraded = true;
                return false;
            }

            var entry = new CatalogEntry
            {
                DisplayName = display,
                SourcePath = ADDRESSABLE_PREFIX + address,
                BoundsSize = Vector3.one,
                Category = CategorizeAsset(display),
                HasCollider = true,
            };
            seen[key] = entry;
            Entries.Add(entry);
            return true;
        }

        public void UpdateAddressableDiscovery()
        {
            if (_addressableRetryAt < 0f || Time.unscaledTime < _addressableRetryAt) return;
            int before = Entries.Count;
            _addressableRetryAt = Time.unscaledTime + 5f;
            ScanAddressableAssets(_seen);
            if (_addressableDiscoveryComplete) PruneCatalogToAddressables();
            if (Entries.Count == before) return;

            Entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
            Categories.Clear();
            foreach (var entry in Entries)
                if (!Categories.Contains(entry.Category)) Categories.Add(entry.Category);
            Categories.Sort();
            ScanVersion++;
            SaveCache();
        }

        private void PruneCatalogToAddressables()
        {
            int removed = 0;
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                bool addressable = entry?.SourcePath != null
                    && entry.SourcePath.StartsWith(ADDRESSABLE_PREFIX);
                bool fihNamed = entry?.DisplayName != null
                    && entry.DisplayName.IndexOf("FIH", StringComparison.OrdinalIgnoreCase) >= 0;
                if (addressable && !fihNamed) continue;
                Entries.RemoveAt(i);
                removed++;
            }
            if (removed == 0) return;

            var staleKeys = new List<string>();
            foreach (var pair in _seen)
                if (pair.Value == null || !Entries.Contains(pair.Value)) staleKeys.Add(pair.Key);
            foreach (var key in staleKeys) _seen.Remove(key);
            _sourcesUpgraded = true;
            ScanVersion++;
            MapEditorPlugin.Logger.LogInfo(
                $"[CATALOG] Asset-only mode omitted {removed} scene/resident entries; " +
                $"{Entries.Count} Addressables entries remain.");
        }

        private void PruneFihNamedEntries()
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (IsFihNamed(Entries[i]?.DisplayName)) Entries.RemoveAt(i);

            var staleKeys = new List<string>();
            foreach (var pair in _seen)
                if (pair.Value == null || IsFihNamed(pair.Value.DisplayName)) staleKeys.Add(pair.Key);
            foreach (var key in staleKeys) _seen.Remove(key);
        }

        public static bool IsFihNamed(string name)
            => !string.IsNullOrEmpty(name)
                && name.IndexOf("FIH", StringComparison.OrdinalIgnoreCase) >= 0;

        public GameObject GetLiveSource(CatalogEntry entry)
        {
            if (entry.Source != null) return entry.Source;
            if (entry.SourcePath != null && entry.SourcePath.StartsWith(ADDRESSABLE_PREFIX))
            {
                entry.Source = LoadAddressable(entry.SourcePath.Substring(ADDRESSABLE_PREFIX.Length));
                if (entry.Source != null)
                {
                    var bounds = ComputeAssetBounds(entry.Source);
                    if (bounds.size != Vector3.zero) entry.BoundsSize = bounds.size;
                    entry.HasCollider = HasSolidCollider(entry.Source);
                }
            }
            else if (entry.SourcePath != null && entry.SourcePath.StartsWith(ASSET_PREFIX))
                entry.Source = FindPrefabAssetByName(entry.SourcePath.Substring(ASSET_PREFIX.Length));
            else
                entry.Source = ResolvePath(entry.SourcePath, exactIndices: true)
                            ?? ResolvePath(entry.SourcePath, exactIndices: false);
            return entry.Source;
        }

        // Preview rendering must not retain every browsed Addressable. Placement uses
        // GetLiveSource and keeps its handle intentionally; previews borrow an otherwise
        // unloaded prefab just long enough to render its thumbnail.
        internal GameObject GetPreviewSource(CatalogEntry entry,
            out AsyncOperationHandle<GameObject> temporaryHandle, out bool hasTemporaryHandle)
        {
            temporaryHandle = default;
            hasTemporaryHandle = false;
            if (entry == null) return null;
            if (entry.Source != null) return entry.Source;
            if (entry.SourcePath == null || !entry.SourcePath.StartsWith(ADDRESSABLE_PREFIX))
                return GetLiveSource(entry);

            string address = entry.SourcePath.Substring(ADDRESSABLE_PREFIX.Length);
            if (!_addressableLocations.TryGetValue(address, out var location))
            {
                if (_addressableRetryAt < 0f) _addressableRetryAt = Time.unscaledTime + 0.25f;
                return null;
            }
            if (_addressableHandles.TryGetValue(address, out var retained) && retained.IsValid())
                return retained.Result;

            temporaryHandle = Addressables.LoadAssetAsync<GameObject>(location);
            var prefab = temporaryHandle.WaitForCompletion();
            if (prefab == null)
            {
                if (temporaryHandle.IsValid()) Addressables.Release(temporaryHandle);
                temporaryHandle = default;
                return null;
            }
            hasTemporaryHandle = true;
            return prefab;
        }

        internal void ReleasePreviewSource(AsyncOperationHandle<GameObject> handle, bool temporary)
        {
            if (temporary && handle.IsValid()) Addressables.Release(handle);
        }

        public static bool HasSolidCollider(GameObject source)
        {
            if (source == null) return false;
            foreach (var collider in source.GetComponentsInChildren<Collider>(true))
                if (collider != null && !collider.isTrigger) return true;
            return false;
        }

        private GameObject LoadAddressable(string address)
        {
            try
            {
                if (!_addressableLocations.TryGetValue(address, out var location))
                {
                    // Cache files can contain an address before discovery finishes. The
                    // main Update loop retries discovery; never mutate catalog rows here
                    // while IMGUI may be iterating them.
                    if (_addressableRetryAt < 0f) _addressableRetryAt = Time.unscaledTime + 0.25f;
                    return null;
                }
                if (_addressableHandles.TryGetValue(address, out var retained) && retained.IsValid())
                    return retained.Result;

                var handle = Addressables.LoadAssetAsync<GameObject>(location);
                var prefab = handle.WaitForCompletion();
                if (prefab == null)
                {
                    if (handle.IsValid()) Addressables.Release(handle);
                    return null;
                }
                _addressableHandles[address] = handle;
                return prefab;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Could not load addressable '{address}': {ex.Message}");
                return null;
            }
        }

        private static GameObject ResolvePath(string path, bool exactIndices)
        {
            try
            {
                var segments = path.Split('/');
                var scene = SceneManager.GetActiveScene();
                var roots = scene.GetRootGameObjects();

                Transform current = null;
                foreach (var segRaw in segments)
                {
                    ParseSegment(segRaw, out string name, out int index);
                    if (!exactIndices) index = -1; // first match by name

                    Transform next = null;
                    if (current == null)
                    {
                        int seen = 0;
                        foreach (var r in roots)
                        {
                            if (r.name != name) continue;
                            if (index < 0 || seen == index) { next = r.transform; break; }
                            seen++;
                        }
                    }
                    else
                    {
                        int seen = 0;
                        for (int i = 0; i < current.childCount; i++)
                        {
                            var child = current.GetChild(i);
                            if (child.name != name) continue;
                            if (index < 0 || seen == index) { next = child; break; }
                            seen++;
                        }
                    }

                    if (next == null) return null;
                    current = next;
                }
                return current?.gameObject;
            }
            catch
            {
                return null;
            }
        }

        // Full completeness report: every catalog entry (is X listed, and as what?) plus
        // every root the LAST scan skipped and why. Called from the manual Rescan button
        // only — automatic rescans never touch the disk.
        public int WriteAudit(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FIH Map Editor — catalog audit ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"{Entries.Count} catalog entries; this audit scan skipped {_auditSkip.Count} scene root(s).");
            sb.AppendLine("Catalog is additive: 'dedup' means that object IS available, under the quoted entry name.");
            sb.AppendLine();

            sb.AppendLine("== CATALOG ENTRIES (category | name | source) ==");
            var entries = new List<CatalogEntry>(Entries);
            entries.Sort((a, b) => string.CompareOrdinal(a.Category + a.DisplayName, b.Category + b.DisplayName));
            foreach (var e in entries)
                sb.AppendLine($"{e.Category,-12} {e.DisplayName,-42} {e.SourcePath}");

            sb.AppendLine();
            sb.AppendLine("== SKIPPED BY LAST SCAN (reason | path) ==");
            var skips = new List<string>(_auditSkip);
            skips.Sort(StringComparer.Ordinal);
            foreach (var line in skips) sb.AppendLine(line);

            File.WriteAllText(path, sb.ToString());
            MapEditorPlugin.Logger.LogInfo($"[CATALOG] Audit written to {path} ({Entries.Count} entries, {_auditSkip.Count} skips).");
            return _auditSkip.Count;
        }

        private static void ParseSegment(string seg, out string name, out int index)
        {
            index = 0;
            int hash = seg.LastIndexOf('#');
            if (hash >= 0 && hash < seg.Length - 1 && int.TryParse(seg.Substring(hash + 1), out int parsed))
            {
                name = seg.Substring(0, hash);
                index = parsed;
            }
            else
            {
                name = seg;
            }
        }
    }
}
