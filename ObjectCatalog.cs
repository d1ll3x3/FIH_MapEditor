using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
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
        public const string ASSET_CATEGORY = "GameFiles";

        public List<CatalogEntry> Entries { get; } = new List<CatalogEntry>();
        public List<string> Categories { get; } = new List<string>();
        public List<DecorPickable> ColliderlessRoots { get; } = new List<DecorPickable>();
        public bool HasScanned { get; private set; }
        // Bumped on every Scan()/Clear() so GUI-side caches of Entries can invalidate.
        public int ScanVersion { get; private set; }

        // Scans are ADDITIVE within a scene session: the game loads assets and spawns
        // objects lazily, so later scans discover things the first one couldn't see.
        // An entry, once found, is never dropped — only Clear() (scene change) resets.
        private readonly Dictionary<string, CatalogEntry> _seen = new Dictionary<string, CatalogEntry>();

        private readonly GameObjectFinder _finder;

        // Anything bigger than this on any axis is level-chunk scale, listed under "Large".
        private const float LARGE_BOUNDS = 60f;

        public ObjectCatalog(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void Clear()
        {
            Entries.Clear();
            Categories.Clear();
            ColliderlessRoots.Clear();
            _seen.Clear();
            HasScanned = false;
            ScanVersion++;
        }

        public void Scan()
        {
            // Additive: keep everything already found. Decor instances are rebuilt (the
            // live objects may have despawned); categories are re-derived at the end.
            Categories.Clear();
            ColliderlessRoots.Clear();
            ScanVersion++;
            int entriesBefore = Entries.Count;

            try
            {
                var playerRoot = _finder.FindPlayer()?.transform?.root;
                var seen = _seen; // dedupe key → entry, persistent across rescans
                var visitedRoots = new HashSet<int>();
                var wrapperCache = new Dictionary<int, int>();     // transform id → renderable child count

                var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    var t = col.transform;
                    if (t == null) continue;

                    var root = FindCandidateRoot(t, wrapperCache);
                    if (root == null) continue;
                    if (!visitedRoots.Add(root.GetInstanceID())) continue;
                    if (!IsValidCandidate(root, playerRoot)) continue;

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
                        bounds = MechanicsController.ComputeColliderBounds(root.gameObject);
                        if (bounds.size == Vector3.zero) continue; // truly nothing measurable
                        invisible = true;
                    }

                    string display = CleanName(root.name);
                    string meshName = FirstMeshName(root.gameObject);
                    string key = display + "|" + meshName;

                    if (seen.ContainsKey(key)) continue;

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

                // Renderer-only pass: pure decoration (no collider anywhere) never shows
                // up in the collider sweep and physics raycasts can't select it. Catalog
                // it here and remember each instance's AABB for bounds-based picking.
                var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var t = r.transform;
                    if (t == null) continue;

                    var root = FindCandidateRoot(t, wrapperCache);
                    if (root == null) continue;
                    if (!visitedRoots.Add(root.GetInstanceID())) continue;
                    if (!IsValidCandidate(root, playerRoot)) continue;

                    var bounds = ComputeBounds(root.gameObject);
                    if (bounds.size == Vector3.zero) continue;

                    bool hasCollider = root.GetComponentInChildren<Collider>(true) != null;
                    if (!hasCollider)
                        ColliderlessRoots.Add(new DecorPickable { Root = root.gameObject, Bounds = bounds });

                    string display = CleanName(root.name);
                    string key = display + "|" + FirstMeshName(root.gameObject);
                    if (seen.ContainsKey(key)) continue;

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

                ScanInactiveSceneObjects(seen, playerRoot);
                ScanPrefabAssets(seen);
                ScanMechanicsAssemblies(seen);

                // Interactables (boost pads, cannons...) get their own category so they
                // are easy to find. Subtree-only check: assembly PIECES (visual mesh,
                // trigger...) must not clutter Mechanics — the full assembly entry from
                // ScanMechanicsAssemblies is the one to use.
                foreach (var e in Entries)
                {
                    if (e.Source != null && MechanicsDetector.DetectTypeInSubtree(e.Source) != MechanicType.None)
                        e.Category = "Mechanics";
                }

                Entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));

                foreach (var e in Entries)
                    if (!Categories.Contains(e.Category)) Categories.Add(e.Category);
                Categories.Sort();

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
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[CATALOG] Scan error: {ex}");
            }
        }

        // Third scan source: scene objects that were DISABLED at scan time (phase-specific
        // obstacles, pooled props, despawned pickups). FindObjectsOfType never returns
        // them, so without this pass they simply look "missing" from the catalog.
        // Cloning re-activates the clone root, so they place as visible objects.
        private void ScanInactiveSceneObjects(Dictionary<string, CatalogEntry> seen, Transform playerRoot)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                int added = 0;
                foreach (var obj in all)
                {
                    var go = obj?.TryCast<GameObject>();
                    if (go == null) continue;
                    if (!go.scene.IsValid()) continue;               // assets: separate pass
                    if (go.activeInHierarchy) continue;              // active: already scanned
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
                    string key = display + "|" + FirstMeshName(go);
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
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Inactive-object scan added {added} hidden entries.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Inactive-object scan failed: {ex.Message}");
            }
        }

        // Mechanics pass: the game's interactables (boost pads, cannons) are multi-part
        // assemblies — visual mesh, interact trigger and area as SIBLINGS. The generic
        // root heuristics catalog those pieces separately, so cloning one piece gives a
        // broken half-cannon. This pass catalogs the whole assembly as one entry.
        private void ScanMechanicsAssemblies(Dictionary<string, CatalogEntry> seen)
        {
            try
            {
                int added = 0;
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string typeName = null;
                    try { typeName = mb.GetIl2CppType()?.Name; }
                    catch { }
                    if (typeName == null) continue;
                    if (!typeName.Contains("InteractableBoostPad") && !typeName.Contains("InteractableCannon"))
                        continue;

                    // Assembly root: climb while the parent is still cannon-sized. Level
                    // chunk containers fail the bounds guard and stop the climb.
                    var root = mb.transform;
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
                    string key = display + "|" + FirstMeshName(root.gameObject);
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
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Mechanics-assembly scan added {added} entries.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Mechanics-assembly scan failed: {ex.Message}");
            }
        }

        // Second scan source: prefab assets loaded from the game files. These are not in
        // any scene — the level references them, Unity keeps them in memory, and
        // Resources.FindObjectsOfTypeAll sees them. Only prefabs Unity has actually
        // loaded appear here; unreferenced bundles on disk do not.
        private void ScanPrefabAssets(Dictionary<string, CatalogEntry> seen)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                int added = 0;
                foreach (var obj in all)
                {
                    var go = obj?.TryCast<GameObject>();
                    if (go == null) continue;
                    if (go.scene.IsValid()) continue;                // scene objects: already scanned
                    if (go.transform.parent != null) continue;       // prefab roots only
                    if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                    if (go.name.Contains(CLONE_MARKER)) continue;
                    if (!IsValidPrefabCandidate(go)) continue;

                    var bounds = ComputeAssetBounds(go);
                    if (bounds.size == Vector3.zero) continue;

                    string display = CleanName(go.name);
                    string key = display + "|" + FirstMeshName(go);
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
                MapEditorPlugin.Logger.LogInfo($"[CATALOG] Prefab-asset scan added {added} entries from game files.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CATALOG] Prefab-asset scan failed: {ex.Message}");
            }
        }

        private bool IsValidPrefabCandidate(GameObject go)
        {
            if (go.GetComponentInChildren<Renderer>(true) == null) return false;
            if (go.GetComponentInChildren<Camera>(true) != null) return false;
            if (go.GetComponentInChildren<AudioListener>(true) != null) return false;
            if (go.GetComponentInChildren<Canvas>(true) != null) return false;
            try
            {
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var typeName = mb.GetIl2CppType()?.Name;
                    if (typeName == "PlayerNetworked" || typeName == "NetworkManager") return false;
                }
            }
            catch { }
            return true;
        }

        // Renderer.bounds is unreliable on inactive prefab assets; approximate from the
        // shared meshes instead (rotation ignored — good enough for catalog sizing).
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

            static void Accumulate(Bounds local, Transform t, ref Bounds bounds, ref bool has)
            {
                Vector3 center = t.TransformPoint(local.center);
                Vector3 scale = t.lossyScale;
                Vector3 size = new Vector3(Mathf.Abs(local.size.x * scale.x),
                                           Mathf.Abs(local.size.y * scale.y),
                                           Mathf.Abs(local.size.z * scale.z));
                var wb = new Bounds(center, size);
                if (!has) { bounds = wb; has = true; }
                else bounds.Encapsulate(wb);
            }
        }

        // Re-find a prefab asset by name (used when a stored map references an asset
        // entry, or when Unity unloaded and re-loaded assets between sessions).
        private static GameObject FindPrefabAssetByName(string name)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                foreach (var obj in all)
                {
                    var go = obj?.TryCast<GameObject>();
                    if (go == null) continue;
                    if (go.scene.IsValid()) continue;
                    if (go.transform.parent != null) continue;
                    if (go.name == name) return go;
                }
            }
            catch { }
            return null;
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
            return root;
        }

        private bool IsValidCandidate(Transform root, Transform playerRoot, bool requireActive = true)
        {
            if (root == null) return false;
            var go = root.gameObject;
            if (requireActive && !go.activeInHierarchy) return false;
            if (go.name.Contains(CLONE_MARKER)) return false;
            if (go.name == "FIH_MapObjectsRoot" || go.name == "FIH_SpawnRoot") return false;
            if (playerRoot != null && root.root == playerRoot) return false;

            // Skip anything that is (or carries) gameplay-critical machinery.
            if (go.GetComponentInChildren<Camera>(true) != null) return false;
            if (go.GetComponentInChildren<AudioListener>(true) != null) return false;
            if (go.GetComponentInChildren<Canvas>(true) != null) return false;

            // Skip other players / networked characters
            try
            {
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var typeName = mb.GetIl2CppType()?.Name;
                    if (typeName == "PlayerNetworked" || typeName == "NetworkManager") return false;
                }
            }
            catch { }

            return true;
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

        public GameObject GetLiveSource(CatalogEntry entry)
        {
            if (entry.Source != null) return entry.Source;
            if (entry.SourcePath != null && entry.SourcePath.StartsWith(ASSET_PREFIX))
                entry.Source = FindPrefabAssetByName(entry.SourcePath.Substring(ASSET_PREFIX.Length));
            else
                entry.Source = ResolvePath(entry.SourcePath, exactIndices: true)
                            ?? ResolvePath(entry.SourcePath, exactIndices: false);
            return entry.Source;
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
