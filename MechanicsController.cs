using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Detects which game mechanic a source object carries and reads its tuning values
    // via Il2Cpp reflection (no compile-time dependency on EHS.Interactables types, so
    // the DLL stays loadable on game versions that rename them).
    public static class MechanicsDetector
    {
        public const float DEFAULT_BOOST_FORCE = 30f;
        public const float DEFAULT_CANNON_TIMER = 1.5f;

        // Subtree-only check (no ancestor climbing): used by the catalog so ONLY the
        // full assemblies get the Mechanics category, not every neighboring piece.
        public static MechanicType DetectTypeInSubtree(GameObject root)
        {
            float bf = DEFAULT_BOOST_FORCE, ct = DEFAULT_CANNON_TIMER;
            return DetectIn(root, ref bf, ref ct);
        }

        public static MechanicType Detect(GameObject root, out float boostForce, out float cannonTimer,
                                          bool climbAncestors = true)
        {
            boostForce = DEFAULT_BOOST_FORCE;
            cannonTimer = DEFAULT_CANNON_TIMER;
            if (root == null) return MechanicType.None;

            var found = DetectIn(root, ref boostForce, ref cannonTimer);
            if (found != MechanicType.None) return found;
            if (!climbAncestors) return MechanicType.None;

            // The game's interactables are multi-part assemblies (visual mesh, interact
            // trigger, area — all siblings). Cloning one piece must still detect the
            // mechanic, so look at nearby ancestors too; the bounds guard keeps us from
            // scanning whole level chunks and mis-tagging unrelated decor.
            try
            {
                var t = root.transform.parent;
                for (int level = 0; t != null && level < 2; level++, t = t.parent)
                {
                    // Never climb into our own clone containers: a sibling cannon clone
                    // would leak its mechanic onto every duplicated plank.
                    if (t.name == "FIH_MapObjectsRoot" || t.name == "FIH_SpawnRoot") break;
                    var bounds = ObjectCatalog.ComputeBounds(t.gameObject);
                    if (bounds.size.x > 40f || bounds.size.y > 40f || bounds.size.z > 40f) break;
                    found = DetectIn(t.gameObject, ref boostForce, ref cannonTimer);
                    if (found != MechanicType.None) return found;
                }
            }
            catch { }
            return MechanicType.None;
        }

        private static MechanicType DetectIn(GameObject root, ref float boostForce, ref float cannonTimer)
        {
            try
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string name = null;
                    try { name = mb.GetIl2CppType()?.Name; }
                    catch { }
                    if (name == null) continue;

                    // Exact demo names first, Contains as a playtest-rename fallback.
                    if (name == "NetworkedInteractableBoostPad" || name.Contains("InteractableBoostPad"))
                    {
                        if (TryReadFloat(mb, "boostForce", "BoostForce", out float f))
                            boostForce = Mathf.Clamp(f, 1f, 200f);
                        return MechanicType.BoostPad;
                    }
                    if (name == "NetworkedInteractableCannon" || name.Contains("InteractableCannon"))
                    {
                        if (TryReadFloat(mb, "launchTimer", "LaunchTimer", out float t))
                            cannonTimer = Mathf.Clamp(t, 0.2f, 5f);
                        return MechanicType.Cannon;
                    }
                }
            }
            catch { }
            return MechanicType.None;
        }

        // Property first (the proven GameObjectFinder pattern), then field, then — pads
        // only — the boostPadData struct. Any failure leaves the caller on its default.
        private static bool TryReadFloat(Component comp, string fieldName, string propName, out float value)
        {
            value = 0f;
            Il2CppSystem.Type type = null;
            try { type = comp.GetIl2CppType(); }
            catch { }
            if (type == null) return false;

            try
            {
                var prop = type.GetProperty(propName);
                var getter = prop?.GetGetMethod();
                var res = getter?.Invoke(comp, null);
                if (res != null) { value = res.Unbox<float>(); return true; }
            }
            catch { }

            try
            {
                var field = type.GetField(fieldName,
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);
                var val = field?.GetValue(comp);
                if (val != null) { value = val.Unbox<float>(); return true; }
            }
            catch { }

            // Nested struct fallback: boostPadData.boostForce
            if (fieldName == "boostForce")
            {
                try
                {
                    var dataField = type.GetField("boostPadData",
                        Il2CppSystem.Reflection.BindingFlags.Instance |
                        Il2CppSystem.Reflection.BindingFlags.Public |
                        Il2CppSystem.Reflection.BindingFlags.NonPublic);
                    var boxed = dataField?.GetValue(comp);
                    if (boxed != null)
                    {
                        var inner = boxed.GetIl2CppType()?.GetField("boostForce")?.GetValue(boxed);
                        if (inner != null) { value = inner.Unbox<float>(); return true; }
                    }
                }
                catch { }
            }
            return false;
        }
    }

    // Runtime simulation of the mechanics on placed clones: boost pads launch the
    // player on contact; cannons capture the player on interact, hold for the timer
    // and launch a ballistic arc to their target. In editor mode a cannon launch
    // temporarily leaves fly mode so real physics can play the arc (test flight).
    public class MechanicsController
    {
        private enum Phase { Idle, Holding, TestFlight }

        private readonly GameObjectFinder _finder;
        private readonly PlacedObjectManager _placed;
        private readonly InputHandler _input;
        private readonly FlyController _fly;

        private Phase _phase = Phase.Idle;
        private PlacedObject _activeCannon;
        private float _holdUntil;
        private Vector3 _holdPos;
        private bool _holdWasKinematic;
        private bool _resumeFlyOnEnd;
        private float _flightTimeout;
        private bool _dragLogged;

        private readonly Dictionary<int, float> _cooldowns = new Dictionary<int, float>();

        // ComputeColliderBounds walks the clone's whole component tree — too costly to
        // redo per placed mechanic per frame. Cached against the transform's state and
        // recomputed only when the object actually moved/rotated/scaled.
        private readonly Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale, Bounds bounds)> _boundsCache
            = new Dictionary<int, (Vector3, Quaternion, Vector3, Bounds)>();

        private Bounds GetColliderBounds(PlacedObject p)
        {
            var t = p.Root.transform;
            if (_boundsCache.TryGetValue(p.Id, out var c)
                && c.pos == t.position && c.rot == t.rotation && c.scale == t.localScale)
                return c.bounds;
            var bounds = ComputeColliderBounds(p.Root);
            _boundsCache[p.Id] = (t.position, t.rotation, t.localScale, bounds);
            return bounds;
        }

        private const float INTERACT_RANGE = 3.5f;
        private const float PAD_COOLDOWN = 0.4f;
        private const float CANNON_COOLDOWN = 1f;
        private const float FLIGHT_MAX_SECONDS = 8f;

        public string InteractPrompt { get; private set; }
        public bool IsControllingPlayer => _phase != Phase.Idle;

        public MechanicsController(GameObjectFinder finder, PlacedObjectManager placed,
                                   InputHandler input, FlyController fly)
        {
            _finder = finder;
            _placed = placed;
            _input = input;
            _fly = fly;
        }

        // Abort anything in progress; called on mode switches and scene changes.
        public void ResetState()
        {
            if (_phase == Phase.Holding)
            {
                var rb = _finder.GetCachedPlayerRigidbody();
                if (rb != null) rb.isKinematic = _holdWasKinematic;
            }
            if (_resumeFlyOnEnd)
            {
                _fly.Enter();
                _resumeFlyOnEnd = false;
            }
            _phase = Phase.Idle;
            _activeCannon = null;
            InteractPrompt = null;
            _cooldowns.Clear();
            _boundsCache.Clear();
        }

        public void Update(EditorMode mode, bool acceptInput, bool textFieldFocused)
        {
            try
            {
                var rb = _finder.GetCachedPlayerRigidbody() ?? _finder.FindPlayer()?.GetComponent<Rigidbody>();
                if (rb == null) { InteractPrompt = null; return; }

                switch (_phase)
                {
                    case Phase.Idle:
                        UpdateBoostPads(rb, mode);
                        UpdateIdleCannons(rb, mode, acceptInput, textFieldFocused);
                        break;
                    case Phase.Holding:
                        UpdateHolding(rb);
                        break;
                    case Phase.TestFlight:
                        UpdateTestFlight(rb);
                        break;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[MECH] Update error: {ex.Message}");
                ResetState();
            }
        }

        // ───────────────────────────────────────────────────────────── boost pads ──

        private void UpdateBoostPads(Rigidbody rb, EditorMode mode)
        {
            // Fly mode keeps the body kinematic/collisionless — pads are inert while
            // building; they only act on a dynamic body (Play, or an editor test flight).
            if (rb.isKinematic) return;

            Vector3 pos = rb.position;
            foreach (var p in _placed.Placed)
            {
                if (p.Mechanic != MechanicType.BoostPad || p.Root == null) continue;

                var bounds = GetColliderBounds(p);
                if (bounds.size == Vector3.zero) continue;
                bounds.Expand(0.6f);
                if (!bounds.Contains(pos)) continue;
                if (_cooldowns.TryGetValue(p.Id, out float until) && Time.time < until) continue;

                if (p.CannonTarget != null)
                {
                    // Aimed pad: ballistic launch that lands on its target ring.
                    var target = VecUtil.ToVector3(p.CannonTarget);
                    rb.linearVelocity = SolveBallisticVelocity(rb.position, target, Mathf.Abs(Physics.gravity.y));
                }
                else
                {
                    // Classic pad: kick along the pad's up axis.
                    Vector3 up = p.Root.transform.up;
                    var v = rb.linearVelocity;
                    v -= Vector3.Project(v, up);
                    v += up * p.BoostForce;
                    rb.linearVelocity = v;
                }

                _cooldowns[p.Id] = Time.time + PAD_COOLDOWN;
                MapEditorPlugin.Logger.LogInfo($"[MECH] Boost pad #{p.Id} launched player " +
                    (p.CannonTarget != null ? "(aimed)" : $"(force {p.BoostForce:0.#})"));
            }
        }

        // ──────────────────────────────────────────────────────────────── cannons ──

        private void UpdateIdleCannons(Rigidbody rb, EditorMode mode, bool acceptInput, bool textFieldFocused)
        {
            InteractPrompt = null;

            PlacedObject nearest = null;
            float nearestDist = INTERACT_RANGE;
            Vector3 playerPos = rb.position;
            foreach (var p in _placed.Placed)
            {
                if (p.Mechanic != MechanicType.Cannon || p.Root == null) continue;
                var bounds = GetColliderBounds(p);
                float d = Vector3.Distance(bounds.center, playerPos);
                if (d < nearestDist)
                {
                    nearest = p;
                    nearestDist = d;
                }
            }
            if (nearest == null) return;

            bool onCooldown = _cooldowns.TryGetValue(nearest.Id, out float until) && Time.time < until;
            if (onCooldown) return;

            InteractPrompt = $"[{EditorConfig.Settings.InteractKey}] Launch cannon";

            if (!acceptInput || textFieldFocused) return;
            if (!_input.WasKeyPressed("interact", EditorConfig.Settings.InteractKey)) return;

            BeginHold(nearest, rb, mode);
        }

        // Custom launch point when set (cyan ring, gizmo-editable); otherwise the center
        // of the cannon's VISUAL. Renderer bounds, not collider bounds: the assembly
        // carries a huge interact-trigger collider whose center floats far above the
        // cannon, which is exactly where the ring must NOT be.
        public static Vector3 GetLaunchPos(PlacedObject cannon)
        {
            if (cannon.CannonLaunchPos != null) return VecUtil.ToVector3(cannon.CannonLaunchPos);
            var bounds = ObjectCatalog.ComputeBounds(cannon.Root);
            if (bounds.size == Vector3.zero) bounds = ComputeColliderBounds(cannon.Root);
            return bounds.center;
        }

        private void BeginHold(PlacedObject cannon, Rigidbody rb, EditorMode mode)
        {
            // In editor mode, leave fly so the body is dynamic for the launch.
            if (mode == EditorMode.Editor && _fly.IsActive)
            {
                _fly.Exit();
                _resumeFlyOnEnd = true;
            }

            _holdPos = GetLaunchPos(cannon);
            _activeCannon = cannon;
            _holdUntil = Time.time + Mathf.Clamp(cannon.CannonTimer, 0.2f, 5f);
            _phase = Phase.Holding;
            InteractPrompt = null;

            // The hold point sits INSIDE the cannon: go kinematic so its colliders can't
            // push the player out; restored just before the launch velocity is applied.
            _holdWasKinematic = rb.isKinematic;
            rb.isKinematic = true;

            // Snap into the hold immediately — the distance-abort check in UpdateHolding
            // would otherwise fire on the very first frame for tall cannons.
            rb.position = _holdPos;
            var t = _finder.FindPlayerTransform();
            if (t != null) t.position = _holdPos;
            MapEditorPlugin.Logger.LogInfo($"[MECH] Cannon #{cannon.Id}: holding for {cannon.CannonTimer:0.##}s");
        }

        private void UpdateHolding(Rigidbody rb)
        {
            // Abort when the cannon vanished or something teleported the player away
            // (reset zone, checkpoint respawn...) since the last frame's pin.
            if (_activeCannon?.Root == null || Vector3.Distance(rb.position, _holdPos) > 3f)
            {
                EndCannonControl();
                return;
            }

            // Pin the player inside the muzzle. Body is kinematic during the hold, so
            // position-only (velocity writes on a kinematic body just log warnings).
            rb.position = _holdPos;
            var t = _finder.FindPlayerTransform();
            if (t != null) t.position = _holdPos;

            if (Time.time < _holdUntil) return;

            // Launch! Back to dynamic first, or the velocity is ignored.
            rb.isKinematic = false;
            var target = VecUtil.ToVector3(_activeCannon.CannonTarget, _holdPos + Vector3.forward * 12f);
            float g = Mathf.Abs(Physics.gravity.y);
            rb.linearVelocity = SolveBallisticVelocity(_holdPos, target, g);
            rb.angularVelocity = Vector3.zero;
            _cooldowns[_activeCannon.Id] = Time.time + CANNON_COOLDOWN;

            if (!_dragLogged)
            {
                _dragLogged = true;
                MapEditorPlugin.Logger.LogInfo($"[MECH] Launch: drag={rb.linearDamping:0.###} (nonzero drag makes arcs undershoot)");
            }

            if (_resumeFlyOnEnd)
            {
                _phase = Phase.TestFlight;
                _flightTimeout = Time.time + FLIGHT_MAX_SECONDS;
            }
            else
            {
                _phase = Phase.Idle;
                _activeCannon = null;
            }
        }

        // Editor-only: the arc plays under real physics; any fly key (or landing, or a
        // timeout) hands control back to fly mode.
        private void UpdateTestFlight(Rigidbody rb)
        {
            bool flyKey = _input.IsFlyForward() || _input.IsFlyBack() || _input.IsFlyLeft()
                       || _input.IsFlyRight() || _input.IsFlyUp() || _input.IsFlyDown();
            bool landed = rb.linearVelocity.magnitude < 0.3f && Time.time > _flightTimeout - FLIGHT_MAX_SECONDS + 1f;
            if (flyKey || landed || Time.time >= _flightTimeout)
                EndCannonControl();
        }

        private void EndCannonControl()
        {
            // An abort mid-hold leaves the body kinematic — hand it back as it was
            // (fly.Enter below re-takes it in the editor case anyway).
            if (_phase == Phase.Holding)
            {
                var rb = _finder.GetCachedPlayerRigidbody();
                if (rb != null) rb.isKinematic = _holdWasKinematic;
            }
            if (_resumeFlyOnEnd)
            {
                _fly.Enter();
                _resumeFlyOnEnd = false;
            }
            _phase = Phase.Idle;
            _activeCannon = null;
        }

        // ──────────────────────────────────────────────────────────────── helpers ──

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
            return has ? bounds : ObjectCatalog.ComputeBounds(go);
        }

        // Apex-height parameterization: always solvable, lands on the target under
        // constant gravity (drag ignored — logged once at launch).
        public static Vector3 SolveBallisticVelocity(Vector3 start, Vector3 target, float g)
        {
            Vector3 flat = target - start;
            flat.y = 0f;
            float horizontalDist = flat.magnitude;

            float apex = Mathf.Max(start.y, target.y) + Mathf.Clamp(horizontalDist * 0.25f, 2f, 12f);
            float vy = Mathf.Sqrt(2f * g * (apex - start.y));
            float tUp = vy / g;
            float tDown = Mathf.Sqrt(2f * Mathf.Max(0.01f, apex - target.y) / g);
            float t = tUp + tDown;

            Vector3 vXZ = t > 0.001f ? flat / t : Vector3.zero;
            return new Vector3(vXZ.x, vy, vXZ.z);
        }
    }
}
