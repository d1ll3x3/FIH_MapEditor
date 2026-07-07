using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FIHMapEditor
{
    public class LeaderboardEntry
    {
        [JsonPropertyName("steam_id")] public long SteamId { get; set; }
        [JsonPropertyName("player_name")] public string PlayerName { get; set; }
        [JsonPropertyName("time_seconds")] public double TimeSeconds { get; set; }
    }

    // Global per-map leaderboard client. Talks to the shared Supabase (PostgREST)
    // backend over HTTPS: submit-if-better via an RPC, fetch a sorted board via a
    // filtered SELECT. Everyone points at the same backend (see Supabase.cs), which is
    // what makes the board global — you see everyone's times without ever being in the
    // same game.
    //
    // Threading: the game must never block on the network, so every call runs on a
    // background Task. Results are published by atomically swapping an immutable list
    // reference (_boards[mapId]); the GUI reads that reference with no locks. The
    // service only ever touches plain POCOs — never Unity objects off-thread.
    public class LeaderboardService
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private class Board
        {
            public volatile List<LeaderboardEntry> Entries = new List<LeaderboardEntry>();
            public volatile bool Loading;
            public volatile string Error;      // null when OK
            public volatile bool EverLoaded;
            public float FetchedAt = -999f;
        }

        private readonly Dictionary<string, Board> _boards = new Dictionary<string, Board>();

        public bool Configured => Supabase.Configured;

        private Board GetOrAdd(string mapId)
        {
            if (!_boards.TryGetValue(mapId, out var b))
            {
                b = new Board();
                _boards[mapId] = b;
            }
            return b;
        }

        // ── UI-facing readers (main thread) ──────────────────────────────────────

        public IReadOnlyList<LeaderboardEntry> GetEntries(string mapId)
            => mapId != null && _boards.TryGetValue(mapId, out var b) ? b.Entries
               : (IReadOnlyList<LeaderboardEntry>)Array.Empty<LeaderboardEntry>();

        public bool IsLoading(string mapId)
            => mapId != null && _boards.TryGetValue(mapId, out var b) && b.Loading;

        public string GetError(string mapId)
            => mapId != null && _boards.TryGetValue(mapId, out var b) ? b.Error : null;

        public bool EverLoaded(string mapId)
            => mapId != null && _boards.TryGetValue(mapId, out var b) && b.EverLoaded;

        // ── Fetch ────────────────────────────────────────────────────────────────

        // Pull the sorted board for a map. Debounced so repeated GUI frames / tab
        // reopens don't spam the backend; pass force to bypass the debounce (Refresh).
        public void FetchBoard(string mapId, bool force = false)
        {
            if (!Configured || string.IsNullOrEmpty(mapId)) return;
            var b = GetOrAdd(mapId);
            if (b.Loading) return;
            if (!force && UnityEngine.Time.unscaledTime - b.FetchedAt < 3f) return;

            b.Loading = true;
            b.FetchedAt = UnityEngine.Time.unscaledTime;
            _ = FetchAsync(mapId, b);
        }

        private async Task FetchAsync(string mapId, Board b)
        {
            try
            {
                string url = $"{Supabase.Url}/rest/v1/times" +
                             $"?map_id=eq.{Uri.EscapeDataString(mapId)}" +
                             "&select=steam_id,player_name,time_seconds" +
                             "&order=time_seconds.asc&limit=200";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                Supabase.AddAuth(req);

                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    b.Error = $"HTTP {(int)resp.StatusCode}";
                    MapEditorPlugin.Logger.LogWarning($"[TIMES] Fetch failed {(int)resp.StatusCode}: {Supabase.Trim(body)}");
                    return;
                }

                var list = JsonSerializer.Deserialize<List<LeaderboardEntry>>(body, Json)
                           ?? new List<LeaderboardEntry>();
                b.Entries = list;         // atomic reference swap — GUI picks it up next frame
                b.Error = null;
                b.EverLoaded = true;
            }
            catch (Exception ex)
            {
                b.Error = ex.Message;
                MapEditorPlugin.Logger.LogWarning($"[TIMES] Fetch error: {ex.Message}");
            }
            finally
            {
                b.Loading = false;
            }
        }

        // ── Submit ───────────────────────────────────────────────────────────────

        // Upload a time (fire-and-forget). The backend RPC keeps only the best per
        // player, so re-submitting a worse time is a harmless no-op server-side.
        public void SubmitTime(string mapId, string playerName, long steamId, double seconds)
        {
            if (!Configured || string.IsNullOrEmpty(mapId)) return;
            if (!EditorConfig.Settings.SubmitTimesOnline) return;
            if (steamId == 0) return;   // no Steam identity → don't pollute the board
            _ = SubmitAsync(mapId, playerName ?? "player", steamId, seconds);
        }

        private async Task SubmitAsync(string mapId, string playerName, long steamId, double seconds)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["p_map_id"] = mapId,
                    ["p_steam_id"] = steamId,
                    ["p_name"] = playerName,
                    ["p_seconds"] = seconds,
                };
                string json = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/submit_time");
                Supabase.AddAuth(req);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    MapEditorPlugin.Logger.LogWarning($"[TIMES] Submit failed {(int)resp.StatusCode}: {Supabase.Trim(body)}");
                }
                else
                {
                    MapEditorPlugin.Logger.LogInfo($"[TIMES] Submitted {seconds:0.000}s for map {mapId}.");
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[TIMES] Submit error: {ex.Message}");
            }
        }
    }
}
