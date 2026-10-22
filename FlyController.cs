using System;
using UnityEngine;

namespace FIHMapEditor
{
    // Editor free-fly, ported from the FlippingIsHard trainer: kinematic body, no gravity,
    // no collisions, moved by position so the game's physics/prediction can't fight us.
    public class FlyController
    {
        private readonly GameObjectFinder _finder;
        private readonly InputHandler _input;

        private bool _active = false;
        private bool _wasKinematic = false;
        private bool _wasGravity = true;

        public bool IsActive => _active;

        public FlyController(GameObjectFinder finder, InputHandler input)
        {
            _finder = finder;
            _input = input;
        }

        public void Enter()
        {
            if (_active) return;
            try
            {
                var rb = _finder.GetCachedPlayerRigidbody() ?? _finder.FindPlayer()?.GetComponent<Rigidbody>();
                if (rb == null) return;

                // Kept as fallbacks for Exit, which otherwise restores the game's own rule.
                _wasKinematic = rb.isKinematic;
                _wasGravity = rb.useGravity;

                rb.isKinematic = true;
                rb.useGravity = false;
                rb.detectCollisions = false;
                _active = true;
                if (EditorConfig.VerboseLogs)
                    MapEditorPlugin.Logger.LogInfo("[FLY] Editor fly ON");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error entering fly mode: {ex}");
            }
        }

        public void Exit()
        {
            if (!_active) return;
            try
            {
                var rb = _finder.GetCachedPlayerRigidbody();
                if (rb != null)
                {
                    // Put back what the game itself would, not what was observed on
                    // Enter: fly can start in the middle of a game routine that has the
                    // body changed for a moment (a cannon launch switches collisions off
                    // and back on), and restoring that snapshot left the player walking
                    // through everything. Collisions are always on outside those
                    // routines; the body is kinematic only while the game freezes the
                    // player (same rule as SceneZiplineSpline.ReleaseAttachedPlayer).
                    rb.useGravity = _wasGravity;
                    rb.detectCollisions = true;
                    rb.isKinematic = IsFrozenByGame(rb) ?? _wasKinematic;

                    // Do not carry editor movement into gameplay as residual momentum.
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
                _active = false;
                if (EditorConfig.VerboseLogs)
                    MapEditorPlugin.Logger.LogInfo("[FLY] Editor fly OFF");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error exiting fly mode: {ex}");
            }
        }

        private static bool? IsFrozenByGame(Rigidbody rb)
        {
            var player = rb.GetComponentInParent<EHS.PlayerNetworked>() ?? rb.GetComponentInChildren<EHS.PlayerNetworked>();
            return player != null ? player.IsComposedFrozen : (bool?)null;
        }

        // Call every Update while active and movement input is allowed.
        public void Move()
        {
            if (!_active) return;
            try
            {
                var cameraTransform = _finder.FindCameraTransform();
                var rb = _finder.GetCachedPlayerRigidbody();
                if (cameraTransform == null || rb == null) return;

                Vector3 velocity = CalculateFlyVelocity(cameraTransform);
                Vector3 displacement = velocity * Time.deltaTime;
                if (displacement == Vector3.zero) return;

                // Move by position (kinematic-safe; setting velocity would be ignored + warn).
                Vector3 target = rb.position + displacement;
                rb.position = target;
                var t = _finder.FindPlayerTransform();
                if (t != null) t.position = target;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error in fly move: {ex}");
            }
        }

        private Vector3 CalculateFlyVelocity(Transform cameraTransform)
        {
            // Horizontal movement follows camera yaw, while vertical input remains world-up.
            // Removing pitch prevents looking down from turning forward movement into descent.
            Vector3 forward = cameraTransform.forward;
            Vector3 right = cameraTransform.right;
            forward.y = 0;
            right.y = 0;
            if (forward.magnitude > 0.001f) forward.Normalize();
            if (right.magnitude > 0.001f) right.Normalize();

            float speed = EditorConfig.Settings.FlySpeed;
            if (_input.IsBoostHeld()) speed *= EditorConfig.Settings.FlySpeedBoost;

            Vector3 velocity = Vector3.zero;
            if (_input.IsFlyForward()) velocity += forward * speed;
            if (_input.IsFlyBack()) velocity -= forward * speed;
            if (_input.IsFlyLeft()) velocity -= right * speed;
            if (_input.IsFlyRight()) velocity += right * speed;
            if (_input.IsFlyUp()) velocity.y += speed;
            if (_input.IsFlyDown()) velocity.y -= speed;

            return velocity;
        }
    }
}
