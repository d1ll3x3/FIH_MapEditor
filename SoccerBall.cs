using System;
using UnityEngine;

namespace FIHMapEditor
{
    // A mod-generated physics ball for soccer maps. Only exists in play mode: a primitive
    // sphere with its own Rigidbody + bouncy PhysicMaterial, spawned at the kickoff point
    // and destroyed on exit. Independent from the player rigidbody (which fly mode makes
    // kinematic in the editor) — the ball rolls and bounces under normal Unity physics and
    // the player kicks it by collision.
    public class SoccerBall
    {
        public GameObject Go { get; private set; }
        public Rigidbody Rb { get; private set; }

        private Vector3 _center;   // kickoff / reset position

        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default",
            "Unlit/Color",
        };

        public bool Alive => Go != null;

        // Create the ball at 'center' with the given radius. Safe to call repeatedly —
        // destroys any previous instance first. 'layer' should be a physics layer that
        // collides with BOTH the level and the player (see PlayModeController) — the
        // default layer of a fresh primitive may be ignored by the player's layer, which
        // makes the player walk straight through the ball.
        public void Spawn(Vector3 center, float radius, int layer = 0)
        {
            Destroy();
            _center = center;
            radius = Mathf.Clamp(radius, 0.1f, 5f);

            try
            {
                Go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Go.name = "FIH_SoccerBall";
                if (layer >= 0 && layer < 32) Go.layer = layer;
                Go.transform.position = center;
                Go.transform.localScale = Vector3.one * (radius * 2f);

                // Material: a visible ball colour (bright so it reads on any field).
                var rend = Go.GetComponent<Renderer>();
                if (rend != null)
                {
                    Shader shader = null;
                    foreach (var s in ShaderCandidates)
                    {
                        shader = Shader.Find(s);
                        if (shader != null) break;
                    }
                    if (shader != null)
                    {
                        var mat = new Material(shader);
                        var color = new Color(0.95f, 0.95f, 0.98f);
                        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                        else if (mat.HasProperty("_Color")) mat.color = color;
                        rend.material = mat;
                    }
                    rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }

                // Bouncy, low-friction so it behaves like a ball, not a lump of clay.
                var col = Go.GetComponent<Collider>();
                if (col != null)
                {
                    try
                    {
                        var pm = new PhysicsMaterial
                        {
                            name = "FIH_BallPhys",
                            bounciness = 0.6f,
                            dynamicFriction = 0.4f,
                            staticFriction = 0.4f,
                            bounceCombine = PhysicsMaterialCombine.Maximum,
                            frictionCombine = PhysicsMaterialCombine.Average,
                        };
                        col.material = pm;
                    }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[BALL] PhysicMaterial failed: {ex.Message}"); }
                }

                Rb = Go.AddComponent<Rigidbody>();
                Rb.mass = 0.6f;
                Rb.linearDamping = 0.15f;
                Rb.angularDamping = 0.25f;
                Rb.useGravity = true;
                Rb.isKinematic = false;
                Rb.interpolation = RigidbodyInterpolation.Interpolate;
                Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                MapEditorPlugin.Logger.LogInfo($"[BALL] Spawned at {center}, radius {radius}.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[BALL] Spawn failed: {ex}");
                Destroy();
            }
        }

        // Snap the ball back to the kickoff point with zeroed velocity (after a goal or a
        // fall out of bounds).
        public void ResetToCenter()
        {
            if (Rb == null) return;
            try
            {
                Rb.position = _center;
                Rb.rotation = Quaternion.identity;
                // Kinematic toggle forces the physics engine to sync immediately.
                bool wasKinematic = Rb.isKinematic;
                Rb.isKinematic = true;
                Rb.isKinematic = wasKinematic;
                Rb.linearVelocity = Vector3.zero;
                Rb.angularVelocity = Vector3.zero;
                if (Go != null) Go.transform.position = _center;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[BALL] Reset error: {ex.Message}");
            }
        }

        public Vector3 Position => Go != null ? Go.transform.position : _center;
        public Vector3 Velocity => Rb != null && !Rb.isKinematic ? Rb.linearVelocity : Vector3.zero;

        // Give the ball an instantaneous velocity (a kick).
        public void Kick(Vector3 velocity)
        {
            if (Rb == null || Rb.isKinematic) return;
            try
            {
                Rb.linearVelocity = velocity;
            }
            catch { }
        }

        // Online follower mode: a non-authoritative client freezes local physics and
        // moves the ball to the states the authority broadcasts.
        public void SetKinematic(bool kinematic)
        {
            if (Rb == null) return;
            try
            {
                Rb.isKinematic = kinematic;
                if (!kinematic)
                {
                    Rb.linearVelocity = Vector3.zero;
                    Rb.angularVelocity = Vector3.zero;
                }
            }
            catch { }
        }

        public void MoveTo(Vector3 position)
        {
            if (Go == null) return;
            try
            {
                if (Rb != null) Rb.position = position;
                Go.transform.position = position;
            }
            catch { }
        }

        public void Destroy()
        {
            if (Go != null)
            {
                try { UnityEngine.Object.Destroy(Go); } catch { }
            }
            Go = null;
            Rb = null;
        }
    }
}
