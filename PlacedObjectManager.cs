using System;
using System.Collections.Generic;
using EHS;
using EHS.Bootstraps;
using EHS.Interactables.Abstract;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        // For real networked mechanics Root is the NetworkObject spawned by this
        // scene spawner. Keep the spawner so delete/map unload can use the game's
        // matching network de-initialization path.
        public GameObject NetworkSpawnerRoot;
        public NetworkSpawnerPoint NetworkSpawner;
        // Non-None for every placed network interactable, on the host and on clients.
        // Clients never spawn: their Root is a local anchor and the FishNet replica the
        // host spawned is parented under it once its ObjectId arrives over coop sync.
        public NetworkMechanicKind NetworkKind = NetworkMechanicKind.None;
        public FishNet.Object.NetworkObject Replica;
        // The prefab it was spawned from: duplicating must spawn that, never copy the
        // live NetworkObject (host) or the anchor with its replica inside (client).
        public FishNet.Object.NetworkObject NetworkPrefab;
        public bool IsReplicaAnchor => NetworkKind != NetworkMechanicKind.None && NetworkSpawner == null;
        // The game's local (non-FishNet) boost pad on a clone, e.g. the CandyLand jelly.
        // Its force/angle live in NetworkBoostForce/NetworkBoostAngle like a network pad's.
        public SceneBoostPad ScenePad;
        public bool IsBoostPad => NetworkKind == NetworkMechanicKind.BoostPad || ScenePad != null;

        // Scene mechanics (ziplines, conveyors, synced movers, hooks...) bake their pose
        // in the game's ScenePostInit and many then move their own transforms. In the
        // editor such a clone is frozen at its authored pose; it runs (re-initialized
        // there) in Play and outside the editor. The Armed* pose is the authored pose
        // the game last initialized against — while the clone runs it is the pose that
        // gets saved, not wherever the mechanic has carried the object.
        public bool RearmOnMove;
        public Vector3 ArmedPos;
        public Quaternion ArmedRot;
        public Vector3 ArmedScale;
        public bool Frozen;
        // Authored local pose of every part, so a frozen clone shows the parts where
        // they were placed and the game re-initializes them from there, not mid-swing.
        public List<(Transform part, Vector3 pos, Quaternion rot, Vector3 scale)> AuthoredParts;
        // Joints anchored to a point in the world (no connected body), in root space.
        public List<(Joint joint, Vector3 localAnchor)> WorldAnchors;
        public List<Behaviour> FrozenBehaviours;
        public List<(Rigidbody body, bool kinematic, RigidbodyInterpolation interpolation)> FrozenBodies;
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

        // Settings captured for a real FishNet-spawned interactable. Nullable keeps
        // ordinary scenery and legacy locally-simulated mechanics out of the JSON.
        public float? NetworkBoostForce;
        public float? NetworkBoostAngle;
        public float? NetworkCannonForce;
        public float? NetworkCannonAngle;
        public float? NetworkCannonAirControlBlock;
        public float? NetworkBreakDelay;
        public float? NetworkBreakDuration;
        public float? NetworkShatteredDuration;
        public float? NetworkShatterImpulse;

        // Original material colors, captured the first time a tint is applied so
        // tint "0" can restore them exactly.
        public Dictionary<int, Color> OriginalColors;

        public MapObjectData ToData()
        {
            var t = Root.transform;
            bool running = RearmOnMove && !Frozen;
            return new MapObjectData
            {
                Uid = Uid,
                GroupId = GroupId,
                Source = SourcePath,
                SourceName = SourceName,
                Pos = VecUtil.ToArray(running ? ArmedPos : t.position),
                Rot = VecUtil.ToArray(running ? ArmedRot.eulerAngles : t.eulerAngles),
                Scale = VecUtil.ToArray(running ? ArmedScale : t.localScale),
                Tint = Tint,
                CustomColor = (float[])CustomColor?.Clone(),
                Mechanic = Mechanic,
                BoostForce = Mechanic == MechanicType.BoostPad ? BoostForce : (float?)null,
                CannonTimer = Mechanic == MechanicType.Cannon ? CannonTimer : (float?)null,
                CannonTarget = (Mechanic != MechanicType.None
                    || NetworkBoostForce.HasValue || NetworkCannonForce.HasValue)
                    ? (float[])CannonTarget?.Clone() : null,
                CannonLaunchPos = Mechanic == MechanicType.Cannon ? (float[])CannonLaunchPos?.Clone() : null,
                NetworkBoostForce = NetworkBoostForce,
                NetworkBoostAngle = NetworkBoostAngle,
                NetworkCannonForce = NetworkCannonForce,
                NetworkCannonAngle = NetworkCannonAngle,
                NetworkCannonAirControlBlock = NetworkCannonAirControlBlock,
                NetworkBreakDelay = NetworkBreakDelay,
                NetworkBreakDuration = NetworkBreakDuration,
                NetworkShatteredDuration = NetworkShatteredDuration,
                NetworkShatterImpulse = NetworkShatterImpulse,
            };
        }

        public void CopyNetworkSettingsFrom(MapObjectData data)
        {
            NetworkBoostForce = data.NetworkBoostForce;
            NetworkBoostAngle = data.NetworkBoostAngle;
            NetworkCannonForce = data.NetworkCannonForce;
            NetworkCannonAngle = data.NetworkCannonAngle;
            NetworkCannonAirControlBlock = data.NetworkCannonAirControlBlock;
            NetworkBreakDelay = data.NetworkBreakDelay;
            NetworkBreakDuration = data.NetworkBreakDuration;
            NetworkShatteredDuration = data.NetworkShatteredDuration;
            NetworkShatterImpulse = data.NetworkShatterImpulse;
        }

        public bool NetworkSettingsDiffer(MapObjectData data)
            => NetworkBoostForce != data.NetworkBoostForce || NetworkBoostAngle != data.NetworkBoostAngle
                || NetworkCannonForce != data.NetworkCannonForce || NetworkCannonAngle != data.NetworkCannonAngle
                || NetworkCannonAirControlBlock != data.NetworkCannonAirControlBlock
                || NetworkBreakDelay != data.NetworkBreakDelay || NetworkBreakDuration != data.NetworkBreakDuration
                || NetworkShatteredDuration != data.NetworkShatteredDuration
                || NetworkShatterImpulse != data.NetworkShatterImpulse;
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

        // Client only: host-assigned FishNet ObjectId per placed network object (by Uid).
        private readonly Dictionary<string, int> _remoteNetIds = new Dictionary<string, int>();
        private float _nextReplicaCheckAt;

        // Applied to NetworkData* before FishNet registers a newly placed mechanic.
        public bool OverrideNetworkMechanicSettings;
        public float NetworkBoostForce = 20f;
        public float NetworkBoostAngle = 45f;
        public float NetworkCannonForce = 20f;
        public float NetworkCannonAngle = 45f;
        public float NetworkCannonAirControlBlock = 0.5f;

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
                var networked = TrySpawnNetworkedInteractable(source, sourcePath, sourceName,
                    position, rotation, scale, tint, restore, out bool networkPlacement);
                if (networkPlacement) return networked; // failure must not fall back to an inert clone

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
                if (mechanic == MechanicType.None)
                    mechanic = MechanicsDetector.InferFromSourceName(sourcePath, sourceName);

                string uid = !string.IsNullOrEmpty(restore?.Uid) ? restore.Uid : Guid.NewGuid().ToString("N");

                // Instantiate under an INACTIVE parent so Awake doesn't run on any
                // component before we strip the networking ones.
                var clone = UnityEngine.Object.Instantiate(source, GetSpawnRoot().transform);
                // Kept inactive until it is posed: scene mechanics register with the
                // game's SceneInitializableRegistry on OnEnable, and ScenePostInit bakes
                // the pose they have at that moment (spline points, synced movers...).
                clone.SetActive(false);
                RestoreRunningStateOfFrozenSource(source, clone);
                int inheritedLayersRestored = HiddenLayer.RestoreInheritedColliderLayers(source, clone);
                if (inheritedLayersRestored > 0)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[PLACE] '{sourceName}' inherited wipe-clean physics layers; restored " +
                        $"{inheritedLayersRestored} collider layer(s) on the clone.");
                StripComponents(clone);
                LocalizeExternalReferences(clone, source);
                // A game mechanic owns its own colliders and visual states (a jump reset
                // is trigger-only, a breaking platform hides its parts): leave both alone.
                bool isMechanic = CarriesSceneInitializable(clone);
                // Reflection over every field is too slow for plain scenery on map load,
                // and only a mechanic can be miswired.
                if (isMechanic)
                {
                    try { ReportExternalReferences(clone); }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[PLACE] Reference check failed for '{sourceName}': {ex.Message}"); }
                }
                AssignFeatureIds(clone, uid);
                // The game's own pad (jellies, scene boost pads) does the boosting; the
                // editor's legacy simulation would launch the player a second time.
                var scenePad = clone.GetComponentInChildren<SceneBoostPad>(true);
                if (scenePad != null) mechanic = MechanicType.None;
                var worldAnchors = CaptureWorldAnchors(clone, source);

                clone.name = $"{ObjectCatalog.CleanName(sourceName)} {ObjectCatalog.CLONE_MARKER}";
                clone.transform.SetParent(GetMapRoot().transform, true);
                clone.transform.position = position;
                clone.transform.rotation = rotation;
                clone.transform.localScale = scale;
                ApplyWorldAnchors(clone, worldAnchors);
                var authoredParts = isMechanic ? CaptureAuthoredParts(clone) : null;
                EnableRespawnZones(clone);
                clone.SetActive(true);

                // A clone must always spawn VISIBLE. Sources cataloged straight from the
                // level can have their renderers toggled off or their visual nodes
                // deactivated (phase objects, chunks the game hides, Hidden-category
                // sources) — a copy inheriting that places as collision-only, an
                // invisible block. Only rescue clones that would otherwise show NOTHING;
                // normally-visible objects keep their intentional on/off child states.
                // Pieces of combined levels (Fridge, The Grid) come with their renderers
                // switched off. A mechanic's inactive nodes are its own states (broken,
                // used...), so for those only the renderers are switched back on.
                bool anyVisible = false;
                foreach (var r in clone.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { anyVisible = true; break; }
                if (!anyVisible)
                {
                    int reEnabled = 0;
                    foreach (var childT in clone.GetComponentsInChildren<Transform>(true))
                    {
                        if (childT != null && !isMechanic && !childT.gameObject.activeSelf)
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
                    // The combiner switches a piece's LODGroup off too; without it every
                    // LOD of the copy draws at once.
                    foreach (var lod in clone.GetComponentsInChildren<LODGroup>(true))
                        if (lod != null && !lod.enabled) lod.enabled = true;
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
                if (!isMechanic) EnsureColliders(clone);

                var placed = new PlacedObject
                {
                    Id = _nextId++,
                    Uid = uid,
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
                if (restore != null && restore.Mechanic != MechanicType.None && scenePad == null)
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

                if (placed.Mechanic != MechanicType.None && EditorConfig.VerboseLogs)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[MECH] '{clone.name}' is a {placed.Mechanic} (force={placed.BoostForce:0.#}, timer={placed.CannonTimer:0.##}s)");

                // Uid/GroupId round-trip through restore so a re-spawn (undo, remote
                // upsert, save/load) keeps the SAME identity instead of minting a new one.
                if (restore != null)
                {
                    if (!string.IsNullOrEmpty(restore.Uid)) placed.Uid = restore.Uid;
                    placed.GroupId = restore.GroupId;
                }
                if (scenePad != null) ConfigureScenePad(placed, scenePad, restore);

                // The game's surface behavior (slippery ground, footstep surfaces) lives
                // in its GroundRegistry, keyed per collider — register the clone's so it
                // behaves exactly like the original it was copied from.
                GroundRegistrar.RegisterClone(clone, source);
                placed.RearmOnMove = isMechanic;
                placed.AuthoredParts = authoredParts;
                placed.WorldAnchors = worldAnchors;
                MarkArmed(placed);

                _placed.Add(placed);
                _byRootId[clone.GetInstanceID()] = placed;
                ApplyPlacementColor(placed, tint, restore);

                return placed;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[PLACE] Error spawning clone of '{sourceName}': {ex}");
                return null;
            }
        }

        // Real FishNet interactables (pads, cannons, breaking platforms, glass, help
        // beams). The host spawns them through a fresh spawner of the concrete kind the
        // levels use; clients create a local anchor and adopt the replica the host
        // spawned (UpdateNetworkReplicas). handled=false means an ordinary catalog object.
        private PlacedObject TrySpawnNetworkedInteractable(GameObject source, string sourcePath,
            string sourceName, Vector3 position, Quaternion rotation, Vector3 scale,
            TintColor tint, MapObjectData restore, out bool handled)
        {
            var prefab = ResolveNetworkPrefab(source, sourceName, out var kind);
            handled = prefab != null;
            if (!handled) return null;

            var placed = new PlacedObject
            {
                Id = _nextId++,
                SourcePath = sourcePath,
                SourceName = sourceName,
                NetworkKind = kind,
                NetworkPrefab = prefab,
                OriginalScale = scale,
                // The real EHS/FishNet components own behavior. Do not also run the
                // editor's legacy local simulation or the effect would fire twice.
                Mechanic = MechanicType.None,
            };
            NetworkMechanics.ReadPrefabDefaults(prefab.gameObject, kind, placed);
            if (OverrideNetworkMechanicSettings) ApplyPlacementOverrides(placed);
            if (restore != null)
            {
                ApplyRestoredNetworkSettings(placed, restore);
                if (!string.IsNullOrEmpty(restore.Uid)) placed.Uid = restore.Uid;
                placed.GroupId = restore.GroupId;
                if (restore.CannonTarget != null) placed.CannonTarget = (float[])restore.CannonTarget.Clone();
                if (restore.CannonLaunchPos != null) placed.CannonLaunchPos = (float[])restore.CannonLaunchPos.Clone();
            }

            bool created = IsNetworkAuthority()
                ? SpawnAsHost(placed, prefab, position, rotation, scale)
                : CreateReplicaAnchor(placed, position, rotation, scale);
            if (!created) return null;

            _placed.Add(placed);
            _byRootId[placed.Root.GetInstanceID()] = placed;
            ApplyPlacementColor(placed, tint, restore);
            return placed;
        }

        // Only a network prefab ROOT redirects. Visual children (SM_Cannon_Platform) and
        // level chunks that merely contain an interactable are ordinary map pieces.
        private FishNet.Object.NetworkObject ResolveNetworkPrefab(GameObject source, string sourceName,
            out NetworkMechanicKind kind)
        {
            var own = FromTransform(source.transform);
            if (own?.NetworkPrefab != null)
            {
                kind = own.NetworkKind;
                return own.NetworkPrefab;
            }

            kind = NetworkMechanics.KindOf(source);
            if (kind != NetworkMechanicKind.None) return source.GetComponent<FishNet.Object.NetworkObject>();

            // Maps saved from a level's own spawned instance can resolve to one of its
            // pieces; the game's network cache still knows the prefab by the saved name.
            string clean = ObjectCatalog.CleanName(sourceName ?? "");
            if (!clean.StartsWith("networked", StringComparison.OrdinalIgnoreCase)) return null;
            var cache = UnityEngine.Object.FindObjectOfType<PostBootstrapGame>()?.GameRefs?
                .BootstrapGlobal?.GlobalRefs?.GameAddressableNetworkedCacheManager;
            var prefab = cache != null ? GetSelectedNetworkPrefab(cache, clean) : null;
            kind = NetworkMechanics.KindOf(prefab?.gameObject);
            return kind != NetworkMechanicKind.None ? prefab : null;
        }

        private void ApplyPlacementOverrides(PlacedObject placed)
        {
            if (placed.NetworkKind == NetworkMechanicKind.BoostPad)
            {
                placed.NetworkBoostForce = NetworkBoostForce;
                placed.NetworkBoostAngle = NetworkBoostAngle;
            }
            else if (placed.NetworkKind == NetworkMechanicKind.Cannon)
            {
                placed.NetworkCannonForce = NetworkCannonForce;
                placed.NetworkCannonAngle = NetworkCannonAngle;
                placed.NetworkCannonAirControlBlock = NetworkCannonAirControlBlock;
            }
        }

        // Map/undo/peer values win; fields the data does not carry keep the defaults.
        private static void ApplyRestoredNetworkSettings(PlacedObject placed, MapObjectData restore)
        {
            var data = new MapObjectData
            {
                NetworkBoostForce = restore.NetworkBoostForce ?? placed.NetworkBoostForce,
                NetworkBoostAngle = restore.NetworkBoostAngle ?? placed.NetworkBoostAngle,
                NetworkCannonForce = restore.NetworkCannonForce ?? placed.NetworkCannonForce,
                NetworkCannonAngle = restore.NetworkCannonAngle ?? placed.NetworkCannonAngle,
                NetworkCannonAirControlBlock = restore.NetworkCannonAirControlBlock ?? placed.NetworkCannonAirControlBlock,
                NetworkBreakDelay = restore.NetworkBreakDelay ?? placed.NetworkBreakDelay,
                NetworkBreakDuration = restore.NetworkBreakDuration ?? placed.NetworkBreakDuration,
                NetworkShatteredDuration = restore.NetworkShatteredDuration ?? placed.NetworkShatteredDuration,
                NetworkShatterImpulse = restore.NetworkShatterImpulse ?? placed.NetworkShatterImpulse,
            };
            NetworkMechanics.Clamp(data);
            placed.CopyNetworkSettingsFrom(data);
        }

        private bool SpawnAsHost(PlacedObject placed, FishNet.Object.NetworkObject prefab,
            Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject spawnerRoot = null;
            FishNet.Object.NetworkObject nob = null;
            try
            {
                var refs = UnityEngine.Object.FindObjectOfType<PostBootstrapGame>()?.GameRefs;
                var server = refs?.FishnetManagers?.ServerManager;
                if (server == null) throw new InvalidOperationException("the FishNet server manager is unavailable");

                // A fresh spawner, never a clone of a level one: a cloned spawner keeps
                // spawnOnEnable and the level's nob, so the game's registry could spawn
                // a second object, or despawn the level's own when the clone is deleted.
                // Built inactive: an enabled spawner registers at once and the registry
                // runs SpawnAssetServer, which fails without an AssetReference. It is
                // activated below, once it already owns its nob.
                spawnerRoot = new GameObject($"NetworkSpawner_{placed.NetworkKind} {ObjectCatalog.CLONE_MARKER}");
                spawnerRoot.SetActive(false);
                spawnerRoot.transform.SetParent(GetMapRoot().transform, false);
                spawnerRoot.transform.SetPositionAndRotation(position, rotation);
                var spawner = NetworkMechanics.AddSpawner(spawnerRoot, placed.NetworkKind);
                spawner.spawnOnEnable = false;
                NetworkMechanics.ConfigureSpawner(spawner, placed);

                // Same order as NetworkSpawnerPoint.SpawnAssetServer. The catalog names
                // the exact prefab variant, which the spawner's AssetReference cannot.
                nob = UnityEngine.Object.Instantiate(prefab, position, rotation);
                nob.transform.localScale = scale;
                server.Spawn(nob.gameObject, null, SceneManager.GetActiveScene());
                spawner.nob = nob;
                spawner.InitializeNobServer(refs);
                spawnerRoot.SetActive(true);

                nob.gameObject.name = $"{ObjectCatalog.CleanName(placed.SourceName)} {ObjectCatalog.CLONE_MARKER}";
                placed.Root = nob.gameObject;
                placed.NetworkSpawnerRoot = spawnerRoot;
                placed.NetworkSpawner = spawner;
                placed.HasCollider = nob.gameObject.GetComponentInChildren<Collider>(true) != null;
                MapEditorPlugin.Logger.LogInfo(
                    $"[NETPLACE] '{placed.SourceName}' spawned as network {NetworkMechanics.Label(placed.NetworkKind)}.");
                return true;
            }
            catch (Exception ex)
            {
                if (nob != null && !nob.IsSpawned) UnityEngine.Object.Destroy(nob.gameObject);
                if (spawnerRoot != null) UnityEngine.Object.Destroy(spawnerRoot);
                MapEditorPlugin.Logger.LogError($"[NETPLACE] Failed to place '{placed.SourceName}': {ex.Message}");
                return false;
            }
        }

        private bool CreateReplicaAnchor(PlacedObject placed, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var anchor = new GameObject($"{ObjectCatalog.CleanName(placed.SourceName)} {ObjectCatalog.CLONE_MARKER}");
            anchor.transform.SetParent(GetMapRoot().transform, false);
            anchor.transform.SetPositionAndRotation(position, rotation);
            anchor.transform.localScale = scale;
            placed.Root = anchor;
            placed.HasCollider = false;
            MapEditorPlugin.Logger.LogInfo(
                $"[NETPLACE] '{placed.SourceName}' is spawned by the host; waiting for its replica.");
            return true;
        }

        private static bool IsNetworkAuthority() => FishNet.InstanceFinder.IsServerStarted;

        // Client side of network placements: parent each host-spawned replica under its
        // anchor so picking, highlight, gizmo and coop sync treat it like any placed
        // object. The host respawns an object (new ObjectId) when its settings change.
        public void UpdateNetworkReplicas()
        {
            if (_remoteNetIds.Count == 0 || Time.unscaledTime < _nextReplicaCheckAt) return;
            _nextReplicaCheckAt = Time.unscaledTime + 0.25f;
            if (IsNetworkAuthority()) return;
            var spawned = FishNet.InstanceFinder.ClientManager?.Objects?.Spawned;
            if (spawned == null) return;

            foreach (var p in _placed)
            {
                if (!p.IsReplicaAnchor || p.Root == null) continue;
                if (!_remoteNetIds.TryGetValue(p.Uid, out int objectId)) continue;
                if (p.Replica != null && p.Replica.ObjectId == objectId) continue;
                DetachReplica(p);
                if (!spawned.TryGetValue(objectId, out var nob) || nob == null) continue;

                var t = nob.transform;
                t.SetParent(p.Root.transform, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                p.Replica = nob;
                p.HasCollider = p.Root.GetComponentInChildren<Collider>(true) != null;
                ApplyPlacementColor(p, p.Tint, null);
                if (EditorConfig.VerboseLogs)
                    MapEditorPlugin.Logger.LogInfo($"[NETPLACE] Adopted replica {objectId} for '{p.SourceName}'.");
            }
        }

        // The replica belongs to FishNet: hand it back to the scene instead of
        // destroying it with our anchor. The host's despawn removes it.
        private static void DetachReplica(PlacedObject placed)
        {
            if (placed.Replica != null) placed.Replica.transform.SetParent(null, true);
            placed.Replica = null;
        }

        // Coop sync carries the host's FishNet ObjectId per placed network object so
        // clients can find the replica. The host derives the map; clients mirror it.
        public Dictionary<string, int> NetworkObjectIds()
        {
            if (!IsNetworkAuthority()) return new Dictionary<string, int>(_remoteNetIds);
            var ids = new Dictionary<string, int>();
            foreach (var p in _placed)
            {
                if (p.NetworkSpawner == null || p.Root == null) continue;
                var nob = p.Root.GetComponent<FishNet.Object.NetworkObject>();
                if (nob != null && nob.IsSpawned) ids[p.Uid] = nob.ObjectId;
            }
            return ids;
        }

        public void SetRemoteNetworkObjectId(string uid, int? objectId)
        {
            if (IsNetworkAuthority() || string.IsNullOrEmpty(uid)) return;
            if (objectId.HasValue) _remoteNetIds[uid] = objectId.Value;
            else _remoteNetIds.Remove(uid);
        }

        // A loaded network prefab by name: the registered prefab asset first (no game
        // error log when absent), then the game's network cache.
        public FishNet.Object.NetworkObject FindNetworkPrefab(string prefabName)
        {
            foreach (var nob in Resources.FindObjectsOfTypeAll<FishNet.Object.NetworkObject>())
            {
                if (nob == null || nob.gameObject.scene.IsValid() || nob.transform.parent != null) continue;
                if (!string.Equals(nob.gameObject.name, prefabName, StringComparison.OrdinalIgnoreCase)) continue;
                if (NetworkMechanics.KindOf(nob.gameObject) != NetworkMechanicKind.None) return nob;
            }
            var cache = UnityEngine.Object.FindObjectOfType<PostBootstrapGame>()?.GameRefs?
                .BootstrapGlobal?.GlobalRefs?.GameAddressableNetworkedCacheManager;
            return cache != null ? GetSelectedNetworkPrefab(cache, prefabName) : null;
        }

        private static FishNet.Object.NetworkObject GetSelectedNetworkPrefab(
            EHS.AddressableNetworkedCacheManager cache, string cleanName)
        {
            string clean = cleanName.Replace("(Clone)", "").Trim();
            string[] keys = { clean + ".prefab", clean, clean.ToLowerInvariant() + ".prefab", clean.ToLowerInvariant() };
            foreach (string key in keys)
            {
                var prefab = cache.GetNetworkedPrefab(key);
                if (prefab != null) return prefab;
            }
            return null;
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

        // Remove networking (FishNet) and loose physics so the clone is plain client-side
        // scenery: no server reconciliation, no falling over. Kinematic bodies stay —
        // they never fall, and the game's synced movers (hammers, moving platforms,
        // magnets...) drive themselves through one. A mechanic keeps every body: a swing
        // hook is a jointed dynamic body that hangs from its anchor.
        private static void StripComponents(GameObject clone)
        {
            bool keepAllBodies = CarriesSceneInitializable(clone)
                || clone.GetComponentInChildren<Joint>(true) != null;
            var toDestroy = new List<Component>();
            var networkObjects = new List<Component>();
            var networkBehaviourType = Il2CppType.Of<FishNet.Object.NetworkBehaviour>();
            var spawnerType = Il2CppType.Of<NetworkSpawnerPoint>();

            foreach (var comp in clone.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                Il2CppSystem.Type type = null;
                try { type = comp.GetIl2CppType(); }
                catch { }
                string fullName = type?.FullName;
                if (fullName == null) continue;

                // NetworkBehaviours are dead without a spawned NetworkObject, and a cloned
                // spawner still points at the level's nob: deleting the clone would make
                // the game's registry despawn the original (GlassShatterSpawner and
                // HighForceShatterSpawner live outside EHS.Interactables). The remaining
                // EHS.Interactables (NPCs, beacons, doors, skin changers) drive game flow;
                // SlideBooster is a purely local surface and keeps working on a clone.
                bool network = fullName.StartsWith("FishNet")
                    || networkBehaviourType.IsAssignableFrom(type)
                    || spawnerType.IsAssignableFrom(type)
                    || (fullName.StartsWith("EHS.Interactables") && fullName != "EHS.Interactables.SlideBooster");
                if (network || LevelFlowComponents.Contains(fullName) || IsLevelFlowFamily(fullName))
                {
                    // NetworkObject must go last: NetworkBehaviours depend on it.
                    if (fullName == "FishNet.Object.NetworkObject") networkObjects.Add(comp);
                    else toDestroy.Add(comp);
                }
                else
                {
                    var body = comp.TryCast<Rigidbody>();
                    if (body != null && !body.isKinematic && !keepAllBodies) toDestroy.Add(comp);
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

        // Movers cloned out of a level keep pointing at the level's own route (a toy-car
        // track, a platform path, a zipline cable), so the clone races off to the
        // original route and seems to vanish. Give the clone its own copy of the route,
        // placed relative to the object exactly as in the source, so it now runs
        // wherever it was put. A synced mover riding on a level mover is detached for
        // the same reason.
        private static void LocalizeExternalReferences(GameObject clone, GameObject source)
        {
            foreach (var mover in clone.GetComponentsInChildren<EHS.Features.SceneObjects.SyncedMovingPlatformSpline>(true))
                mover.splineContainer = LocalizeRoute(mover.splineContainer, clone, source);
            foreach (var zipline in clone.GetComponentsInChildren<EHS.Features.SceneObjects.SceneZiplineSpline>(true))
                zipline.splineContainer = LocalizeRoute(zipline.splineContainer, clone, source);
            foreach (var pipe in clone.GetComponentsInChildren<EHS.Features.SceneObjects.SceneSuckPipeSpline>(true))
                pipe.splineContainer = LocalizeRoute(pipe.splineContainer, clone, source);
            foreach (var animate in clone.GetComponentsInChildren<UnityEngine.Splines.SplineAnimate>(true))
                animate.Container = LocalizeRoute(animate.Container, clone, source);
            foreach (var synced in clone.GetComponentsInChildren<EHS.Features.SceneObjects.SyncedSceneRbBase>(true))
                if (synced.parent != null && !synced.parent.transform.IsChildOf(clone.transform))
                    synced.parent = null;
        }

        private static UnityEngine.Splines.SplineContainer LocalizeRoute(UnityEngine.Splines.SplineContainer route,
            GameObject clone, GameObject source)
        {
            if (route == null || route.transform.IsChildOf(clone.transform)) return route;

            // Only the path data is copied (the Spline objects are shared read-only), not
            // the GameObject: a route often sits on a large chunk of level geometry.
            var copy = new GameObject($"{route.name} Route {ObjectCatalog.CLONE_MARKER}");
            var t = copy.transform;
            t.SetParent(clone.transform, false);
            var src = source.transform;
            t.localPosition = src.InverseTransformPoint(route.transform.position);
            t.localRotation = Quaternion.Inverse(src.rotation) * route.transform.rotation;
            Vector3 srcScale = src.lossyScale, routeScale = route.transform.lossyScale;
            t.localScale = new Vector3(SafeRatio(routeScale.x, srcScale.x),
                SafeRatio(routeScale.y, srcScale.y), SafeRatio(routeScale.z, srcScale.z));
            var container = copy.AddComponent<UnityEngine.Splines.SplineContainer>();
            container.Splines = route.Splines;
            return container;
        }

        private static float SafeRatio(float a, float b) => Mathf.Abs(b) < 1e-5f ? a : a / b;

        // Valid FeatureUniqueIDs are 1..65534. Clones take the top range, far above the
        // ids level designers assign, and never one already used in the scene.
        private const int CloneFeatureIdMin = 40000, CloneFeatureIdMax = 65534;

        // Visual sync (pad animation, zipline effects, mines...) is relayed between
        // players by FeatureUniqueID. A clone keeps its source's id, so the game's
        // registry rejects it as a duplicate and remote players see the level's original
        // react instead. The id is derived from the object's Uid, so every peer gives its
        // own copy of the clone the same id and the relay reaches all of them.
        private static void AssignFeatureIds(GameObject clone, string uid)
        {
            var ids = clone.GetComponentsInChildren<FeatureUniqueID>(true);
            if (ids.Length == 0) return;

            var taken = new HashSet<int>();
            foreach (var other in Resources.FindObjectsOfTypeAll<FeatureUniqueID>())
                if (other != null && other.gameObject.scene.IsValid() && !other.transform.IsChildOf(clone.transform))
                    taken.Add(other.id);

            const int range = CloneFeatureIdMax - CloneFeatureIdMin + 1;
            uint hash = StableHash(uid);
            for (int i = 0; i < ids.Length; i++)
            {
                int id = CloneFeatureIdMin + (int)((hash + (uint)i * 7919u) % range);
                while (taken.Contains(id)) id = id == CloneFeatureIdMax ? CloneFeatureIdMin : id + 1;
                ids[i].id = (ushort)id;
                taken.Add(id);
            }
        }

        // FNV-1a: string.GetHashCode is randomized per process, peers must agree.
        private static uint StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        // The game's scene pad reads boostForce/targetAngle on every touch, so its
        // settings apply live — no respawn as with network pads.
        private void ConfigureScenePad(PlacedObject placed, SceneBoostPad pad, MapObjectData restore)
        {
            placed.ScenePad = pad;
            placed.NetworkBoostForce = restore?.NetworkBoostForce
                ?? (OverrideNetworkMechanicSettings ? NetworkBoostForce : pad.boostForce);
            placed.NetworkBoostAngle = restore?.NetworkBoostAngle
                ?? (OverrideNetworkMechanicSettings ? NetworkBoostAngle : pad.targetAngle);
            ApplyScenePadSettings(placed);
        }

        public static void ApplyScenePadSettings(PlacedObject placed)
        {
            if (placed.ScenePad == null) return;
            placed.ScenePad.boostForce = Mathf.Clamp(placed.NetworkBoostForce ?? placed.ScenePad.boostForce, 0f, 200f);
            placed.ScenePad.targetAngle = Mathf.Clamp(placed.NetworkBoostAngle ?? placed.ScenePad.targetAngle, -89f, 89f);
        }

        // Level-flow triggers riding along on cloned geometry: on a copy they would
        // replay story lines, swap fog/weather/lighting, move the level-progress bar,
        // grant rewards or end the game wherever the copy is placed.
        private static readonly HashSet<string> LevelFlowComponents = new HashSet<string>(StringComparer.Ordinal)
        {
            "EHS.ArchieFallLinesTrigger", "EHS.BackroomJumpScare1", "EHS.BackroomJumpScare2",
            "EHS.BlockCheckpointsTrigger", "EHS.BlockFallLinesTrigger", "EHS.ChangeFogOnStart",
            "EHS.CollectibleCoin", "EHS.GameEndZoneChecker", "EHS.InteractableCheckpoint",
            "EHS.InteractableNarratorSequence", "EHS.InteractableRandomCallerInteract",
            "EHS.LevelInfoHolder", "EHS.MultiplayerOnlyButton", "EHS.RewardUnlockTrigger",
            "EHS.TestingNotifierTrigger", "EHS.UnstuckPoint", "EHS.AnalyticsZone",
            "EHS.AnalyticsLevelZones", "EHS.CharmUnlockTriggerZone",
            "EHS.RespawnZones.InteractableRespawn",
        };

        private static bool IsLevelFlowFamily(string fullName)
            => fullName.StartsWith("EHS.NarratorTrigger", StringComparison.Ordinal)
                || fullName.StartsWith("EHS.Yarn.", StringComparison.Ordinal)
                || fullName.StartsWith("EHS.ChangeLevelProgress", StringComparison.Ordinal)
                || (fullName.StartsWith("EHS.Change", StringComparison.Ordinal)
                    && fullName.EndsWith("OnCollision", StringComparison.Ordinal));

        // A game component wired to an object outside the copy keeps driving or
        // listening to the level's original (its trigger, route or target lives back
        // in the level), which is the usual reason a placed mechanic "does nothing".
        // Reported once per placement so such a case can be fixed from one log line.
        private static void ReportExternalReferences(GameObject clone)
        {
            var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                | Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.NonPublic;
            var unityObject = Il2CppType.Of<UnityEngine.Object>();
            var external = new List<string>();
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var type = mb.GetIl2CppType();
                if (type == null || type.FullName == null || !type.FullName.StartsWith("EHS.", StringComparison.Ordinal)) continue;
                foreach (var field in type.GetFields(flags))
                {
                    if (!unityObject.IsAssignableFrom(field.FieldType)) continue;
                    var target = field.GetValue(mb)?.TryCast<UnityEngine.Object>();
                    Transform targetT = target?.TryCast<Component>()?.transform ?? target?.TryCast<GameObject>()?.transform;
                    // Only scene objects can be "outside"; assets (SOs, prefabs) are shared by design.
                    if (targetT == null || !targetT.gameObject.scene.IsValid() || targetT.IsChildOf(clone.transform)) continue;
                    external.Add($"{type.Name}.{field.Name} -> {ObjectCatalog.BuildPath(targetT)}");
                }
            }
            if (external.Count == 0) return;
            MapEditorPlugin.Logger.LogWarning(
                $"[PLACE] '{clone.name}' is wired to {external.Count} object(s) outside the copy: " +
                string.Join("; ", external.GetRange(0, Math.Min(external.Count, 6))));
        }

        // RespawnOnTouch registers itself on OnEnable and the game's registry runs its
        // ScenePostInit (which wires respawnZones) right away. A zone disabled on the
        // source never registers, so enabling it before activation is all it needs;
        // calling ScenePostInit by hand would subscribe its trigger a second time.
        private static void EnableRespawnZones(GameObject clone)
        {
            foreach (var zone in clone.GetComponentsInChildren<EHS.RespawnZones.RespawnOnTouch>(true))
                if (zone != null) zone.enabled = true;
        }

        // True when the copy carries a game mechanic the game initializes in
        // ScenePostInit. Ground also implements the interface, but only describes the
        // surface of ordinary blocks; it does not make a block a mechanic.
        private static bool CarriesSceneInitializable(GameObject root)
        {
            var sceneInitializable = Il2CppType.Of<ISceneInitializable>();
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var type = mb.GetIl2CppType();
                if (type == null || type.FullName == "EHS.Ground.Ground") continue;
                if (sceneInitializable.IsAssignableFrom(type)) return true;
            }
            return false;
        }

        // Behaviours that drive a mechanic: the scene-initialized ones plus the older
        // self-moving components that run straight from Update/FixedUpdate.
        private static readonly HashSet<string> SelfMovingComponents = new HashSet<string>(StringComparer.Ordinal)
        {
            "EHS.MovePlatform", "EHS.RingPlatform", "EHS.AutoRotate", "EHS.RigidbodyLauncher",
        };

        private static bool IsMechanicBehaviour(Il2CppSystem.Type type, Il2CppSystem.Type sceneInitializable)
        {
            if (type == null || type.FullName == "EHS.Ground.Ground") return false;
            return sceneInitializable.IsAssignableFrom(type) || SelfMovingComponents.Contains(type.FullName);
        }

        // Duplicating a frozen copy would copy it frozen: its mechanic behaviours
        // disabled and its bodies kinematic, so the duplicate would never run. Put the
        // duplicate back in the source's running configuration (same hierarchy order).
        private void RestoreRunningStateOfFrozenSource(GameObject source, GameObject clone)
        {
            var own = FromTransform(source.transform);
            if (own == null || !own.Frozen) return;

            var frozen = new HashSet<int>();
            foreach (var behaviour in own.FrozenBehaviours) if (behaviour != null) frozen.Add(behaviour.GetInstanceID());
            var sourceBehaviours = source.GetComponentsInChildren<MonoBehaviour>(true);
            var cloneBehaviours = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < sourceBehaviours.Length && i < cloneBehaviours.Length; i++)
                if (sourceBehaviours[i] != null && frozen.Contains(sourceBehaviours[i].GetInstanceID()))
                    cloneBehaviours[i].enabled = true;

            var bodies = new Dictionary<int, (bool kinematic, RigidbodyInterpolation interpolation)>();
            foreach (var (body, wasKinematic, interpolation) in own.FrozenBodies)
                if (body != null) bodies[body.GetInstanceID()] = (wasKinematic, interpolation);
            var sourceBodies = source.GetComponentsInChildren<Rigidbody>(true);
            var cloneBodies = clone.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < sourceBodies.Length && i < cloneBodies.Length; i++)
            {
                if (sourceBodies[i] == null || !bodies.TryGetValue(sourceBodies[i].GetInstanceID(), out var state)) continue;
                cloneBodies[i].isKinematic = state.kinematic;
                cloneBodies[i].interpolation = state.interpolation;
            }
        }

        private static void MarkArmed(PlacedObject placed)
        {
            var t = placed.Root.transform;
            placed.ArmedPos = t.position;
            placed.ArmedRot = t.rotation;
            placed.ArmedScale = t.localScale;
        }

        private static List<(Transform, Vector3, Quaternion, Vector3)> CaptureAuthoredParts(GameObject clone)
        {
            var parts = new List<(Transform, Vector3, Quaternion, Vector3)>();
            foreach (var part in clone.GetComponentsInChildren<Transform>(true))
                if (part != null && part != clone.transform)
                    parts.Add((part, part.localPosition, part.localRotation, part.localScale));
            return parts;
        }

        // A joint with no connected body hangs from a fixed WORLD point (a swing hook's
        // pivot). The copy would keep hanging from the level original's pivot; express
        // it in the source's space so it follows the copy wherever it goes.
        private static List<(Joint, Vector3)> CaptureWorldAnchors(GameObject clone, GameObject source)
        {
            List<(Joint, Vector3)> anchors = null;
            foreach (var joint in clone.GetComponentsInChildren<Joint>(true))
            {
                if (joint == null || joint.connectedBody != null) continue;
                (anchors ??= new List<(Joint, Vector3)>()).Add(
                    (joint, source.transform.InverseTransformPoint(joint.connectedAnchor)));
            }
            return anchors;
        }

        private static void ApplyWorldAnchors(GameObject clone, List<(Joint joint, Vector3 localAnchor)> anchors)
        {
            if (anchors == null) return;
            foreach (var (joint, localAnchor) in anchors)
            {
                if (joint == null) continue;
                joint.autoConfigureConnectedAnchor = false;
                joint.connectedAnchor = clone.transform.TransformPoint(localAnchor);
            }
        }

        // Editor = frozen mechanics, Play/Off = running ones. Called every frame; only
        // clones whose state differs are touched, so new placements join right away.
        public void SetMechanicsRunning(bool running, GameObject player)
        {
            bool released = false;
            foreach (var p in _placed)
            {
                if (!p.RearmOnMove || p.Root == null) continue;
                if (running && p.Frozen) Thaw(p);
                else if (!running && !p.Frozen)
                {
                    // A frozen mechanic stops driving the player without letting go.
                    if (!released) { PlayerAttachments.ReleaseAll(player); released = true; }
                    Freeze(p);
                }
            }
        }

        // Stops the mechanic and puts every part back at its authored pose, so the
        // gizmo moves the object freely and nothing animates while editing.
        private static void Freeze(PlacedObject p)
        {
            var root = p.Root.transform;
            root.SetPositionAndRotation(p.ArmedPos, p.ArmedRot);
            root.localScale = p.ArmedScale;
            if (p.AuthoredParts != null)
                foreach (var (part, pos, rot, scale) in p.AuthoredParts)
                {
                    if (part == null) continue;
                    part.localPosition = pos;
                    part.localRotation = rot;
                    part.localScale = scale;
                }

            // Disabling runs the game's SceneDeInit (registry Unregister on OnDisable).
            var sceneInitializable = Il2CppType.Of<ISceneInitializable>();
            p.FrozenBehaviours = new List<Behaviour>();
            foreach (var mb in p.Root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || !mb.enabled || !IsMechanicBehaviour(mb.GetIl2CppType(), sceneInitializable)) continue;
                mb.enabled = false;
                p.FrozenBehaviours.Add(mb);
            }
            // Kinematic and not interpolated: the gizmo's transform writes stick.
            p.FrozenBodies = new List<(Rigidbody, bool, RigidbodyInterpolation)>();
            foreach (var body in p.Root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body == null) continue;
                p.FrozenBodies.Add((body, body.isKinematic, body.interpolation));
                body.isKinematic = true;
                body.interpolation = RigidbodyInterpolation.None;
            }
            p.Frozen = true;
        }

        // Runs the mechanic from wherever the object now is: re-enabling registers each
        // behaviour again and the game's ScenePostInit bakes the current (authored) pose.
        private static void Thaw(PlacedObject p)
        {
            MarkArmed(p);
            ApplyWorldAnchors(p.Root, p.WorldAnchors);
            // A swing hook takes its rest point once, in Awake, which never runs again:
            // re-take it here or the moved hook keeps pulling back to where it was.
            foreach (var hook in p.Root.GetComponentsInChildren<Hook>(true))
            {
                if (hook == null) continue;
                var rest = hook.transform.position;
                hook.initialHookPos = rest;
                hook.currentHookAnchorPos = rest;
                hook.previousHookAnchorPos = rest;
                hook.ResetHookPos();
            }
            if (p.FrozenBodies != null)
                foreach (var (body, kinematic, interpolation) in p.FrozenBodies)
                {
                    if (body == null) continue;
                    body.isKinematic = kinematic;
                    body.interpolation = interpolation;
                }
            if (p.FrozenBehaviours != null)
                foreach (var behaviour in p.FrozenBehaviours)
                    if (behaviour != null) behaviour.enabled = true;
            p.FrozenBodies = null;
            p.FrozenBehaviours = null;
            p.Frozen = false;
        }

        // A peer moved a clone whose mechanic is running (editor closed): restart it at
        // the new pose instead of letting the next freeze snap it back to the old one.
        public void SetAuthoredPose(PlacedObject p, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (!p.RearmOnMove || p.Frozen)
            {
                p.Root.transform.SetPositionAndRotation(position, rotation);
                p.Root.transform.localScale = scale;
                return;
            }
            Freeze(p);
            p.Root.transform.SetPositionAndRotation(position, rotation);
            p.Root.transform.localScale = scale;
            Thaw(p);
        }

        private void ApplyPlacementColor(PlacedObject placed, TintColor tint, MapObjectData restore)
        {
            var custom = restore?.CustomColor ?? placed.CustomColor;
            if (custom != null && custom.Length >= 3)
                ApplyCustomColor(placed, new Color(custom[0], custom[1], custom[2]));
            else if (tint != TintColor.None)
                ApplyTint(placed, tint);
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
        private const float MaxBackfillColliderSize = 40f;

        private static void EnsureColliders(GameObject root)
        {
            try
            {
                if (root == null) return;
                // Raw Addressable prefabs often contain disabled colliders, colliders
                // under inactive pool branches, or trigger-only interaction volumes.
                // Placement requires an active, enabled, non-trigger collider.
                int usableSolids = 0;
                int disabledSolids = 0;
                int triggers = 0;
                foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null) continue;
                    if (collider.isTrigger) { triggers++; continue; }
                    if (!collider.enabled || !collider.gameObject.activeInHierarchy)
                    {
                        disabledSolids++;
                        collider.enabled = true;
                        for (var current = collider.transform;
                             current != null && current != root.transform;
                             current = current.parent)
                            current.gameObject.SetActive(true);
                    }
                    if (collider.gameObject.layer == HiddenLayer.Layer)
                        collider.gameObject.layer = 0;
                    if (collider.enabled && collider.gameObject.activeInHierarchy) usableSolids++;
                }
                if (usableSolids > 0)
                {
                    if (disabledSolids > 0 || EditorConfig.VerboseLogs)
                        MapEditorPlugin.Logger.LogInfo(
                            $"[PLACE] '{root.name}' physics normalized: {usableSolids} usable solid, " +
                            $"{disabledSolids} re-enabled, {triggers} trigger collider(s).");
                    return;
                }

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
                // A visual this large is effects or a whole set piece (one clone measured
                // 70m across): a solid box would bury the player in an invisible wall.
                if (Mathf.Max(la.size.x, Mathf.Max(la.size.y, la.size.z)) > MaxBackfillColliderSize)
                {
                    MapEditorPlugin.Logger.LogInfo(
                        $"[PLACE] '{root.name}' has no solid collider; its {la.size.x:0.#}x{la.size.y:0.#}x{la.size.z:0.#} " +
                        "visual is too large for a backfilled box, left without one.");
                    return;
                }

                var bc = root.AddComponent<BoxCollider>();
                bc.center = la.center;
                bc.size = la.size;
                if (root.layer == HiddenLayer.Layer) root.layer = 0;

                MapEditorPlugin.Logger.LogInfo(
                    $"[PLACE] '{root.name}' had no usable solid collider " +
                    $"({triggers} trigger-only) — added 1 box collider " +
                    $"(AABB {la.size.x:0.#}x{la.size.y:0.#}x{la.size.z:0.#}).");
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
                if (placed.NetworkSpawner != null)
                {
                    try { placed.NetworkSpawner.NetworkSceneDeInit(); }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[NETPLACE] DeInit failed: {ex.Message}"); }
                    if (placed.NetworkSpawnerRoot != null) UnityEngine.Object.Destroy(placed.NetworkSpawnerRoot);
                }
                else
                {
                    DetachReplica(placed);
                    DestroyWithUnregister(placed.Root);
                }
            }
        }

        public void WipeAll()
        {
            foreach (var p in _placed)
            {
                if (p.NetworkSpawner != null)
                {
                    try { p.NetworkSpawner.NetworkSceneDeInit(); } catch { }
                    if (p.NetworkSpawnerRoot != null) UnityEngine.Object.Destroy(p.NetworkSpawnerRoot);
                }
                else
                {
                    DetachReplica(p);
                    DestroyWithUnregister(p.Root);
                }
            }
            _placed.Clear();
            _byRootId.Clear();
        }

        // CRITICAL: Unity's bug — destroying a collider that the player is touching
        // also fails to fire OnCollisionExit, leaving the game's "grounded" tracker
        // stuck on a collider that no longer exists. Same root cause as Hide() with
        // c.enabled = false, but for the Destroy path used by map change (WipeAll)
        // and single-object delete. Mirror the fix: Unregister first, then destroy.
        private static void DestroyWithUnregister(GameObject root)
        {
            if (root == null) return;
            try
            {
                foreach (var c in root.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null) continue;
                    GroundRegistrar.Unregister(c);
                }
            }
            catch { }
            UnityEngine.Object.Destroy(root);
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
