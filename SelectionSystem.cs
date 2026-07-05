using System;
using UnityEngine;

namespace FIHMapEditor
{
    public class Selection
    {
        public PlacedObject Placed;   // set when the selection is one of our clones
        public GameObject Raw;        // set when "Unlock" selected an original scene object

        public bool IsPlaced => Placed != null;
        public GameObject Target => Placed?.Root ?? Raw;
        public bool IsValid => Target != null;
        public string DisplayName => Placed != null
            ? Placed.Root.name
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

        public SelectionSystem(GameObjectFinder finder, PlacedObjectManager placedManager, ObjectCatalog catalog)
        {
            _finder = finder;
            _placedManager = placedManager;
            _catalog = catalog;
        }

        public void Select(PlacedObject placed)
        {
            Current = new Selection { Placed = placed };
        }

        public void Deselect()
        {
            Current = new Selection();
        }

        // Returns the world point hit (for stamp placement) even when nothing selectable
        // was there. unlockOriginals lets clicks resolve original scene geometry too.
        public bool PickAtMouse(bool unlockOriginals, out Vector3 hitPoint)
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

                var hits = Physics.RaycastAll(ray, 1000f);
                if (hits != null)
                {
                    foreach (var hit in hits)
                    {
                        var col = hit.collider;
                        if (col == null) continue;
                        var t = col.transform;
                        if (playerRoot != null && t.root == playerRoot) continue;
                        if (t.name.StartsWith("FIH_Line")) continue;
                        if (hit.distance < bestDist)
                        {
                            bestDist = hit.distance;
                            bestTransform = t;
                            bestPoint = hit.point;
                        }
                    }
                }

                // Bounds pass: collider-less objects are invisible to physics raycasts.
                // Test their AABBs (our clones live, scene decor from the scan cache) and
                // take one only when it's closer than the physics hit.
                Transform boundsTransform = null;
                float boundsDist = bestDist;

                foreach (var p in _placedManager.Placed)
                {
                    if (p.Root == null || p.HasCollider) continue;
                    var b = ObjectCatalog.ComputeBounds(p.Root);
                    TestBounds(p.Root.transform, b, ray, ref boundsDist, ref boundsTransform);
                }
                foreach (var decor in _catalog.ColliderlessRoots)
                {
                    if (decor.Root == null || !decor.Root.activeInHierarchy) continue;
                    TestBounds(decor.Root.transform, decor.Bounds, ray, ref boundsDist, ref boundsTransform);
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
                    Current = new Selection { Placed = placed };
                    return true;
                }

                if (unlockOriginals)
                {
                    // Resolve to the same logical root the catalog scan would pick.
                    var root = ClimbToLogicalRoot(bestTransform);
                    Current = new Selection { Raw = root.gameObject };
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

        // Pure-managed slab test; avoids relying on the Bounds.IntersectRay interop
        // out-param overload.
        private static bool RayIntersectsAABB(Ray ray, Bounds b, out float dist)
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

        private static Transform ClimbToLogicalRoot(Transform t)
        {
            var root = t;
            var cache = new System.Collections.Generic.Dictionary<int, int>();
            while (root.parent != null)
            {
                var p = root.parent;
                if (p.GetComponent<Renderer>() != null || p.GetComponent<Collider>() != null)
                {
                    root = p;
                    continue;
                }
                int renderableChildren = 0;
                for (int i = 0; i < p.childCount; i++)
                {
                    var child = p.GetChild(i);
                    if (child.GetComponentInChildren<Renderer>(true) != null ||
                        child.GetComponentInChildren<Collider>(true) != null)
                        renderableChildren++;
                    if (renderableChildren > 1) break;
                }
                if (renderableChildren == 1) { root = p; continue; }
                break;
            }
            return root;
        }
    }
}
