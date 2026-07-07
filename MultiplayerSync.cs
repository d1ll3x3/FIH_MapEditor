using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace FIHMapEditor
{
    // Co-edit over Steam P2P: every modded player broadcasts the full map (debounced)
    // when they edit; receivers apply it — last writer wins. Rides the classic
    // SteamNetworking P2P API on our own channel, fully independent from the game's
    // FishySteamworks traffic. The game's SteamManager already initialized SteamAPI;
    // we must never call SteamAPI.Init/Shutdown ourselves.
    //
    // Session handshake without callbacks: both sides beacon HELLO to every lobby
    // member — mutual SendP2PPacket implicitly accepts the P2P session.
    public class MultiplayerSync
    {
        private const int CHANNEL = 42;
        private static readonly byte[] MAGIC = { (byte)'F', (byte)'I', (byte)'H', (byte)'1' };
        private const byte MSG_HELLO = 1;
        private const byte MSG_MAP = 2;
        private const byte MSG_REQUEST = 3;

        private const float HELLO_INTERVAL = 3f;
        private const float PEER_TIMEOUT = 10f;
        private const float BROADCAST_DEBOUNCE = 1.5f;
        private const int MAX_PACKET = 900_000;   // classic P2P reliable limit is ~1 MB

        private class Peer
        {
            public ulong Id;
            public string Name = "?";
            public float LastHello;
            public int LastAppliedRevision;
        }

        private class MapEnvelope
        {
            public int Rev { get; set; }
            public string Sender { get; set; }
            public MapFile Map { get; set; }
        }

        private readonly EditorController _c;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();

        // Always on: discovery is idle-cheap in singleplayer, and syncing must never
        // depend on someone remembering a menu toggle.
        public bool Enabled => true;
        public string LastSyncInfo { get; private set; } = "";

        private float _nextHelloAt;
        private float _nextLobbyRefreshAt;
        private float _broadcastAt = -1f;         // pending debounced broadcast, -1 = none
        private int _revision;
        private bool _applyingRemote;

        // Map received while in Play mode — applied on returning to the editor.
        private MapEnvelope _queuedRemote;

        // Lobby lookup via Il2Cpp reflection (type name may move between game versions),
        // cached with cooldown (GameObjectFinder pattern).
        private MonoBehaviour _lobby;
        private Il2CppSystem.Reflection.MethodInfo _getLobbyMembers;
        private float _lobbySearchCooldown;
        private ulong _selfId;
        private readonly List<ulong> _lobbyMembers = new List<ulong>();

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

        // Local edit happened: schedule a debounced broadcast.
        public void NotifyDirty()
        {
            if (!Enabled || _applyingRemote) return;
            _broadcastAt = Time.unscaledTime + BROADCAST_DEBOUNCE;
        }

        public void OnEnteredEditor()
        {
            if (_queuedRemote != null)
            {
                var env = _queuedRemote;
                _queuedRemote = null;
                ApplyEnvelope(env);
            }
        }

        public void OnSceneLeft()
        {
            _broadcastAt = -1f;
            _queuedRemote = null;
            _lobby = null;
            _lastLoggedMemberCount = -1;
        }

        public void Update()
        {
            if (!Enabled) return;
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
                    BroadcastMap();
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Update error: {ex.Message}");
            }
        }

        public void ForceBroadcast()
        {
            if (!Enabled) return;
            _broadcastAt = -1f;
            BroadcastMap();
        }

        // ───────────────────────────────────────────────────────────── discovery ──

        private int _lastLoggedMemberCount = -1;

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

        // ─────────────────────────────────────────────────────────────── sending ──

        private void SendHello()
        {
            string name = "player";
            try { name = SteamFriends.GetPersonaName(); } catch { }
            var payload = Encoding.UTF8.GetBytes(name);
            foreach (var id in _lobbyMembers)
                SendTo(id, MSG_HELLO, payload);
        }

        private void BroadcastMap()
        {
            var fresh = FreshPeers();
            if (fresh.Count == 0) return;

            byte[] payload;
            try
            {
                payload = BuildMapPayload();
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Map serialize error: {ex.Message}");
                return;
            }
            if (payload.Length > MAX_PACKET)
            {
                _c.ShowToast($"Co-edit: map too large to sync ({payload.Length / 1024} KB)");
                return;
            }

            foreach (var peer in fresh)
                SendTo(peer.Id, MSG_MAP, payload);

            LastSyncInfo = $"sent rev {_revision} to {fresh.Count} peer(s) ({payload.Length / 1024} KB)";
            MapEditorPlugin.Logger.LogInfo($"[COOP] {LastSyncInfo}");
        }

        private byte[] BuildMapPayload()
        {
            _revision++;
            string sender = "player";
            try { sender = SteamFriends.GetPersonaName(); } catch { }
            var envelope = new MapEnvelope { Rev = _revision, Sender = sender, Map = _c.BuildMapFile() };
            string json = System.Text.Json.JsonSerializer.Serialize(envelope,
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                });
            return Gzip(Encoding.UTF8.GetBytes(json));
        }

        private List<Peer> FreshPeers()
        {
            var list = new List<Peer>();
            foreach (var p in _peers.Values)
                if (Time.unscaledTime - p.LastHello < PEER_TIMEOUT) list.Add(p);
            return list;
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
                            // Late joiner: ask them for their map state too — whichever
                            // side has content will answer.
                            SendTo(senderId, MSG_REQUEST, Array.Empty<byte>());
                        }
                        break;
                    }

                    case MSG_MAP:
                    {
                        var json = Encoding.UTF8.GetString(Gunzip(payload));
                        var envelope = System.Text.Json.JsonSerializer.Deserialize<MapEnvelope>(json,
                            new System.Text.Json.JsonSerializerOptions
                            {
                                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                            });
                        if (envelope?.Map == null) break;

                        if (_peers.TryGetValue(senderId, out var mapPeer))
                        {
                            if (envelope.Rev <= mapPeer.LastAppliedRevision) break;   // stale
                            mapPeer.LastAppliedRevision = envelope.Rev;
                        }

                        if (_c.Mode == EditorMode.Play)
                        {
                            _queuedRemote = envelope;
                            _c.ShowToast($"Co-edit: map update from {envelope.Sender} (applies after Play)");
                        }
                        else
                        {
                            ApplyEnvelope(envelope);
                        }
                        break;
                    }

                    case MSG_REQUEST:
                    {
                        if (_c.HasMapContent())
                        {
                            var payload2 = BuildMapPayload();
                            if (payload2.Length <= MAX_PACKET) SendTo(senderId, MSG_MAP, payload2);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] Message error (type {type}): {ex.Message}");
            }
        }

        private void ApplyEnvelope(MapEnvelope envelope)
        {
            _applyingRemote = true;
            try
            {
                _c.ApplyRemoteMap(envelope.Map, envelope.Sender);
                LastSyncInfo = $"applied rev {envelope.Rev} from {envelope.Sender}";
            }
            finally
            {
                _applyingRemote = false;
            }
        }

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
