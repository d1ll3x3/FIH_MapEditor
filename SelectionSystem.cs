using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    public class Selection
    {
        public PlacedObject Placed;   // set when the selection is one of our clones
        public GameObject Raw;        // set when "Unlock" selected an original scene object
        public string Marker;         // "goal" | "spawn" | "checkpoint" | "reset"
        public int MarkerIndex;       // which checkpoint / reset zone in the map's list

        public bool IsPlaced => Placed != null;
        public bool IsRaw => Raw != null;
        public bool IsMarker => Marker != null;
        public GameObject Target => Placed?.Root ?? Raw;   // null for markers
        public bool IsValid => Target != null || IsMarker;
        public string DisplayName => Marker == "goal" ? "Goal zone"
            : Marker == "spawn" ? "Spawn point"
            : Marker == "checkpoint" ? $"Checkpoint #{MarkerIndex + 1}"
            : Marker == "reset" ? $"Reset trigger #{MarkerIndex + 1}"
            : Marker == "cannontarget" ? $"Launch target (obj #{MarkerIndex:000})"
            : Marker == "cannonlaunch" ? $"Launch point (obj #{MarkerIndex:000})"
            : Placed != null ? Placed.Root.name
            : (Raw != null ? Raw.name : "");
    }

    // Mouse raycast picking. Uses RaycastAll + nearest-hit so we never depend on the
    // out-param Physics.Raycast interop signature.
    public class SelectionSystem
    {
        private readonly GameObjectFinder _finder;
        private readonly PlacedObjectManager _placedManager;
        private readonly ObjectCatalog _catalog;

        public Selection Current { get; private set; } = new Selection();

        // Ctrl+Click multi-selection: placed clones and level objects only (no markers).
        // Current always mirrors the last-clicked member while active.
        public readonly List<Selection> Multi = new List<Selection>();
        public bool IsMulti => Multi.Count > 1;

        public SelectionSystem(GameObjectFinder finder, PlacedObjectManager placedManager, ObjectCatalog catalog)
        {
            _finder = finder;
            _placedManager = placedManager;
            _catalog = catalog;
        }

        // Group-aware: selecting any member of a persistent group brings in every other
        // member as a multi-selection, so a click always reselects the whole group.
        public void Select(PlacedObject placed)
        {
            if (placed?.GroupId != null)
            {
                var members = new List<Selection>();
                foreach (var p in _placedManager.Placed)
                    if (p.GroupId == placed.GroupId) members.Add(new Selection { Placed = p });
                if (members.Count > 1)
                {
                    Multi.Clear();
                    Multi.AddRange(members);
                    Current = new Selection { Placed = placed };
                    return;
                }
            }
            Multi.Clear();
            Current = new Selection { Placed = placed };
        }

        public void SelectMarker(string marker, int index = 0)
        {
            Multi.Clear();
            Current = new Selection { Marker = marker, MarkerIndex = index };
        }

        public void SelectRaw(GameObject go)
        {
            Multi.Clear();
            Current = new Selection { Raw = go };
        }

        public void Deselect()
        {
            Multi.Clear();
            Current = new Selection();
        }

        // Ctrl-click path: Current was just re-picked; toggle its membership, seeding
        // the group with whatever was selected before.
        public void CtrlMerge(Selection previous)
        {
            var cur = Current;
            if (!cur.IsValid || cur.IsMarker) return;

            if (Multi.Count == 0 && previous != null && previous.IsValid && !previous.IsMarker
                && previous.Target != cur.Target)
                Multi.Add(previous);

            var existing = Multi.Find(m => m.Target == cur.Target);
            if (existing != null)
            {
                Multi.Remove(existing);
                Current = Multi.Count > 0 ? Multi[Multi.Count - 1] : new Selection();
            }
            else
            {
                Multi.Add(cur);
            }

            if (Multi.Count == 1)
            {
                Current = Multi[0];
                Multi.Clear();
            }
        }

        // Replace the whole group programmatically (e.g. after a group duplicate).
        public void SetMulti(List<Selection> members)
        {
            Multi.Clear();
            if (members != null) Multi.AddRange(members);
            Current = Multi.Count > 0 ? Multi[Multi.Count - 1] : new Selection();
            if (Multi.Count == 1) Multi.Clear();
        }

        // Drop members whose objects were destroyed; collapse to single when only one left.
        public void PruneMulti()
        {
            if (Multi.Count == 0) return;
            Multi.RemoveAll(m => m.Target == null);
            if (Multi.Count == 1)
            {
                Current = Multi[0];
                Multi.Clear();
            }
            else if (Multi.Count == 0 && Current.Target == null && !Current.IsMarker)
            {
                Current = new Selection();
            }
        }

        // Returns the world point hit (for stamp placement) even when nothing selectable
        // was there. unlockOriginals lets clicks resolve original scene geometry too;
        // pickInvisible additionally lets clicks land on triggers and renderless
        // colliders (kill zones, invisible walls) instead of passing through to what
        // the user actually sees. additive (Ctrl+Click) only replaces Current — it must
        // NOT clear Multi (CtrlMerge builds on it right after) nor expand groups.
        public bool PickAtMouse(bool unlockOriginals, bool pickInvisible, out Vector3 hitPoint,
                                bool additive = false)
        {
            hitPoint = Vector3.zero;
            try
            {
                var cam = Camera.main;
                if (cam == null) return false;

                var ray = cam.ScreenPointToRay(Input.mousePosition);
                var playerRoot = _finder.FindPlayer()?.transform?.root;

                // Physics pass: nearest collider hit that isn't the player or a helper.
                float bestDist = float.MaxValue;
                Transform bestTransform = null;
                Vector3 bestPoint = Vector3.zero;

                // Visibility verdicts are cached per logical root: several hits of one
                // click often share the same object.
                var visCache = new System.Collections.Generic.Dictionary<int, bool>();

                var hits = Physics.RaycastAll(ray, 1000f);
                if (hits != null)
                {
                    foreach (var hit in hits)
                    {
                        var col = hit.collider;
                        if (col == null) continue;
                        var t = col.transform;
                        if (playerRoot != null && t.root == playerRoot) continue;
                        if (t.name.StartsWith("FIH_Line") || t.name.StartsWith("FIH_Gizmo")) continue;
                        if (!pickInvisible)
                        {
                            if (col.isTrigger) continue;
                            if (!IsVisible(t, visCache)) continue;
                        }
                        if (hit.distance < bestDist)
                        {
                            bestDist = hit.distance;
                            bestTransform = t;
                            bestPoint = hit.point;
                        }
                    }
                }

                // Bounds pass: collider-less objects are invisible to physics raycasts,
                // so they're tested against their AABBs. Priority rules:
                //  - our own collider-less clones compete with physics hits by distance
                //    (the user placed them and must be able to reselect them), but
                //  - scene decor (SM_* visual-only meshes) NEVER beats a collider hit —
                //    an AABB always starts before the real surface behind it, so letting
                //    it compete would hijack almost every click. Decor is last resort.
                Transform boundsTransform = null;
                float boundsDist = bestDist;

                foreach (var p in _placedManager.Placed)
                {
                    if (p.Root == null || p.HasCollider) continue;
                    var b = ObjectCatalog.ComputeBounds(p.Root);
                    TestBounds(p.Root.transform, b, ray, ref boundsDist, ref boundsTransform);
                }

                if (boundsTransform == null && bestTransform == null)
                {
                    foreach (var decor in _catalog.ColliderlessRoots)
                    {
                        if (decor.Root == null || !decor.Root.activeInHierarchy) continue;
                        TestBounds(decor.Root.transform, decor.Bounds, ray, ref boundsDist, ref boundsTransform);
                    }
                }

                if (boundsTransform != null)
                {
                    bestDist = boundsDist;
                    bestTransform = boundsTransform;
                    bestPoint = ray.GetPoint(boundsDist);
                }

                if (bestTransform == null) return false;

                hitPoint = bestPoint;

                var placed = _placedManager.FromTransform(bestTransform);
                if (placed != null)
                {
                    if (additive) Current = new Selection { Placed = placed };
                    else Select(placed); // group-aware
                    return true;
                }

                if (unlockOriginals)
                {
                    // Resolve to the same logical root the catalog scan would pick.
                    var root = ClimbToLogicalRoot(bestTransform);
                    if (!additive) Multi.Clear();
                    Current = new Selection { Raw = root.gameObject };
                    LogRawPickDebug(bestTransform, root);
                    return true;
                }

                return true; // hit the world but nothing selectable — hitPoint is still useful
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[SELECT] Raycast error: {ex.Message}");
                return false;
            }
        }

        // An object counts as visible when its logical root has at least one enabled
        // renderer (ComputeBounds already ignores disabled ones). Our own clones are
        // always pickable regardless.
        private bool IsVisible(Transform t, System.Collections.Generic.Dictionary<int, bool> cache)
        {
            try
            {
                if (_placedManager.FromTransform(t) != null) return true;

                var root = ClimbToLogicalRoot(t);
                int id = root.GetInstanceID();
                if (cache.TryGetValue(id, out bool cached)) return cached;

                bool visible = ObjectCatalog.ComputeBounds(root.gameObject).size != Vector3.zero;
                cache[id] = visible;
                return visible;
            }
            catch
            {
                return true; // never let the visibility check eat a click on error
            }
        }

        private static void TestBounds(Transform t, Bounds bounds, Ray ray,
                                       ref float bestDist, ref Transform best)
        {
            if (bounds.size == Vector3.zero) return;
            // Level-chunk-sized AABBs would hijack every click when the camera is
            // inside them; leave those to the physics pass / catalog.
            if (bounds.size.x > 60f || bounds.size.y > 60f || bounds.size.z > 60f) return;

            if (!RayIntersectsAABB(ray, bounds, out float dist)) return;
            if (dist <= 0.05f || dist >= 1000f) return;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = t;
            }
        }

        // Oriented-box ray test: rotate the ray into the box's local frame and reuse
        // the slab test (rotation preserves distances).
        public static bool RayIntersectsOBB(Ray ray, Vector3 center, Vector3 size, Quaternion rot, out float dist)
        {
            var inv = Quaternion.Inverse(rot);
            var localRay = new Ray(inv * (ray.origin - center) + center, inv * ray.direction);
            return RayIntersectsAABB(localRay, new Bounds(center, size), out dist);
        }

        // Pure-managed slab test; avoids relying on the Bounds.IntersectRay interop
        // out-param overload. Public: the controller also uses it to click-test the
        // goal/spawn marker boxes, which have no colliders.
        public static bool RayIntersectsAABB(Ray ray, Bounds b, out float dist)
        {
            dist = 0f;
            Vector3 o = ray.origin, d = ray.direction;
            Vector3 min = b.min, max = b.max;
            float tmin = 0f, tmax = float.MaxValue;

            for (int axis = 0; axis < 3; axis++)
            {
                float dirComp = axis == 0 ? d.x : axis == 1 ? d.y : d.z;
                float oriComp = axis == 0 ? o.x : axis == 1 ? o.y : o.z;
                float minComp = axis == 0 ? min.x : axis == 1 ? min.y : min.z;
                float maxComp = axis == 0 ? max.x : axis == 1 ? max.y : max.z;

                if (Mathf.Abs(dirComp) < 1e-8f)
                {
                    if (oriComp < minComp || oriComp > maxComp) return false;
                    continue;
                }

                float inv = 1f / dirComp;
                float t1 = (minComp - oriComp) * inv;
                float t2 = (maxComp - oriComp) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return false;
            }

            dist = tmin;
            return true;
        }

        // Hierarchy dump on every raw (level-object) pick: which node the ray hit, what
        // root the climb resolved, and what lives around it. This is the evidence trail
        // for objects whose visuals and colliders the game keeps in separate branches.
        private static void LogRawPickDebug(Transform hit, Transform root)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[SELECT] raw pick hit='{ObjectCatalog.BuildPath(hit)}' root='{ObjectCatalog.BuildPath(root)}'");
                var p = root.parent;
                if (p != null)
                {
                    sb.AppendLine($"[SELECT] climb into parent '{p.name}'? {CanClimbInto(p, null)} — children ({p.childCount}):");
                    for (int i = 0; i < p.childCount && i < 24; i++)
                        sb.AppendLine($"    - {p.GetChild(i).name} {DescribeNode(p.GetChild(i))}");
                }
                sb.AppendLine($"[SELECT] root children ({root.childCount}):");
                for (int i = 0; i < root.childCount && i < 24; i++)
                    sb.AppendLine($"    - {root.GetChild(i).name} {DescribeNode(root.GetChild(i))}");
                MapEditorPlugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static string DescribeNode(Transform t)
        {
            try
            {
                int r = t.GetComponentsInChildren<Renderer>(true).Length;
                int c = t.GetComponentsInChildren<Collider>(true).Length;
                bool rb = t.GetComponentInChildren<Rigidbody>(true) != null;
                return $"[renderers:{r} colliders:{c}{(rb ? " rigidbody" : "")}{(t.gameObject.activeInHierarchy ? "" : " INACTIVE")}]";
            }
            catch { return "[?]"; }
        }

        // Public: the invisible-collider visualizer groups colliders by the same root.
        public static Transform ClimbToLogicalRoot(Transform t)
        {
            var root = t;
            var cache = new Dictionary<int, int>();
            while (root.parent != null)
            {
                var p = root.parent;
                if (p.GetComponent<Renderer>() != null || p.GetComponent<Collider>() != null)
                {
                    root = p;
                    continue;
                }
                if (CanClimbInto(p, cache)) { root = p; continue; }
                break;
            }
            return root;
        }

        // Whether `p` is still part of the SAME logical object as its children — i.e.
        // the root climb should continue through it. Shared by selection picking and
        // the catalog scan so both always agree on what "one object" means.
        // Cache stores 1 = climb, 0 = stop, keyed by transform instance id.
        public static bool CanClimbInto(Transform p, Dictionary<int, int> verdictCache)
        {
            int id = p.GetInstanceID();
            if (verdictCache != null && verdictCache.TryGetValue(id, out int v)) return v == 1;
            bool climb = ComputeCanClimbInto(p);
            if (verdictCache != null) verdictCache[id] = climb ? 1 : 0;
            return climb;
        }

        private static bool ComputeCanClimbInto(Transform p)
        {
            // An LODGroup's children (LOD0/LOD1/... plus collision nodes) are by
            // definition one object — grabbing a single LOD would tear it apart.
            try { if (p.GetComponent<LODGroup>() != null) return true; } catch { }

            int rendererBranches = 0;
            bool rendererBranchesAllLod = true;
            bool hasVisualBounds = false;
            Bounds visualBounds = default;
            List<Transform> colliderOnly = null;
            for (int i = 0; i < p.childCount; i++)
            {
                var child = p.GetChild(i);
                if (child.GetComponentInChildren<Renderer>(true) != null)
                {
                    rendererBranches++;
                    if (child.name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) < 0)
                        rendererBranchesAllLod = false;
                    var vb = ObjectCatalog.ComputeBounds(child.gameObject);
                    if (vb.size != Vector3.zero)
                    {
                        if (!hasVisualBounds) { visualBounds = vb; hasVisualBounds = true; }
                        else visualBounds.Encapsulate(vb);
                    }
                }
                else if (child.GetComponentInChildren<Collider>(true) != null)
                {
                    (colliderOnly ??= new List<Transform>()).Add(child);
                }
            }
            int colliderOnlyBranches = colliderOnly?.Count ?? 0;

            // Several distinct visible children = a container (level chunk, prop group)
            // — unless they're just LOD variants of one mesh (no LODGroup component).
            if (rendererBranches > 1 && !rendererBranchesAllLod) return false;

            // Pure wrapper: a single renderable branch and nothing else. Always climb.
            if (colliderOnlyBranches == 0 && rendererBranches == 1) return true;
            if (colliderOnlyBranches == 0 && rendererBranches == 0) return false; // empty node

            // Visual + collision split (the game keeps SM_* meshes and their collision
            // nodes as siblings): merge ONLY when every collider-only sibling actually
            // overlaps the visual — a collision proxy wraps its mesh; an independent
            // invisible wall that merely shares the parent does not, and merging it
            // would swallow the visible object out of the catalog and the selection.
            if (hasVisualBounds && colliderOnly != null)
            {
                var probe = visualBounds;
                probe.Expand(1.5f);
                foreach (var branch in colliderOnly)
                {
                    var cb = MechanicsController.ComputeColliderBounds(branch.gameObject);
                    if (cb.size == Vector3.zero) continue;
                    if (!probe.Intersects(cb)) return false;
                }
            }

            // Chunk-size guard: never merge anything level-container sized.
            var bounds = ObjectCatalog.ComputeBounds(p.gameObject);
            if (bounds.size == Vector3.zero)
                bounds = MechanicsController.ComputeColliderBounds(p.gameObject);
            if (bounds.size == Vector3.zero) return false;
            return bounds.size.x <= 60f && bounds.size.y <= 60f && bounds.size.z <= 60f;
        }
    }
}
