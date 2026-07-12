using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    public class PlacedObject
    {
        public int Id;
        // Stable across the object's lifetime — survives duplicate/undo/save-load and is
        // the identity used by grouping and by multiplayer sync.
        public string Uid = System.Guid.NewGuid().ToString("N");
        public string GroupId;            // objects sharing this move/rotate/scale/delete together
        public GameObject Root;
        public string SourcePath;
        public string SourceName;
        public TintColor Tint = TintColor.None;
        public float[] CustomColor;       // RGB 0..1; overrides Tint when set
        public Vector3 OriginalScale = Vector3.one;
        public bool HasCollider = true;   // false → selectable only via bounds picking

        // Mechanics: detected from the source object at spawn (or restored from the map
        // file); the mod simulates the behavior since the game components are stripped.
        public MechanicType Mechanic = MechanicType.None;
        public float BoostForce = MechanicsDetector.DEFAULT_BOOST_FORCE;
        public float CannonTimer = MechanicsDetector.DEFAULT_CANNON_TIMER;
        public float[] CannonTarget;      // landing point (cannons + aimed pads)
        public float[] CannonLaunchPos;   // custom hold/launch point; null = above the collider

        // Original material colors, captured the first time a tint is applied so
        // tint "0" can restore them exactly.
        public Dictionary<int, Color> OriginalColors;

        public MapObjectData ToData()
        {
            var t = Root.transform;
            return new MapObjectData
            {
                Uid = Uid,
                GroupId = GroupId,
                Source = SourcePath,
                SourceName = SourceName,
                Pos = VecUtil.ToArray(t.position),
                Rot = VecUtil.ToArray(t.eulerAngles),
                Scale = VecUtil.ToArray(t.localScale),
                Tint = Tint,
                CustomColor = (float[])CustomColor?.Clone(),
                Mechanic = Mechanic,
                BoostForce = Mechanic == MechanicType.BoostPad ? BoostForce : (float?)null,
                CannonTimer = Mechanic == MechanicType.Cannon ? CannonTimer : (float?)null,
                CannonTarget = Mechanic != MechanicType.None ? (float[])CannonTarget?.Clone() : null,
                CannonLaunchPos = Mechanic == MechanicType.Cannon ? (float[])CannonLaunchPos?.Clone() : null,
            };
        }
    }

    // Owns clone spawning (with FishNet stripping), the registry of placed objects,
    // tinting, deletion and wiping.
    public class PlacedObjectManager
    {
        private readonly List<PlacedObject> _placed = new List<PlacedObject>();
        private readonly Dictionary<int, PlacedObject> _byRootId = new Dictionary<int, PlacedObject>();

        private GameObject _spawnRoot;   // inactive + DontDestroyOnLoad: clones under it never run Awake
        private GameObject _mapRoot;     // scene-local: a scene reload naturally clears all clones
        private int _nextId = 1;

        public IReadOnlyList<PlacedObject> Placed => _placed;
        public int Count => _placed.Count;

        private GameObject GetSpawnRoot()
        {
            if (_spawnRoot == null)
            {
                _spawnRoot = new GameObject("FIH_SpawnRoot");
                _spawnRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_spawnRoot);
            }
            return _spawnRoot;
        }

        private GameObject GetMapRoot()
        {
            if (_mapRoot == null)
            {
                _mapRoot = new GameObject("FIH_MapObjectsRoot");
            }
            return _mapRoot;
        }

        // The scene reload destroyed the map root and all clones; drop the stale registry.
        public void OnSceneChanged()
        {
            _placed.Clear();
            _byRootId.Clear();
            _mapRoot = null;
        }

        public PlacedObject Spawn(GameObject source, string sourcePath, string sourceName,
                                  Vector3 position, Quaternion rotation, Vector3 scale, TintColor tint,
                                  MapObjectData restore = null)
        {
            if (source == null) return null;
            try
            {
                // Mechanics must be detected on the SOURCE: the clone gets its
                // EHS.Interactables components stripped below. When the source is one of
                // OUR clones (duplicate/multi-clone), never climb ancestors — the shared
                // FIH_MapObjectsRoot parent contains every other clone, and a cannon
                // sibling would leak its mechanic onto a duplicated plank. Duplicates get
                // their mechanics through `restore` instead.
                bool sourceIsClone = source.name.Contains(ObjectCatalog.CLONE_MARKER)
                    || source.transform.root.name == "FIH_MapObjectsRoot"
                    || source.transform.root.name == "FIH_SpawnRoot";
                var mechanic = MechanicsDetector.Detect(source, out float boostForce, out float cannonTimer,
                    climbAncestors: !sourceIsClone);

                // Instantiate under an INACTIVE parent so Awake doesn't run on any
                // component before we strip the networking ones.
                var clone = UnityEngine.Object.Instantiate(source, GetSpawnRoot().transform);
                StripComponents(clone);

                clone.name = $"{ObjectCatalog.CleanName(sourceName)} {ObjectCatalog.CLONE_MARKER}";
                clone.transform.SetParent(GetMapRoot().transform, true);
                clone.transform.position = position;
                clone.transform.rotation = rotation;
                clone.transform.localScale = scale;
                clone.SetActive(true);

                // A clone must always spawn VISIBLE. Sources cataloged straight from the
                // level can have their renderers toggled off or their visual nodes
                // deactivated (phase objects, chunks the game hides, Hidden-category
                // sources) — a copy inheriting that places as collision-only, an
                // invisible block. Only rescue clones that would otherwise show NOTHING;
                // normally-visible objects keep their intentional on/off child states.
                bool anyVisible = false;
                foreach (var r in clone.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { anyVisible = true; break; }
                if (!anyVisible)
                {
                    int reEnabled = 0;
                    foreach (var childT in clone.GetComponentsInChildren<Transform>(true))
                    {
                        if (childT != null && !childT.gameObject.activeSelf)
                        {
                            childT.gameObject.SetActive(true);
                            reEnabled++;
                        }
                    }
                    foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && !r.enabled)
                        {
                            r.enabled = true;
                            reEnabled++;
                        }
                    }
                    if (reEnabled > 0)
                        MapEditorPlugin.Logger.LogInfo(
                            $"[PLACE] '{clone.name}' had no visible renderer — re-enabled {reEnabled} hidden renderer(s)/node(s).");
                }

                // Visual-only sources (SM_*, decor, prefab assets with no collider) would
                // leave the clone unselectable via the physics raycast and physics-less in
                // play mode — the user expects placed geometry to behave like the level's
                // own colliders. Add a matching collider (MeshCollider from the shared
                // mesh, BoxCollider fallback) so the clone enters the normal picking +
                // physics path the same way the original collider-bearing sources do.
                EnsureColliders(clone);

                var placed = new PlacedObject
                {
                    Id = _nextId++,
                    Root = clone,
                    SourcePath = sourcePath,
                    SourceName = sourceName,
                    OriginalScale = scale,
                    HasCollider = clone.GetComponentInChildren<Collider>(true) != null,
                    Mechanic = mechanic,
                    BoostForce = boostForce,
                    CannonTimer = cannonTimer,
                };
                if (mechanic == MechanicType.Cannon)
                    placed.CannonTarget = VecUtil.ToArray(position + rotation * Vector3.forward * 12f);
                else if (mechanic == MechanicType.BoostPad)
                    // Pads are always aimed: the violet ring is where you land.
                    placed.CannonTarget = VecUtil.ToArray(position + rotation * (Vector3.up * 5f + Vector3.forward * 4f));

                // Restored data (load / undo / duplicate) wins over fresh detection —
                // a duplicated clone can't re-detect (its own components are stripped).
                if (restore != null && restore.Mechanic != MechanicType.None)
                {
                    placed.Mechanic = restore.Mechanic;
                    if (restore.BoostForce.HasValue) placed.BoostForce = restore.BoostForce.Value;
                    if (restore.CannonTimer.HasValue) placed.CannonTimer = restore.CannonTimer.Value;
                    if (restore.CannonTarget != null) placed.CannonTarget = (float[])restore.CannonTarget.Clone();
                    if (restore.CannonLaunchPos != null) placed.CannonLaunchPos = (float[])restore.CannonLaunchPos.Clone();
                    // Maps saved before always-aim: synthesize the missing target.
                    if (placed.CannonTarget == null)
                        placed.CannonTarget = VecUtil.ToArray(position + rotation *
                            (placed.Mechanic == MechanicType.Cannon ? Vector3.forward * 12f : Vector3.up * 5f + Vector3.forward * 4f));
                }

                if (placed.Mechanic != MechanicType.None)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[MECH] '{clone.name}' is a {placed.Mechanic} (force={placed.BoostForce:0.#}, timer={placed.CannonTimer:0.##}s)");

                // Uid/GroupId round-trip through restore so a re-spawn (undo, remote
                // upsert, save/load) keeps the SAME identity instead of minting a new one.
                if (restore != null)
                {
                    if (!string.IsNullOrEmpty(restore.Uid)) placed.Uid = restore.Uid;
                    placed.GroupId = restore.GroupId;
                }

                // The game's surface behavior (slippery ground, footstep surfaces) lives
                // in its GroundRegistry, keyed per collider — register the clone's so it
                // behaves exactly like the original it was copied from.
                GroundRegistrar.RegisterClone(clone, source);

                _placed.Add(placed);
                _byRootId[clone.GetInstanceID()] = placed;

                if (restore?.CustomColor != null && restore.CustomColor.Length >= 3)
                    ApplyCustomColor(placed, new Color(restore.CustomColor[0], restore.CustomColor[1], restore.CustomColor[2]));
                else if (tint != TintColor.None)
                    ApplyTint(placed, tint);

                return placed;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[PLACE] Error spawning clone of '{sourceName}': {ex}");
                return null;
            }
        }

        public PlacedObject FindById(int id)
        {
            foreach (var p in _placed)
                if (p.Id == id) return p;
            return null;
        }

        public PlacedObject FindByUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            foreach (var p in _placed)
                if (p.Uid == uid) return p;
            return null;
        }

        // Remove networking (FishNet) and physics components so the clone is plain static
        // scenery: client-side only, no server reconciliation, no falling over.
        private static void StripComponents(GameObject clone)
        {
            var toDestroy = new List<Component>();
            var networkObjects = new List<Component>();

            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                string fullName = null;
                try { fullName = comp.GetIl2CppType()?.FullName; }
                catch { }
                if (fullName == null) continue;

                if (fullName.StartsWith("FishNet") || fullName.StartsWith("EHS.Interactables"))
                {
                    // EHS interactables (boost pads, cannons...) are NetworkBehaviours: dead
                    // without a server, but still discoverable by the game's interaction
                    // raycasts — destroy them and let MechanicsController simulate instead.
                    // NetworkObject must go last: NetworkBehaviours depend on it.
                    if (fullName == "FishNet.Object.NetworkObject") networkObjects.Add(comp);
                    else toDestroy.Add(comp);
                }
                else if (comp.TryCast<Rigidbody>() != null)
                {
                    toDestroy.Add(comp);
                }
            }
            toDestroy.AddRange(networkObjects);

            foreach (var comp in toDestroy)
            {
                try
                {
                    UnityEngine.Object.DestroyImmediate(comp);
                }
                catch (Exception ex)
                {
                    // Fallback: at least stop it from running.
                    try
                    {
                        var beh = comp.TryCast<Behaviour>();
                        if (beh != null) beh.enabled = false;
                    }
                    catch { }
                    MapEditorPlugin.Logger.LogWarning($"[PLACE] Could not destroy component: {ex.Message}");
                }
            }
        }

        // Climb from a raycast hit to the placed-object root, or null if the hit
        // wasn't on one of our clones.
        public PlacedObject FromTransform(Transform t)
        {
            while (t != null)
            {
                if (_byRootId.TryGetValue(t.gameObject.GetInstanceID(), out var placed))
                    return placed;
                t = t.parent;
            }
            return null;
        }

        // Backfill colliders on a clone whose source had none. Done AFTER the
        // visibility rescue so the visible extent is the right one to measure.
        //
        // The sharedMesh on a game-bundle asset is non-accessible at the native
        // level (Read/Write Enabled = OFF in the asset's import settings, baked
        // at build time) — Unity refuses to bake CollisionMeshData for any
        // MeshCollider that uses it, regardless of whether the C# wrapper is an
        // Instantiate() copy. The flag lives on the native data, not the
        // wrapper, so Instantiate doesn't help. BoxCollider is the only type
        // that can be guaranteed to work at runtime on these assets.
        //
        // One BoxCollider covering the full visual AABB in the root's local
        // space (computed by transforming each renderer's world-AABB corners
        // through InverseTransformPoint so the collider follows placement
        // position, rotation and scale). Stacking the AABB into multiple
        // step-sized boxes LOOKED like the right answer for stairs, but the
        // game detects the player as "always grounded" whenever a non-trigger
        // collider is in constant contact — and with a 0.3m stack, a player
        // ~1m tall always has their torso inside the box of the step above
        // them, so the jump keeps resetting mid-air. A single solid box means
        // the player stands on the TOP face (nothing above them in the air)
        // and the jump works normally. The trade-off is no step-by-step
        // climb — the top of the box is at the top of the AABB, so the player
        // can only reach it by jumping onto it from the side or by chaining
        // ramps next to it.
        private static void EnsureColliders(GameObject root)
        {
            try
            {
                if (root == null) return;
                if (root.GetComponentInChildren<Collider>(true) != null) return;

                var rt = root.transform;
                Bounds? localAabb = null;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var rb = r.bounds;
                    Vector3 c = rb.center, e = rb.extents;
                    // 8 corners of the renderer's world AABB, brought into local space.
                    for (int xi = -1; xi <= 1; xi += 2)
                        for (int yi = -1; yi <= 1; yi += 2)
                            for (int zi = -1; zi <= 1; zi += 2)
                            {
                                Vector3 lc = rt.InverseTransformPoint(
                                    c + new Vector3(xi * e.x, yi * e.y, zi * e.z));
                                if (localAabb == null) localAabb = new Bounds(lc, Vector3.zero);
                                else { var b = localAabb.Value; b.Encapsulate(lc); localAabb = b; }
                            }
                }
                if (localAabb == null) return;
                var la = localAabb.Value;
                if (la.size.sqrMagnitude < 0.01f) return;        // degenerate

                var bc = root.AddComponent<BoxCollider>();
                bc.center = la.center;
                bc.size = la.size;

                MapEditorPlugin.Logger.LogInfo(
                    $"[PLACE] '{root.name}' had no colliders — added 1 box collider (AABB {la.size.x:0.#}x{la.size.y:0.#}x{la.size.z:0.#}) for raycast picking + play-mode physics.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PLACE] Could not add colliders to '{root.name}': {ex.Message}");
            }
        }

        public void ApplyTint(PlacedObject placed, TintColor tint)
        {
            placed.CustomColor = null; // a preset overrides any custom color
            Color? color = tint switch
            {
                TintColor.None => null,
                TintColor.Red => new Color(1f, 0.35f, 0.35f),
                TintColor.Blue => new Color(0.4f, 0.55f, 1f),
                TintColor.Green => new Color(0.4f, 1f, 0.45f),
                TintColor.Yellow => new Color(1f, 0.95f, 0.35f),
                _ => null,
            };
            ApplyColorInternal(placed, color);
            placed.Tint = tint;
        }

        // Arbitrary color (hex/RGB picker). Setting a custom color clears the enum
        // preset — the two are mutually exclusive views of the same override.
        public void ApplyCustomColor(PlacedObject placed, Color color)
        {
            placed.Tint = TintColor.None;
            placed.CustomColor = new[] { color.r, color.g, color.b };
            ApplyColorInternal(placed, color);
        }

        public void ClearColor(PlacedObject placed)
        {
            placed.Tint = TintColor.None;
            placed.CustomColor = null;
            ApplyColorInternal(placed, null);
        }

        // Shared apply: null clears back to the captured original color.
        private void ApplyColorInternal(PlacedObject placed, Color? color)
        {
            if (placed?.Root == null) return;
            try
            {
                var renderers = placed.Root.GetComponentsInChildren<Renderer>(true);

                if (color == null)
                {
                    if (placed.OriginalColors != null)
                    {
                        foreach (var r in renderers)
                        {
                            if (r == null) continue;
                            if (!placed.OriginalColors.TryGetValue(r.GetInstanceID(), out var original)) continue;
                            SetRendererColor(r, original);
                        }
                    }
                    return;
                }

                placed.OriginalColors ??= new Dictionary<int, Color>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    // Capture the pre-tint color once. Accessing .material instantiates a
                    // per-clone material, so the tint never leaks to the original object.
                    if (!placed.OriginalColors.ContainsKey(r.GetInstanceID()))
                    {
                        if (TryGetRendererColor(r, out var original))
                            placed.OriginalColors[r.GetInstanceID()] = original;
                    }
                    SetRendererColor(r, color.Value);
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[TINT] Error applying color: {ex.Message}");
            }
        }

        // URP uses _BaseColor; fall back to the classic _Color / material.color.
        private static bool TryGetRendererColor(Renderer r, out Color color)
        {
            color = Color.white;
            try
            {
                var mat = r.material;
                if (mat == null) return false;
                if (mat.HasProperty("_BaseColor")) { color = mat.GetColor("_BaseColor"); return true; }
                if (mat.HasProperty("_Color")) { color = mat.color; return true; }
            }
            catch { }
            return false;
        }

        private static void SetRendererColor(Renderer r, Color color)
        {
            try
            {
                var mat = r.material;
                if (mat == null) return;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                else if (mat.HasProperty("_Color")) mat.color = color;
            }
            catch { }
        }

        public void Delete(PlacedObject placed)
        {
            if (placed == null) return;
            _placed.Remove(placed);
            if (placed.Root != null)
            {
                _byRootId.Remove(placed.Root.GetInstanceID());
                UnityEngine.Object.Destroy(placed.Root);
            }
        }

        public void WipeAll()
        {
            foreach (var p in _placed)
            {
                if (p.Root != null) UnityEngine.Object.Destroy(p.Root);
            }
            _placed.Clear();
            _byRootId.Clear();
        }

        public List<MapObjectData> Snapshot()
        {
            var list = new List<MapObjectData>();
            foreach (var p in _placed)
            {
                if (p.Root == null) continue;
                list.Add(p.ToData());
            }
            return list;
        }
    }
}
