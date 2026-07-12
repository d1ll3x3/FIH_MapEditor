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
            : Marker == "ball" ? "Soccer ball (kickoff)"
            : Marker == "soccergoal" ? $"Soccer goal #{MarkerIndex + 1}"
            : Marker == "scoreboard" ? "Scoreboard"
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
        //
        // Overlap handling: clicking the SAME spot again within 1.5s cycles to the next
        // candidate (grass → the ground behind it → back), so overlapping objects are
        // all reachable. Small collider-less decor (grass tufts, plants ≤8m) may WIN
        // over a physics hit when its box is closer along the ray — that's what makes
        // grass selectable at all; big decor stays a last resort so it can't hijack
        // clicks aimed at the world.
        public Action<string> OnPickHint;   // wired to EditorController.ShowToast

        private Vector2 _lastPickScreenPos;
        private float _lastPickAt = -99f;
        private Transform _lastPickChoice;
        private float _lastHintAt = -99f;

        private const float DECOR_PRIORITY_MAX_SIZE = 8f;   // per axis
        private const float PICK_CYCLE_SECONDS = 1.5f;
        private const float PICK_CYCLE_PIXELS = 8f;

        public bool PickAtMouse(bool unlockOriginals, bool pickInvisible, out Vector3 hitPoint,
                                bool additive = false)
        {
            hitPoint = Vector3.zero;
            try
            {
                var cam = Camera.main;
                if (cam == null) return false;

                Vector2 screenPos = Input.mousePosition;
                var ray = cam.ScreenPointToRay(Input.mousePosition);

                // Same-spot re-click: exclude last time's winner so the next candidate
                // behind it gets its turn. Falls back to a fresh pick when the exclusion
                // leaves nothing (wrap-around).
                bool sameSpot = !additive
                    && Time.unscaledTime - _lastPickAt < PICK_CYCLE_SECONDS
                    && (screenPos - _lastPickScreenPos).sqrMagnitude <= PICK_CYCLE_PIXELS * PICK_CYCLE_PIXELS
                    && _lastPickChoice != null;
                Transform exclude = sameSpot ? _lastPickChoice : null;

                if (!ResolvePick(ray, unlockOriginals, pickInvisible, exclude,
                        out Transform bestTransform, out Vector3 bestPoint, out float bestDist)
                    && exclude != null)
                {
                    // Nothing besides the excluded object here: cycle wraps around.
                    ResolvePick(ray, unlockOriginals, pickInvisible, null,
                        out bestTransform, out bestPoint, out bestDist);
                }

                if (bestTransform == null) return false;

                hitPoint = bestPoint;

                var placed = _placedManager.FromTransform(bestTransform);
                if (placed != null)
                {
                    RememberPick(screenPos, placed.Root.transform, additive);
                    if (additive) Current = new Selection { Placed = placed };
                    else Select(placed); // group-aware
                    LogColliderPhysics(placed.Root, "clone");
                    return true;
                }

                // Resolve to the same logical root the catalog scan would pick.
                var root = ClimbToLogicalRoot(bestTransform);
                if (unlockOriginals)
                {
                    RememberPick(screenPos, root, additive);
                    if (!additive) Multi.Clear();
                    Current = new Selection { Raw = root.gameObject };
                    LogRawPickDebug(bestTransform, root);
                    LogColliderPhysics(root.gameObject, "original");
                    return true;
                }

                // Hit level geometry with Unlock OFF: the click "does nothing" silently,
                // which reads as "I can't select X". Say why (throttled).
                if (Time.unscaledTime - _lastHintAt > 2f)
                {
                    _lastHintAt = Time.unscaledTime;
                    OnPickHint?.Invoke($"Level object \"{root.name}\" — turn Unlock ON to select level geometry");
                }
                return true; // hit the world but nothing selectable — hitPoint is still useful
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[SELECT] Raycast error: {ex.Message}");
                return false;
            }
        }

        private void RememberPick(Vector2 screenPos, Transform choice, bool additive)
        {
            if (additive) return;   // Ctrl+Click builds a set; cycling there is confusing
            _lastPickScreenPos = screenPos;
            _lastPickAt = Time.unscaledTime;
            _lastPickChoice = choice;
        }

        // One full pick resolution: physics hits + collider-less bounds candidates,
        // optionally excluding one object (the previous winner, for click-cycling).
        private bool ResolvePick(Ray ray, bool unlockOriginals, bool pickInvisible, Transform exclude,
                                 out Transform bestTransform, out Vector3 bestPoint, out float bestDist)
        {
            bestTransform = null;
            bestPoint = Vector3.zero;
            bestDist = float.MaxValue;

            var playerRoot = _finder.FindPlayer()?.transform?.root;

            // Visibility verdicts are cached per logical root: several hits of one
            // click often share the same object.
            var visCache = new Dictionary<int, bool>();

            // Discard log (change: no more silent dead clicks) — first few reasons only.
            var discarded = new List<string>();

            // Physics pass: nearest collider hit that isn't the player or a helper.
            var hits = Physics.RaycastAll(ray, 1000f);
            int rawHitCount = hits?.Length ?? 0;
            if (hits != null)
            {
                foreach (var hit in hits)
                {
                    var col = hit.collider;
                    if (col == null) continue;
                    var t = col.transform;
                    if (playerRoot != null && t.root == playerRoot) { Note(discarded, t, "player"); continue; }
                    if (t.name.StartsWith("FIH_Line") || t.name.StartsWith("FIH_Gizmo")) continue;
                    if (exclude != null && (t == exclude || t.IsChildOf(exclude))) continue;
                    if (!pickInvisible)
                    {
                        if (col.isTrigger) { Note(discarded, t, "trigger (Pick invisible OFF)"); continue; }
                        if (!IsVisible(t, visCache)) { Note(discarded, t, "no visible renderer (Pick invisible OFF)"); continue; }
                    }
                    if (hit.distance < bestDist)
                    {
                        bestDist = hit.distance;
                        bestTransform = t;
                        bestPoint = hit.point;
                    }
                }
            }

            // Our own collider-less clones compete with physics hits by distance —
            // the user placed them and must always be able to reselect them.
            Transform boundsTransform = null;
            float boundsDist = bestDist;
            foreach (var p in _placedManager.Placed)
            {
                if (p.Root == null || p.HasCollider) continue;
                if (exclude != null && p.Root.transform == exclude) continue;
                var b = ObjectCatalog.ComputeBounds(p.Root);
                TestBounds(p.Root.transform, b, ray, ref boundsDist, ref boundsTransform);
            }
            if (boundsTransform != null)
            {
                bestDist = boundsDist;
                bestTransform = boundsTransform;
                bestPoint = ray.GetPoint(boundsDist);
            }

            // Scene decor (grass, plants — no colliders anywhere): SMALL pieces may win
            // over a physics hit when their box entry is strictly closer along the ray
            // (grass sits on top of the ground, so aiming at it enters the tuft's box
            // first). Among near-equal candidates the SMALLEST box wins, so one tuft
            // beats a whole bush cluster. Large decor keeps the old last-resort rule.
            Transform smallDecor = null;
            float smallDecorDist = float.MaxValue;
            float smallDecorVol = float.MaxValue;
            foreach (var decor in _catalog.ColliderlessRoots)
            {
                if (decor.Root == null || !decor.Root.activeInHierarchy) continue;
                if (exclude != null && decor.Root.transform == exclude) continue;
                var b = decor.Bounds;
                if (b.size.x > DECOR_PRIORITY_MAX_SIZE || b.size.y > DECOR_PRIORITY_MAX_SIZE
                    || b.size.z > DECOR_PRIORITY_MAX_SIZE) continue;
                if (!RayIntersectsAABB(ray, b, out float d)) continue;
                if (d <= 0.05f || d >= 1000f) continue;
                if (d >= bestDist) continue;   // must be strictly in FRONT of the physics hit
                float vol = b.size.x * b.size.y * b.size.z;
                if (smallDecor == null || d < smallDecorDist - 0.5f
                    || (d < smallDecorDist + 0.5f && vol < smallDecorVol))
                {
                    smallDecor = decor.Root.transform;
                    smallDecorDist = d;
                    smallDecorVol = vol;
                }
            }
            if (smallDecor != null)
            {
                bestDist = smallDecorDist;
                bestTransform = smallDecor;
                bestPoint = ray.GetPoint(smallDecorDist);
            }

            // Large decor: only when the click found nothing at all (old behavior — a
            // big AABB always starts before the real surface and would hijack clicks).
            if (bestTransform == null)
            {
                boundsTransform = null;
                boundsDist = float.MaxValue;
                foreach (var decor in _catalog.ColliderlessRoots)
                {
                    if (decor.Root == null || !decor.Root.activeInHierarchy) continue;
                    if (exclude != null && decor.Root.transform == exclude) continue;
                    TestBounds(decor.Root.transform, decor.Bounds, ray, ref boundsDist, ref boundsTransform);
                }
                if (boundsTransform != null)
                {
                    bestDist = boundsDist;
                    bestTransform = boundsTransform;
                    bestPoint = ray.GetPoint(boundsDist);
                }
            }

            // Nothing selected and this wasn't a cycling retry: leave an evidence trail
            // instead of a silent dead click.
            if (bestTransform == null && exclude == null)
            {
                MapEditorPlugin.Logger.LogInfo(rawHitCount == 0
                    ? "[SELECT] Click hit nothing (no colliders along the ray)."
                    : $"[SELECT] Click discarded all {rawHitCount} hit(s): {string.Join("; ", discarded)}");
            }
            return bestTransform != null;
        }

        private static void Note(List<string> discarded, Transform t, string reason)
        {
            if (discarded.Count < 3) discarded.Add($"'{t.name}' ({reason})");
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

        // One-line breadcrumb on every raw (level-object) pick: which node the ray hit
        // and what root the climb resolved to. The verbose "climb into parent / root
        // children" hierarchy dump used to be appended here — turned into a spam
        // multiplier (24 children × 2 sections per click) so the log file was
        // unreadable. If you need the full hierarchy for a specific case, flip
        // VERBOSE_PICK_DUMP to true and rebuild.
        private static bool VERBOSE_PICK_DUMP = false;
        private static void LogRawPickDebug(Transform hit, Transform root)
        {
            try
            {
                if (!VERBOSE_PICK_DUMP)
                {
                    MapEditorPlugin.Logger.LogInfo(
                        $"[SELECT] raw pick hit='{ObjectCatalog.BuildPath(hit)}' root='{ObjectCatalog.BuildPath(root)}'");
                    return;
                }
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

        // Physics fingerprint of the colliders under a root. Used to be a full
        // per-collider dump (type, layer, trigger, physic material) — every pick
        // spammed 5-10 lines into the log. Collapsed to a single count line; if
        // you need the per-collider details for a specific case, flip
        // VERBOSE_COLLIDER_DUMP to true and rebuild.
        private static bool VERBOSE_COLLIDER_DUMP = false;
        public static void LogColliderPhysics(GameObject root, string label)
        {
            try
            {
                var cols = root.GetComponentsInChildren<Collider>(true);
                if (!VERBOSE_COLLIDER_DUMP)
                {
                    MapEditorPlugin.Logger.LogInfo(
                        $"[SELECT] physics of {label} '{root.name}' ({cols.Length} collider(s))");
                    return;
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[SELECT] physics of {label} '{root.name}' ({cols.Length} collider(s)):");
                int shown = 0;
                foreach (var col in cols)
                {
                    if (col == null || shown++ >= 10) continue;
                    string mat;
                    try
                    {
                        var pm = col.sharedMaterial;
                        mat = pm == null
                            ? "none"
                            : $"'{pm.name}' dynFric={pm.dynamicFriction:0.###} statFric={pm.staticFriction:0.###} " +
                              $"bounce={pm.bounciness:0.###} fricCombine={pm.frictionCombine} bounceCombine={pm.bounceCombine}";
                    }
                    catch { mat = "?"; }
                    sb.AppendLine($"    - '{col.name}' type={col.GetType().Name} layer={col.gameObject.layer} " +
                                  $"trigger={col.isTrigger} enabled={col.enabled} mat={mat}");
                }
                MapEditorPlugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
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
