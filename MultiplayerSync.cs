using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace FIHMapEditor
{
    // Co-edit over Steam P2P: every modded player's edits sync automatically to every
    // other modded player in the lobby — no menu toggle. Rides the classic
    // SteamNetworking P2P API on our own channel, fully independent from the game's
    // FishySteamworks traffic. The game's SteamManager already initialized SteamAPI;
    // we must never call SteamAPI.Init/Shutdown ourselves.
    //
    // Protocol: per-KEY last-writer-wins (a "key" is one object/checkpoint/reset zone/
    // level edit/spawn/goal/base-mode/map-name — see BuildCurrentSnapshotJson). Every
    // change is diffed against the last-sent snapshot and sent as a small batch of ops,
    // each carrying a monotonically increasing per-key revision + the sender's SteamID
    // as a tie-breaker. Applying an op mutates ONLY that one unit directly (move an
    // existing GameObject, spawn/delete one clone...) — never a full map rebuild — so:
    //   - three or more people editing DIFFERENT objects never conflict (different keys)
    //   - a stale/out-of-order op can never roll back a newer edit (rev comparison)
    //   - steady-state editing never stutters (only the changed unit is touched)
    // A brand-new peer's full state is served on request as one batch of upserts, using
    // the exact same op format and the real (rev, editor) each key was last set to.
    //
    // Session handshake without callbacks: both sides beacon HELLO to every lobby
    // member — mutual SendP2PPacket implicitly accepts the P2P session.
    public class MultiplayerSync
    {
        private const int CHANNEL = 42;
        private static readonly byte[] MAGIC = { (byte)'F', (byte)'I', (byte)'H', (byte)'2' };
        private const byte MSG_HELLO = 1;
        private const byte MSG_OPS = 2;
        private const byte MSG_REQUEST = 3;
        private const byte MSG_BALL = 4;   // soccer ball state / kick requests

        private const float HELLO_INTERVAL = 3f;
        private const float PEER_TIMEOUT = 10f;
        private const float BROADCAST_DEBOUNCE = 1.5f;
        private const int MAX_PACKET = 900_000;   // classic P2P reliable limit is ~1 MB

        private class Peer
        {
            public ulong Id;
            public string Name = "?";
            public float LastHello;
        }

        // One change to one syncable unit. Payload is that unit's own JSON (or null for
        // a delete/clear) — a small amount of double-encoding for a lot of protocol
        // simplicity (one envelope shape for every kind of change).
        private class SyncOp
        {
            public string Key { get; set; }
            public int Kind { get; set; }
            public int Rev { get; set; }
            public ulong Editor { get; set; }
            public string Payload { get; set; }
        }

        // Soccer ball state (authority → followers) or kick request (follower → authority).
        private class BallEnvelope
        {
            public string MapId { get; set; }
            public float[] P { get; set; }
            public float[] V { get; set; }
            public int A { get; set; }
            public int B { get; set; }
            public bool K { get; set; }   // true = kick request
        }

        // Kinds
        private const int K_OBJ_UPSERT = 0, K_OBJ_DELETE = 1;
        private const int K_CP_UPSERT = 2, K_CP_DELETE = 3;
        private const int K_RZ_UPSERT = 4, K_RZ_DELETE = 5;
        private const int K_LVL_UPSERT = 6, K_LVL_REVERT = 7;
        private const int K_SPAWN = 8, K_SPAWN_CLEAR = 9;
        private const int K_GOAL = 10, K_GOAL_CLEAR = 11;
        private const int K_BASEMODE = 12, K_MAPNAME = 13;
        private const int K_MAPID = 14;   // whole-map swaps re-key the leaderboard too
        private const int K_EDITABLE = 15; // play-only lock must follow a synced map swap

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly EditorController _c;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();

        // Always on: discovery is idle-cheap in singleplayer, and syncing must never
        // depend on someone remembering a menu toggle.
        public string LastSyncInfo { get; private set; } = "";

        private float _nextHelloAt;
        private float _nextLobbyRefreshAt;
        private float _broadcastAt = -1f;         // pending debounced broadcast, -1 = none
        private bool _applyingRemote;

        // Per-key LWW state: the (revision, editor SteamID) currently applied, and the
        // JSON we last sent/accepted for it (used both to diff local changes and to
        // seed a new peer's full state without re-deriving fresh revisions).
        private readonly Dictionary<string, (int rev, ulong editor)> _appliedRevs = new Dictionary<string, (int, ulong)>();
        private Dictionary<string, string> _lastSentJson = new Dictionary<string, string>();

        // Ops received while the local player is in the middle of a run — applied once
        // they return to the editor, so a sync never disturbs an active Play session.
        private readonly List<SyncOp> _queuedOps = new List<SyncOp>();

        // Lobby lookup via Il2Cpp reflection (type name may move between game versions),
        // cached with cooldown (GameObjectFinder pattern).
        private MonoBehaviour _lobby;
        private Il2CppSystem.Reflection.MethodInfo _getLobbyMembers;
        private float _lobbySearchCooldown;
        private ulong _selfId;
        private readonly List<ulong> _lobbyMembers = new List<ulong>();
        private int _lastLoggedMemberCount = -1;

        public MultiplayerSync(EditorController controller)
        {
            _c = controller;
        }

        public int PeerCount
        {
            get
            {
                int n = 0;
                foreach (var p in _peers.Values)
                    if (Time.unscaledTime - p.LastHello < PEER_TIMEOUT) n++;
                return n;
            }
        }

        public string StatusLine
        {
            get
            {
                if (_lobby == null) return "looking for the game's lobby…";
                if (_lobbyMembers.Count == 0) return "solo (no other players in lobby)";
                int peers = PeerCount;
                return peers == 0
                    ? $"{_lobbyMembers.Count} player(s) in lobby — none with the mod yet"
                    : $"syncing with {peers} modded player(s) ({_lobbyMembers.Count} in lobby)";
            }
        }

        // Local edit happened: schedule a debounced diff+broadcast.
        public void NotifyDirty()
        {
            if (_applyingRemote) return;
            _broadcastAt = Time.unscaledTime + BROADCAST_DEBOUNCE;
        }

        // Flush anything that arrived while we were away from the editor (Play mode /
        // scene reload), in arrival order — the per-op revision check makes the result
        // the same regardless of order, so this is just "catch up now".
        public void OnEnteredEditor()
        {
            if (_queuedOps.Count == 0) return;
            var ops = new List<SyncOp>(_queuedOps);
            _queuedOps.Clear();
            _applyingRemote = true;
            try { foreach (var op in ops) ApplyVisual(op); }
            finally { _applyingRemote = false; }
        }

        public void OnSceneLeft()
        {
            _broadcastAt = -1f;
            _lobby = null;
            _lastLoggedMemberCount = -1;
        }

        public void Update()
        {
            try
            {
                if (Time.unscaledTime >= _nextLobbyRefreshAt)
                {
                    _nextLobbyRefreshAt = Time.unscaledTime + 3f;
                    RefreshLobbyMembers();
                }

                if (_lobbyMembers.Count > 0 && Time.unscaledTime >= _nextHelloAt)
                {
                    _nextHelloAt = Time.unscaledTime + HELLO_INTERVAL;
                    SendHello();
                }

                DrainPackets();

                if (_broadcastAt > 0 && Time.unscaledTime >= _broadcastAt)
                {
                    _broadcastAt = -1f;
                    BroadcastPendingDiff();
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Update error: {ex.Message}");
            }
        }

        // "Send map now" button: force an immediate diff+broadcast instead of waiting
        // for the debounce.
        public void ForceBroadcast()
        {
            _broadcastAt = -1f;
            BroadcastPendingDiff();
        }

        // ───────────────────────────────────────────────────────────── discovery ──

        private void RefreshLobbyMembers()
        {
            _lobbyMembers.Clear();
            try
            {
                if (_lobby == null)
                {
                    if (Time.unscaledTime < _lobbySearchCooldown) return;
                    FindLobbyObject();
                    if (_lobby == null)
                    {
                        _lobbySearchCooldown = Time.unscaledTime + 5f;
                        return;
                    }
                }

                if (_selfId == 0)
                    _selfId = SteamUser.GetSteamID().m_SteamID;

                var boxed = _getLobbyMembers.Invoke(_lobby, null);
                if (boxed != null)
                {
                    // The boxed result is an Il2Cpp array; rewrap it from the raw pointer.
                    var members = new Il2CppStructArray<CSteamID>(boxed.Pointer);
                    foreach (var m in members)
                    {
                        ulong id = m.m_SteamID;
                        if (id == 0 || id == _selfId) continue;
                        _lobbyMembers.Add(id);
                    }
                }

                // Second route: ask Steam directly with the lobby id from the manager.
                if (_lobbyMembers.Count == 0)
                    RefreshViaMatchmaking();

                if (_lobbyMembers.Count != _lastLoggedMemberCount)
                {
                    _lastLoggedMemberCount = _lobbyMembers.Count;
                    MapEditorPlugin.Logger.LogInfo($"[COOP] Lobby members (excluding self): {_lobbyMembers.Count}");
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Lobby refresh error: {ex.Message}");
                _lobby = null;
                _getLobbyMembers = null;
                _lobbySearchCooldown = Time.unscaledTime + 5f;
            }
        }

        // Fallback path: read the manager's CurrentLobbySteamId property and enumerate
        // members through SteamMatchmaking ourselves.
        private void RefreshViaMatchmaking()
        {
            try
            {
                var prop = _lobby.GetIl2CppType().GetProperty("CurrentLobbySteamId");
                var getter = prop?.GetGetMethod();
                var boxed = getter?.Invoke(_lobby, null);
                if (boxed == null) return;

                var lobbyId = boxed.Unbox<CSteamID>();
                if (lobbyId.m_SteamID == 0) return;

                int n = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                for (int i = 0; i < n; i++)
                {
                    ulong id = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID;
                    if (id == 0 || id == _selfId) continue;
                    if (!_lobbyMembers.Contains(id)) _lobbyMembers.Add(id);
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Matchmaking fallback error: {ex.Message}");
            }
        }

        // The game's lobby manager is EHS.Steam.SteamManager (verified via interop
        // reflection): a MonoBehaviour exposing GetLobbyMembers(). Match by METHOD, not
        // by an exact type name, so build renames don't break discovery again.
        private void FindLobbyObject()
        {
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                Il2CppSystem.Type type = null;
                try { type = mb.GetIl2CppType(); }
                catch { }
                if (type == null || !type.Name.Contains("Steam")) continue;

                Il2CppSystem.Reflection.MethodInfo method = null;
                try { method = type.GetMethod("GetLobbyMembers"); }
                catch { }
                if (method == null) continue;

                _lobby = mb;
                _getLobbyMembers = method;
                MapEditorPlugin.Logger.LogInfo($"[COOP] Lobby manager found: {type.FullName}");
                return;
            }
        }

        // ────────────────────────────────────────────────────────────── snapshot ──

        // One JSON entry per syncable unit, keyed so different objects/markers never
        // collide and can be diffed/applied independently.
        private Dictionary<string, string> BuildCurrentSnapshotJson()
        {
            var map = _c.BuildMapFile();
            var snap = new Dictionary<string, string>();

            foreach (var obj in map.Objects)
                if (!string.IsNullOrEmpty(obj.Uid))
                    snap["obj:" + obj.Uid] = JsonSerializer.Serialize(obj, JsonOpts);
            foreach (var cp in map.Checkpoints)
                if (!string.IsNullOrEmpty(cp.Uid))
                    snap["cp:" + cp.Uid] = JsonSerializer.Serialize(cp, JsonOpts);
            foreach (var z in map.ResetZones)
                if (!string.IsNullOrEmpty(z.Uid))
                    snap["rz:" + z.Uid] = JsonSerializer.Serialize(z, JsonOpts);
            foreach (var le in map.LevelEdits)
                if (!string.IsNullOrEmpty(le.Path))
                    snap["lvl:" + le.Path] = JsonSerializer.Serialize(le, JsonOpts);
            if (map.Spawn != null) snap["spawn"] = JsonSerializer.Serialize(map.Spawn, JsonOpts);
            if (map.Goal != null) snap["goal"] = JsonSerializer.Serialize(map.Goal, JsonOpts);
            snap["basemode"] = JsonSerializer.Serialize(map.BaseMode.ToString());
            snap["mapname"] = JsonSerializer.Serialize(map.Name ?? "");
            snap["mapid"] = JsonSerializer.Serialize(map.MapId ?? "");
            snap["editable"] = JsonSerializer.Serialize(map.Editable);
            return snap;
        }

        private static int KindForUpsert(string key)
        {
            if (key.StartsWith("obj:")) return K_OBJ_UPSERT;
            if (key.StartsWith("cp:")) return K_CP_UPSERT;
            if (key.StartsWith("rz:")) return K_RZ_UPSERT;
            if (key.StartsWith("lvl:")) return K_LVL_UPSERT;
            if (key == "spawn") return K_SPAWN;
            if (key == "goal") return K_GOAL;
            if (key == "basemode") return K_BASEMODE;
            if (key == "mapname") return K_MAPNAME;
            if (key == "mapid") return K_MAPID;
            if (key == "editable") return K_EDITABLE;
            return -1;
        }

        private static int KindForDelete(string key)
        {
            if (key.StartsWith("obj:")) return K_OBJ_DELETE;
            if (key.StartsWith("cp:")) return K_CP_DELETE;
            if (key.StartsWith("rz:")) return K_RZ_DELETE;
            if (key.StartsWith("lvl:")) return K_LVL_REVERT;
            if (key == "spawn") return K_SPAWN_CLEAR;
            if (key == "goal") return K_GOAL_CLEAR;
            return -1; // basemode/mapname/mapid are never absent
        }

        // Diffs the live map against what we last sent, bumping this session's own
        // per-key revision for anything that changed. Pure bookkeeping — callers decide
        // whether/where to send the resulting ops.
        private List<SyncOp> ComputeDiff()
        {
            // Normally set during lobby discovery, but a diff can run before the lobby
            // is found (a peer's HELLO can arrive first) — ops stamped with Editor=0
            // would lose every same-rev tie-break, so resolve the id here too.
            if (_selfId == 0)
                try { _selfId = SteamUser.GetSteamID().m_SteamID; } catch { }

            var current = BuildCurrentSnapshotJson();
            var ops = new List<SyncOp>();

            foreach (var kv in current)
            {
                if (_lastSentJson.TryGetValue(kv.Key, out var prev) && prev == kv.Value) continue;
                int rev = (_appliedRevs.TryGetValue(kv.Key, out var cur) ? cur.rev : 0) + 1;
                _appliedRevs[kv.Key] = (rev, _selfId);
                ops.Add(new SyncOp { Key = kv.Key, Kind = KindForUpsert(kv.Key), Rev = rev, Editor = _selfId, Payload = kv.Value });
            }
            foreach (var key in _lastSentJson.Keys)
            {
                if (current.ContainsKey(key)) continue;
                int kind = KindForDelete(key);
                if (kind < 0) continue;
                int rev = (_appliedRevs.TryGetValue(key, out var cur) ? cur.rev : 0) + 1;
                _appliedRevs[key] = (rev, _selfId);
                ops.Add(new SyncOp { Key = key, Kind = kind, Rev = rev, Editor = _selfId, Payload = null });
            }

            _lastSentJson = current;
            return ops;
        }

        // Everything currently live, as upserts — the exact same op format as a normal
        // diff, so a joining peer's application code path is identical either way.
        private List<SyncOp> ComputeFullSeed()
        {
            var pending = ComputeDiff();
            var ops = new List<SyncOp>(pending);
            var seen = new HashSet<string>();
            foreach (var op in pending) seen.Add(op.Key);

            foreach (var kv in _lastSentJson)
            {
                if (seen.Contains(kv.Key)) continue;
                if (!_appliedRevs.TryGetValue(kv.Key, out var rev)) continue;
                ops.Add(new SyncOp { Key = kv.Key, Kind = KindForUpsert(kv.Key), Rev = rev.rev, Editor = rev.editor, Payload = kv.Value });
            }
            return ops;
        }

        // ─────────────────────────────────────────────────────────────── sending ──

        private void SendHello()
        {
            string name = "player";
            try { name = SteamFriends.GetPersonaName(); } catch { }
            var payload = Encoding.UTF8.GetBytes(name);
            foreach (var id in _lobbyMembers)
                SendTo(id, MSG_HELLO, payload);
        }

        private void BroadcastPendingDiff()
        {
            var ops = ComputeDiff();
            if (ops.Count == 0) return;

            var fresh = FreshPeers();
            if (fresh.Count == 0) return; // still tracked locally; a future REQUEST will pick it up

            SendOps(fresh.Select(p => p.Id), ops);
            LastSyncInfo = $"sent {ops.Count} change(s) to {fresh.Count} peer(s)";
            MapEditorPlugin.Logger.LogInfo($"[COOP] {LastSyncInfo}");
        }

        private void SendOps(IEnumerable<ulong> targets, List<SyncOp> ops)
        {
            byte[] payload;
            try
            {
                string json = JsonSerializer.Serialize(ops, JsonOpts);
                payload = Gzip(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Ops serialize error: {ex.Message}");
                return;
            }
            if (payload.Length > MAX_PACKET)
            {
                _c.ShowToast($"Co-edit: sync batch too large ({payload.Length / 1024} KB) — some changes may not sync");
                return;
            }
            foreach (var id in targets)
                SendTo(id, MSG_OPS, payload);
        }

        private List<Peer> FreshPeers()
        {
            var list = new List<Peer>();
            foreach (var p in _peers.Values)
                if (Time.unscaledTime - p.LastHello < PEER_TIMEOUT) list.Add(p);
            return list;
        }

        // ─────────────────────────────────────────────────────────── soccer ball ──

        // The lobby's lowest modded SteamID simulates the ball and counts goals; the
        // rest run kinematic followers. Alone (no fresh peers) = always authority.
        public bool IsBallAuthority
        {
            get
            {
                if (_selfId == 0)
                    try { _selfId = SteamUser.GetSteamID().m_SteamID; } catch { return true; }
                foreach (var p in FreshPeers())
                    if (p.Id != 0 && p.Id < _selfId) return false;
                return true;
            }
        }

        // Broadcast ball state (authority) or a kick request (follower). Tiny payload,
        // sent uncompressed-ish (gzip anyway for the shared framing).
        public void SendBall(Vector3 pos, Vector3 vel, int a, int b, bool kick)
        {
            try
            {
                var fresh = FreshPeers();
                if (fresh.Count == 0) return;

                var env = new BallEnvelope
                {
                    MapId = _c.MapId,
                    P = VecUtil.ToArray(pos),
                    V = VecUtil.ToArray(vel),
                    A = a,
                    B = b,
                    K = kick,
                };
                var payload = Gzip(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env, JsonOpts)));
                foreach (var peer in fresh)
                    SendTo(peer.Id, MSG_BALL, payload);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Ball send error: {ex.Message}");
            }
        }

        private void SendTo(ulong steamId, byte type, byte[] payload)
        {
            try
            {
                var data = new byte[MAGIC.Length + 1 + payload.Length];
                Array.Copy(MAGIC, data, MAGIC.Length);
                data[MAGIC.Length] = type;
                Array.Copy(payload, 0, data, MAGIC.Length + 1, payload.Length);

                SteamNetworking.SendP2PPacket(new CSteamID(steamId), data, (uint)data.Length,
                    EP2PSend.k_EP2PSendReliable, CHANNEL);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Send error: {ex.Message}");
            }
        }

        // ───────────────────────────────────────────────────────────── receiving ──

        private void DrainPackets()
        {
            int safety = 16;
            while (safety-- > 0 && SteamNetworking.IsP2PPacketAvailable(out uint size, CHANNEL))
            {
                // Must pass a real Il2Cpp array: the implicit byte[] conversion would
                // hand Steam a temporary copy and our managed buffer would stay empty.
                var buffer = new Il2CppStructArray<byte>(size);
                if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint msgSize, out CSteamID sender, CHANNEL))
                    break;

                if (msgSize < MAGIC.Length + 1) continue;
                var data = new byte[msgSize];
                for (int i = 0; i < msgSize; i++) data[i] = buffer[i];

                bool magicOk = true;
                for (int i = 0; i < MAGIC.Length; i++)
                    if (data[i] != MAGIC[i]) { magicOk = false; break; }
                if (!magicOk) continue;

                byte type = data[MAGIC.Length];
                var payload = new byte[msgSize - MAGIC.Length - 1];
                Array.Copy(data, MAGIC.Length + 1, payload, 0, payload.Length);

                HandleMessage(sender.m_SteamID, type, payload);
            }
        }

        private void HandleMessage(ulong senderId, byte type, byte[] payload)
        {
            try
            {
                switch (type)
                {
                    case MSG_HELLO:
                    {
                        bool isNew = !_peers.TryGetValue(senderId, out var peer);
                        if (isNew)
                        {
                            peer = new Peer { Id = senderId };
                            _peers[senderId] = peer;
                        }
                        peer.LastHello = Time.unscaledTime;
                        try { peer.Name = Encoding.UTF8.GetString(payload); } catch { }
                        if (isNew)
                        {
                            MapEditorPlugin.Logger.LogInfo($"[COOP] Modded peer joined: {peer.Name} ({senderId})");
                            _c.ShowToast($"Co-edit: {peer.Name} is here with the mod");
                            // Late joiner: ask them for their current state too — whoever
                            // has content answers (possibly both, harmlessly).
                            SendTo(senderId, MSG_REQUEST, Array.Empty<byte>());
                        }
                        break;
                    }

                    case MSG_OPS:
                    {
                        var json = Encoding.UTF8.GetString(Gunzip(payload));
                        var ops = JsonSerializer.Deserialize<List<SyncOp>>(json, JsonOpts);
                        if (ops == null) break;

                        bool deferVisuals = _c.Mode == EditorMode.Play;
                        _applyingRemote = true;
                        int accepted = 0;
                        try
                        {
                            foreach (var op in ops)
                            {
                                if (!TryAcceptOp(op)) continue;
                                accepted++;
                                if (deferVisuals) _queuedOps.Add(op);
                                else ApplyVisual(op);
                            }
                        }
                        finally { _applyingRemote = false; }

                        if (accepted > 0)
                            LastSyncInfo = $"received {accepted} change(s)"
                                + (deferVisuals ? " (applying after Play)" : "");
                        break;
                    }

                    case MSG_REQUEST:
                    {
                        if (_c.HasMapContent())
                        {
                            var seed = ComputeFullSeed();
                            if (seed.Count > 0) SendOps(new[] { senderId }, seed);
                        }
                        break;
                    }

                    case MSG_BALL:
                    {
                        var json = Encoding.UTF8.GetString(Gunzip(payload));
                        var env = JsonSerializer.Deserialize<BallEnvelope>(json, JsonOpts);
                        if (env?.P == null) break;
                        // Only the same map's match matters.
                        if (!string.IsNullOrEmpty(env.MapId) && env.MapId != _c.MapId) break;

                        Vector3 pos = VecUtil.ToVector3(env.P);
                        Vector3 vel = VecUtil.ToVector3(env.V);
                        int a = env.A, b = env.B;
                        bool kick = env.K;
                        _c.RunOnMainThread(() => _c.PlayMode.ApplyRemoteBall(pos, vel, a, b, kick));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Message error (type {type}): {ex.Message}");
            }
        }

        // Per-key LWW check: does this op beat what we currently have for its key? If so,
        // records the new state immediately (so a re-check — e.g. a duplicate resend —
        // never double-applies) and returns true. Purely bookkeeping; ApplyVisual does
        // the actual game-state mutation, separately, so a deferred-to-Play-end op can
        // be visually applied later without re-running (and failing) this check.
        private bool TryAcceptOp(SyncOp op)
        {
            var local = _appliedRevs.TryGetValue(op.Key, out var cur) ? cur : (rev: 0, editor: 0UL);
            bool wins = op.Rev > local.rev || (op.Rev == local.rev && op.Editor > local.editor);
            if (!wins) return false;

            _appliedRevs[op.Key] = (op.Rev, op.Editor);
            if (op.Payload != null) _lastSentJson[op.Key] = op.Payload;
            else _lastSentJson.Remove(op.Key);
            return true;
        }

        private void ApplyVisual(SyncOp op)
        {
            try
            {
                switch (op.Kind)
                {
                    case K_OBJ_UPSERT: _c.ApplyRemoteObjectUpsert(Deserialize<MapObjectData>(op.Payload)); break;
                    case K_OBJ_DELETE: _c.ApplyRemoteObjectDelete(op.Key.Substring(4)); break;
                    case K_CP_UPSERT: _c.ApplyRemoteCheckpointUpsert(Deserialize<CheckpointData>(op.Payload)); break;
                    case K_CP_DELETE: _c.ApplyRemoteCheckpointDelete(op.Key.Substring(3)); break;
                    case K_RZ_UPSERT: _c.ApplyRemoteResetZoneUpsert(Deserialize<ResetZoneData>(op.Payload)); break;
                    case K_RZ_DELETE: _c.ApplyRemoteResetZoneDelete(op.Key.Substring(3)); break;
                    case K_LVL_UPSERT: _c.ApplyRemoteLevelEditUpsert(Deserialize<LevelEditData>(op.Payload)); break;
                    case K_LVL_REVERT: _c.ApplyRemoteLevelEditRevert(op.Key.Substring(4)); break;
                    case K_SPAWN: _c.ApplyRemoteSpawn(Deserialize<SpawnPointData>(op.Payload)); break;
                    case K_SPAWN_CLEAR: _c.ApplyRemoteSpawn(null); break;
                    case K_GOAL: _c.ApplyRemoteGoal(Deserialize<GoalZoneData>(op.Payload)); break;
                    case K_GOAL_CLEAR: _c.ApplyRemoteGoal(null); break;
                    case K_BASEMODE:
                        _c.ApplyRemoteBaseMode(Enum.Parse<MapBaseMode>(Deserialize<string>(op.Payload)));
                        break;
                    case K_MAPNAME: _c.ApplyRemoteMapName(Deserialize<string>(op.Payload)); break;
                    case K_MAPID: _c.ApplyRemoteMapId(Deserialize<string>(op.Payload)); break;
                    case K_EDITABLE: _c.ApplyRemoteEditable(Deserialize<bool>(op.Payload)); break;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Apply op error (kind {op.Kind}, key {op.Key}): {ex.Message}");
            }
        }

        private static T Deserialize<T>(string json)
            => json == null ? default : JsonSerializer.Deserialize<T>(json, JsonOpts);

        // ──────────────────────────────────────────────────────────────── gzip ──

        private static byte[] Gzip(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
                gz.Write(data, 0, data.Length);
            return output.ToArray();
        }

        private static byte[] Gunzip(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }
    }
}
