using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Player/camera lookup ported from the FlippingIsHard trainer: cached, cooldown-guarded
    // and multiplayer-aware (only the locally-owned player is returned).
    public class GameObjectFinder
    {
        private GameObject _cachedPlayer;
        private Rigidbody _cachedPlayerRigidbody;
        private GameObject _cachedCamera;

        private float _playerSearchCooldown = 0f;
        private float _cameraSearchCooldown = 0f;
        private const float SEARCH_COOLDOWN_DURATION = 2f;

        public Rigidbody GetCachedPlayerRigidbody() => _cachedPlayerRigidbody;

        private EHS.FlyCheat _cachedFlyCheat;

        // The game's native fly cheat component. We disable it so its Shift+F toggle can
        // never start a second fly while the editor owns the player body.
        public EHS.FlyCheat GetFlyCheat()
        {
            if (_cachedFlyCheat != null)
                return _cachedFlyCheat;
            try
            {
                _cachedFlyCheat = UnityEngine.Object.FindObjectOfType<EHS.FlyCheat>();
            }
            catch (Exception)
            {
                // Silent catch
            }
            return _cachedFlyCheat;
        }

        public Transform FindPlayerTransform()
        {
            var player = FindPlayer();
            return player?.transform;
        }

        public Transform FindCameraTransform()
        {
            var camera = FindCamera();
            return camera?.transform;
        }

        public GameObject FindPlayer()
        {
            // Unity overloads == so a destroyed object (scene change / respawn) compares
            // as null, auto-invalidating the cache.
            if (_cachedPlayer != null)
                return _cachedPlayer;

            if (Time.time < _playerSearchCooldown)
                return null;

            try
            {
                var local = FindLocalPlayer();
                if (local != null)
                {
                    _cachedPlayer = local;
                    _cachedPlayerRigidbody = _cachedPlayer.GetComponent<Rigidbody>();
                    return _cachedPlayer;
                }
            }
            catch (Exception)
            {
                // Silent catch
            }

            _playerSearchCooldown = Time.time + SEARCH_COOLDOWN_DURATION;
            _cachedPlayerRigidbody = null;
            return null;
        }

        private GameObject FindLocalPlayer()
        {
            GameObject[] players = null;
            try { players = GameObject.FindGameObjectsWithTag("Player"); }
            catch { }

            // Fallback: no tagged players (different game version) → scan for PlayerNetworked.
            if (players == null || players.Length == 0)
            {
                var list = new List<GameObject>();
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                    foreach (var mb in all)
                    {
                        if (mb != null && mb.GetIl2CppType() != null
                            && mb.GetIl2CppType().Name == "PlayerNetworked")
                            list.Add(mb.gameObject);
                    }
                }
                catch { }
                players = list.ToArray();
            }

            if (players == null || players.Length == 0)
                return null;

            if (players.Length == 1)
                return players[0];

            // Multiplayer: find the one we own.
            foreach (var p in players)
            {
                if (IsLocalPlayer(p)) return p;
            }

            // Last-resort fallback: the player closest to the active camera is almost
            // always the local one.
            if (Camera.main != null)
            {
                GameObject best = null;
                float bestDist = float.MaxValue;
                var camPos = Camera.main.transform.position;
                foreach (var p in players)
                {
                    if (p == null) continue;
                    float d = Vector3.Distance(p.transform.position, camPos);
                    if (d < bestDist) { bestDist = d; best = p; }
                }
                if (best != null && bestDist <= 10f) return best;
            }

            return null;
        }

        private bool IsLocalPlayer(GameObject p)
        {
            if (p == null) return false;
            try
            {
                var cam = p.GetComponentInChildren<Camera>(false);
                if (cam != null && cam.isActiveAndEnabled) return true;

                var mbs = p.GetComponents<MonoBehaviour>();
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    var typeObj = mb.GetIl2CppType();
                    if (typeObj == null) continue;

                    var isOwnerProp = typeObj.GetProperty("IsOwner");
                    if (isOwnerProp == null) continue;

                    var method = isOwnerProp.GetGetMethod();
                    if (method == null) continue;

                    var res = method.Invoke(mb, null);
                    if (res != null && res.Unbox<bool>()) return true;
                }
            }
            catch { }
            return false;
        }

        public int CountPlayers()
        {
            try
            {
                var players = GameObject.FindGameObjectsWithTag("Player");
                return players?.Length ?? 0;
            }
            catch { return 0; }
        }

        public GameObject FindCamera()
        {
            if (_cachedCamera != null)
                return _cachedCamera;

            if (Time.time < _cameraSearchCooldown)
                return null;

            try
            {
                _cachedCamera = GameObject.FindWithTag("MainCamera");
                if (_cachedCamera != null)
                {
                    return _cachedCamera;
                }
            }
            catch (Exception)
            {
                // Silent catch
            }

            _cameraSearchCooldown = Time.time + SEARCH_COOLDOWN_DURATION;
            return null;
        }

        public void ClearCache()
        {
            _cachedPlayer = null;
            _cachedPlayerRigidbody = null;
            _cachedCamera = null;
            _cachedFlyCheat = null;
        }
    }
}
