using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FIHMapEditor
{
    // One row in the online map library (metadata only — no map data).
    public class OnlineMapInfo
    {
        [JsonPropertyName("map_id")] public string MapId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("author_name")] public string AuthorName { get; set; }
        [JsonPropertyName("author_steam_id")] public long AuthorSteamId { get; set; }
        [JsonPropertyName("editable")] public bool Editable { get; set; }
        [JsonPropertyName("object_count")] public int ObjectCount { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; }
    }

    // Community map repository over the shared Supabase backend: list metadata, download
    // a map's JSON, upload/update (owner-token gated) and delete your own. Same threading
    // discipline as LeaderboardService — all network on background Tasks, results handed
    // back to the main thread via callbacks the caller marshals through EditorController.
    public class OnlineMapService
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public bool Configured => Supabase.Configured;

        // Published list for the GUI (swapped atomically).
        public volatile List<OnlineMapInfo> Maps = new List<OnlineMapInfo>();
        public volatile bool Loading;
        public volatile string Error;
        public volatile bool EverLoaded;
        private float _listedAt = -999f;
        public volatile bool Busy;   // an upload/download/delete in flight

        // ── List ─────────────────────────────────────────────────────────────────

        public void RefreshList(bool force = false)
        {
            if (!Configured || Loading) return;
            if (!force && UnityEngine.Time.unscaledTime - _listedAt < 2f) return;
            Loading = true;
            _listedAt = UnityEngine.Time.unscaledTime;
            _ = ListAsync();
        }

        private async Task ListAsync()
        {
            try
            {
                string url = $"{Supabase.Url}/rest/v1/maps" +
                             "?select=map_id,name,author_name,author_steam_id,editable,object_count,downloads,updated_at" +
                             "&order=updated_at.desc&limit=200";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                Supabase.AddAuth(req);
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Error = $"HTTP {(int)resp.StatusCode}";
                    MapEditorPlugin.Logger.LogWarning($"[MAPS] List failed {(int)resp.StatusCode}: {Supabase.Trim(body)}");
                    return;
                }
                Maps = JsonSerializer.Deserialize<List<OnlineMapInfo>>(body, Json) ?? new List<OnlineMapInfo>();
                Error = null;
                EverLoaded = true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                MapEditorPlugin.Logger.LogWarning($"[MAPS] List error: {ex.Message}");
            }
            finally
            {
                Loading = false;
            }
        }

        // ── Download ───────────────────────────────────────────────────────────────

        // Fetch a map's data and hand the parsed MapFile to onLoaded (called on the
        // main thread by EditorController's dispatcher). onError reports failures.
        public void Download(string mapId, Action<MapFile> onLoaded, Action<string> onError)
        {
            if (!Configured || string.IsNullOrEmpty(mapId)) { onError?.Invoke("not configured"); return; }
            Busy = true;
            _ = DownloadAsync(mapId, onLoaded, onError);
        }

        private async Task DownloadAsync(string mapId, Action<MapFile> onLoaded, Action<string> onError)
        {
            try
            {
                string payload = JsonSerializer.Serialize(new Dictionary<string, object> { ["p_map_id"] = mapId });
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/fetch_map");
                Supabase.AddAuth(req);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    onError?.Invoke($"HTTP {(int)resp.StatusCode}");
                    return;
                }
                // RPC returns the stored data as a JSON string (or null).
                string dataB64 = JsonSerializer.Deserialize<string>(body, Json);
                if (string.IsNullOrEmpty(dataB64)) { onError?.Invoke("map not found"); return; }

                string mapJson = Unpack(dataB64);
                var map = MapSerializer.FromJson(mapJson);
                onLoaded?.Invoke(map);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[MAPS] Download error: {ex.Message}");
                onError?.Invoke(ex.Message);
            }
            finally
            {
                Busy = false;
            }
        }

        // ── Upload ───────────────────────────────────────────────────────────────

        // Upload/update a map. ownerToken gates overwrites server-side; onDone(result)
        // gets "inserted" / "updated" / "forbidden" / error message.
        public void Upload(MapFile map, string ownerToken, Action<string> onDone)
        {
            if (!Configured || map == null) { onDone?.Invoke("not configured"); return; }
            Busy = true;
            _ = UploadAsync(map, ownerToken, onDone);
        }

        private async Task UploadAsync(MapFile map, string ownerToken, Action<string> onDone)
        {
            try
            {
                string mapJson = MapSerializer.ToJson(map);
                string dataB64 = Pack(mapJson);

                var payload = new Dictionary<string, object>
                {
                    ["p_map_id"] = map.MapId,
                    ["p_name"] = map.Name ?? "Untitled",
                    ["p_author_steam_id"] = map.AuthorSteamId,
                    ["p_author_name"] = map.AuthorName ?? "player",
                    ["p_editable"] = map.Editable,
                    ["p_object_count"] = map.Objects?.Count ?? 0,
                    ["p_data"] = dataB64,
                    ["p_owner_token"] = ownerToken,
                };
                string json = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/upload_map");
                Supabase.AddAuth(req);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    MapEditorPlugin.Logger.LogWarning($"[MAPS] Upload failed {(int)resp.StatusCode}: {Supabase.Trim(body)}");
                    onDone?.Invoke($"HTTP {(int)resp.StatusCode}");
                    return;
                }
                string result = JsonSerializer.Deserialize<string>(body, Json) ?? "ok";
                MapEditorPlugin.Logger.LogInfo($"[MAPS] Upload '{map.Name}' -> {result}");
                onDone?.Invoke(result);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[MAPS] Upload error: {ex.Message}");
                onDone?.Invoke(ex.Message);
            }
            finally
            {
                Busy = false;
            }
        }

        // ── Delete ───────────────────────────────────────────────────────────────

        public void Delete(string mapId, string ownerToken, Action<string> onDone)
        {
            if (!Configured || string.IsNullOrEmpty(mapId)) { onDone?.Invoke("not configured"); return; }
            Busy = true;
            _ = DeleteAsync(mapId, ownerToken, onDone);
        }

        private async Task DeleteAsync(string mapId, string ownerToken, Action<string> onDone)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["p_map_id"] = mapId,
                    ["p_owner_token"] = ownerToken,
                };
                string json = JsonSerializer.Serialize(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/delete_map");
                Supabase.AddAuth(req);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { onDone?.Invoke($"HTTP {(int)resp.StatusCode}"); return; }
                string result = JsonSerializer.Deserialize<string>(body, Json) ?? "ok";
                onDone?.Invoke(result);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[MAPS] Delete error: {ex.Message}");
                onDone?.Invoke(ex.Message);
            }
            finally
            {
                Busy = false;
            }
        }

        // ── gzip + base64 (map JSON is text; compress to keep rows small) ──────────

        private static string Pack(string text)
        {
            var raw = Encoding.UTF8.GetBytes(text);
            using var outMs = new MemoryStream();
            using (var gz = new GZipStream(outMs, CompressionLevel.Optimal, true))
                gz.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(outMs.ToArray());
        }

        private static string Unpack(string b64)
        {
            var data = Convert.FromBase64String(b64);
            using var inMs = new MemoryStream(data);
            using var gz = new GZipStream(inMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
    }
}
