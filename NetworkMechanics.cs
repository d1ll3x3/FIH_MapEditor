using EHS;
using EHS.Features.SceneObjects;
using EHS.Interactables.Abstract;
using EHS.Interactables.BoostPad;
using EHS.Interactables.BreakingPlatform;
using EHS.Interactables.Cannon;
using EHS.Player.HelpBeam;
using FishNet.Object;
using UnityEngine;

namespace FIHMapEditor
{
    // The release game's spawner-backed FishNet interactables. Each kind maps to the
    // concrete NetworkSpawnerPoint the levels use, so the game's own InitializeNobServer
    // copies the per-object data into the replicated SyncVars exactly like a level does.
    public enum NetworkMechanicKind { None, BoostPad, Cannon, BreakingPlatform, GlassShatter, HighForceGlass, HelpBeam }

    public static class NetworkMechanics
    {
        // Fallback pad force when neither the map nor the prefab has one.
        public const float DEFAULT_BOOST_FORCE = 30f;

        // Identified by the behaviour on the prefab, not by name: themed variants
        // (BoostPadCandyLand...) share the component, and visual-only pieces such as
        // Cannon_Platform_Visual carry no NetworkObject at all.
        public static NetworkMechanicKind KindOf(GameObject prefabRoot)
        {
            if (prefabRoot == null || prefabRoot.GetComponent<NetworkObject>() == null)
                return NetworkMechanicKind.None;
            if (prefabRoot.GetComponentInChildren<NetworkedInteractableBoostPad>(true) != null)
                return NetworkMechanicKind.BoostPad;
            if (prefabRoot.GetComponentInChildren<NetworkedInteractableCannon>(true) != null)
                return NetworkMechanicKind.Cannon;
            if (prefabRoot.GetComponentInChildren<NetworkedInteractableTouchBreakingPlatform>(true) != null)
                return NetworkMechanicKind.BreakingPlatform;
            if (prefabRoot.GetComponentInChildren<NetworkedHighForceShatter>(true) != null)
                return NetworkMechanicKind.HighForceGlass;
            if (prefabRoot.GetComponentInChildren<NetworkedGlassShatter>(true) != null)
                return NetworkMechanicKind.GlassShatter;
            if (prefabRoot.GetComponentInChildren<NetworkedHelpBeam>(true) != null)
                return NetworkMechanicKind.HelpBeam;
            return NetworkMechanicKind.None;
        }

        // Whether a spawned replica received its replicated data. FishNet only sends a
        // SyncVar in the spawn message when it was set after the object's network
        // initialisation; the spawner sets it before, so clients get it empty: the object
        // shows its default pose and does nothing (a cannon even switches the player's
        // collisions off and bails out). Kinds without data always pass.
        public static bool HasReplicatedData(GameObject root, NetworkMechanicKind kind) => kind switch
        {
            NetworkMechanicKind.BoostPad =>
                root.GetComponentInChildren<NetworkedInteractableBoostPad>(true)?.dataSyncVar?.Value != null,
            NetworkMechanicKind.Cannon =>
                root.GetComponentInChildren<NetworkedInteractableCannon>(true)?.dataSyncVar?.Value != null,
            NetworkMechanicKind.BreakingPlatform =>
                root.GetComponentInChildren<NetworkedInteractableTouchBreakingPlatform>(true)?.dataSyncVar?.Value != null,
            NetworkMechanicKind.GlassShatter =>
                root.GetComponentInChildren<NetworkedGlassShatter>(true)?.dataSyncVar?.Value != null,
            NetworkMechanicKind.HighForceGlass =>
                root.GetComponentInChildren<NetworkedHighForceShatter>(true)?.dataSyncVar?.Value != null,
            _ => true,
        };

        // Host, right after Spawn: mark the data dirty so FishNet sends it to every
        // client, mod or not (see HasReplicatedData for why the spawn message lacks it).
        public static void PushReplicatedData(GameObject root, NetworkMechanicKind kind)
        {
            try
            {
                PushReplicatedDataUnchecked(root, kind);
            }
            catch (System.Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[NETPLACE] Could not send the data of '{root.name}': {ex.Message}");
            }
        }

        private static void PushReplicatedDataUnchecked(GameObject root, NetworkMechanicKind kind)
        {
            switch (kind)
            {
                case NetworkMechanicKind.BoostPad:
                    root.GetComponentInChildren<NetworkedInteractableBoostPad>(true)?.dataSyncVar?.DirtyAll();
                    break;
                case NetworkMechanicKind.Cannon:
                    root.GetComponentInChildren<NetworkedInteractableCannon>(true)?.dataSyncVar?.DirtyAll();
                    break;
                case NetworkMechanicKind.BreakingPlatform:
                    root.GetComponentInChildren<NetworkedInteractableTouchBreakingPlatform>(true)?.dataSyncVar?.DirtyAll();
                    break;
                case NetworkMechanicKind.GlassShatter:
                    root.GetComponentInChildren<NetworkedGlassShatter>(true)?.dataSyncVar?.DirtyAll();
                    break;
                case NetworkMechanicKind.HighForceGlass:
                    root.GetComponentInChildren<NetworkedHighForceShatter>(true)?.dataSyncVar?.DirtyAll();
                    break;
            }
        }

        // Client: give a replica that arrived without data the placement's own values,
        // which co-edit already delivered. SetInitialValues writes the SyncVar's value
        // directly (a client may not set it through Value). The cannon's barrel is aimed
        // again: OnStartClient only aims it when the data is already there.
        public static void ApplyDataLocally(GameObject root, PlacedObject placed)
        {
            switch (placed.NetworkKind)
            {
                case NetworkMechanicKind.BoostPad:
                {
                    var pad = root.GetComponentInChildren<NetworkedInteractableBoostPad>(true);
                    pad?.dataSyncVar?.SetInitialValues(new NetworkDataBoostPad
                    {
                        boostForce = placed.NetworkBoostForce ?? DEFAULT_BOOST_FORCE,
                        targetAngle = placed.NetworkBoostAngle ?? 45f,
                    });
                    break;
                }
                case NetworkMechanicKind.Cannon:
                {
                    var cannon = root.GetComponentInChildren<NetworkedInteractableCannon>(true);
                    if (cannon == null) break;
                    var data = new NetworkDataCannon
                    {
                        launchAngle = placed.NetworkCannonAngle ?? 45f,
                        launchForce = placed.NetworkCannonForce ?? 20f,
                        blockAirControlDuration = placed.NetworkCannonAirControlBlock ?? 0.5f,
                    };
                    cannon.dataSyncVar?.SetInitialValues(data);
                    cannon.SetupRotation(data.launchAngle);
                    break;
                }
                case NetworkMechanicKind.BreakingPlatform:
                    root.GetComponentInChildren<NetworkedInteractableTouchBreakingPlatform>(true)?.dataSyncVar?
                        .SetInitialValues(new NetworkDataTouchBreakingPlatform
                        {
                            delayBeforeBreak = placed.NetworkBreakDelay ?? 0.5f,
                            breakDuration = placed.NetworkBreakDuration ?? 3f,
                        });
                    break;
                case NetworkMechanicKind.GlassShatter:
                    root.GetComponentInChildren<NetworkedGlassShatter>(true)?.dataSyncVar?
                        .SetInitialValues(new NetworkDataGlassShatter
                        {
                            shatteredDuration = placed.NetworkShatteredDuration ?? 3f,
                        });
                    break;
                case NetworkMechanicKind.HighForceGlass:
                    root.GetComponentInChildren<NetworkedHighForceShatter>(true)?.dataSyncVar?
                        .SetInitialValues(new NetworkDataHighForceShatter
                        {
                            shatterImpulseThreshold = placed.NetworkShatterImpulse ?? 10f,
                            shatteredDuration = placed.NetworkShatteredDuration ?? 3f,
                        });
                    break;
            }
        }

        public static string Label(NetworkMechanicKind kind) => kind switch
        {
            NetworkMechanicKind.BoostPad => "Boost Pad",
            NetworkMechanicKind.Cannon => "Cannon",
            NetworkMechanicKind.BreakingPlatform => "Breaking Platform",
            NetworkMechanicKind.GlassShatter => "Glass",
            NetworkMechanicKind.HighForceGlass => "High-Force Glass",
            NetworkMechanicKind.HelpBeam => "Help Beam",
            _ => "Network object",
        };

        public static NetworkSpawnerPoint AddSpawner(GameObject host, NetworkMechanicKind kind) => kind switch
        {
            NetworkMechanicKind.BoostPad => host.AddComponent<NetworkSpawnerPointBoostPad>(),
            NetworkMechanicKind.Cannon => host.AddComponent<NetworkSpawnerPointCannon>(),
            NetworkMechanicKind.BreakingPlatform => host.AddComponent<NetworkSpawnerPointTouchBreakingPlatform>(),
            NetworkMechanicKind.GlassShatter => host.AddComponent<GlassShatterSpawner>(),
            NetworkMechanicKind.HighForceGlass => host.AddComponent<HighForceShatterSpawner>(),
            NetworkMechanicKind.HelpBeam => host.AddComponent<NetworkSpawnerHelpBeam>(),
            _ => null,
        };

        // Values a placement starts from when neither the map nor the editor overrides
        // them: the prefab's own designer data.
        public static void ReadPrefabDefaults(GameObject prefabRoot, NetworkMechanicKind kind, PlacedObject into)
        {
            switch (kind)
            {
                case NetworkMechanicKind.BoostPad:
                {
                    var data = prefabRoot.GetComponentInChildren<NetworkedInteractableBoostPad>(true)?.dataEditor;
                    into.NetworkBoostForce = data?.boostForce ?? DEFAULT_BOOST_FORCE;
                    into.NetworkBoostAngle = data?.targetAngle ?? 45f;
                    break;
                }
                case NetworkMechanicKind.Cannon:
                {
                    var data = prefabRoot.GetComponentInChildren<NetworkedInteractableCannon>(true)?.dataEditor;
                    into.NetworkCannonForce = data?.launchForce ?? 20f;
                    into.NetworkCannonAngle = data?.launchAngle ?? 45f;
                    into.NetworkCannonAirControlBlock = data?.blockAirControlDuration ?? 0.5f;
                    break;
                }
                case NetworkMechanicKind.BreakingPlatform:
                {
                    var data = prefabRoot.GetComponentInChildren<NetworkedInteractableTouchBreakingPlatform>(true)?.dataEditor;
                    into.NetworkBreakDelay = data?.delayBeforeBreak ?? 0.5f;
                    into.NetworkBreakDuration = data?.breakDuration ?? 3f;
                    break;
                }
                case NetworkMechanicKind.GlassShatter:
                {
                    var data = prefabRoot.GetComponentInChildren<NetworkedGlassShatter>(true)?.dataEditor;
                    into.NetworkShatteredDuration = data?.shatteredDuration ?? 3f;
                    break;
                }
                case NetworkMechanicKind.HighForceGlass:
                {
                    var data = prefabRoot.GetComponentInChildren<NetworkedHighForceShatter>(true)?.dataEditor;
                    into.NetworkShatterImpulse = data?.shatterImpulseThreshold ?? 10f;
                    into.NetworkShatteredDuration = data?.shatteredDuration ?? 3f;
                    break;
                }
            }
        }

        // Hands the placement's values to the spawner before InitializeNobServer runs.
        public static void ConfigureSpawner(NetworkSpawnerPoint spawner, PlacedObject placed)
        {
            switch (placed.NetworkKind)
            {
                case NetworkMechanicKind.BoostPad:
                    spawner.Cast<NetworkSpawnerPointBoostPad>().boostPadData = new NetworkDataBoostPad
                    {
                        boostForce = placed.NetworkBoostForce.Value,
                        targetAngle = placed.NetworkBoostAngle.Value,
                    };
                    break;
                case NetworkMechanicKind.Cannon:
                    spawner.Cast<NetworkSpawnerPointCannon>().cannonData = new NetworkDataCannon
                    {
                        launchAngle = placed.NetworkCannonAngle.Value,
                        launchForce = placed.NetworkCannonForce.Value,
                        blockAirControlDuration = placed.NetworkCannonAirControlBlock.Value,
                    };
                    break;
                case NetworkMechanicKind.BreakingPlatform:
                    spawner.Cast<NetworkSpawnerPointTouchBreakingPlatform>().breakingPlatformData =
                        new NetworkDataTouchBreakingPlatform
                        {
                            delayBeforeBreak = placed.NetworkBreakDelay.Value,
                            breakDuration = placed.NetworkBreakDuration.Value,
                        };
                    break;
                case NetworkMechanicKind.GlassShatter:
                    spawner.Cast<GlassShatterSpawner>().shatterData = new NetworkDataGlassShatter
                    {
                        shatteredDuration = placed.NetworkShatteredDuration.Value,
                    };
                    break;
                case NetworkMechanicKind.HighForceGlass:
                    spawner.Cast<HighForceShatterSpawner>().shatterData = new NetworkDataHighForceShatter
                    {
                        shatterImpulseThreshold = placed.NetworkShatterImpulse.Value,
                        shatteredDuration = placed.NetworkShatteredDuration.Value,
                    };
                    break;
            }
        }

        // Same bounds the editor's inputs enforce; also guards values arriving from
        // map files and peers.
        public static void Clamp(MapObjectData data)
        {
            if (data.NetworkBoostForce.HasValue) data.NetworkBoostForce = Mathf.Clamp(data.NetworkBoostForce.Value, 0f, 200f);
            if (data.NetworkBoostAngle.HasValue) data.NetworkBoostAngle = Mathf.Clamp(data.NetworkBoostAngle.Value, -89f, 89f);
            if (data.NetworkCannonForce.HasValue) data.NetworkCannonForce = Mathf.Clamp(data.NetworkCannonForce.Value, 0f, 200f);
            if (data.NetworkCannonAngle.HasValue) data.NetworkCannonAngle = Mathf.Clamp(data.NetworkCannonAngle.Value, -89f, 89f);
            if (data.NetworkCannonAirControlBlock.HasValue)
                data.NetworkCannonAirControlBlock = Mathf.Clamp(data.NetworkCannonAirControlBlock.Value, 0f, 10f);
            if (data.NetworkBreakDelay.HasValue) data.NetworkBreakDelay = Mathf.Clamp(data.NetworkBreakDelay.Value, 0f, 30f);
            if (data.NetworkBreakDuration.HasValue) data.NetworkBreakDuration = Mathf.Clamp(data.NetworkBreakDuration.Value, 0.1f, 60f);
            if (data.NetworkShatteredDuration.HasValue)
                data.NetworkShatteredDuration = Mathf.Clamp(data.NetworkShatteredDuration.Value, 0.1f, 60f);
            if (data.NetworkShatterImpulse.HasValue) data.NetworkShatterImpulse = Mathf.Clamp(data.NetworkShatterImpulse.Value, 0f, 500f);
        }

        // Catalog grouping only. Scene mechanics have no single marker component across
        // all of them, so the Addressables names the game ships are the stable signal.
        public static bool IsMechanicAssetName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            string n = displayName.ToLowerInvariant();
            return n.StartsWith("networkedinteractable_") || n.StartsWith("networkedglassshatter")
                || n == "networkedhelpbeam" || n.StartsWith("sceneobject_")
                || n.Contains("conveyor") || n.Contains("zipline") || n.Contains("movingplatform")
                || n.Contains("swinghammer") || n.StartsWith("hammer") || n.StartsWith("magnet")
                || n.Contains("breakingautoplatform") || n.Contains("breakingrings")
                || n.Contains("helpbeamzone") || n.StartsWith("jelly");
        }
    }
}
