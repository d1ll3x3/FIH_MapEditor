using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FIHMapEditor
{
    // Moves voice-line clips through the shared backend (README: voice_clips SQL) so other
    // players hear a map's own recordings. Uploads ride on a successful map upload — the
    // server re-checks the owner token and re-hashes every clip, so nobody can attach audio
    // to a map that isn't theirs. Downloads run whenever a map or co-edit op references a
    // clip this machine lacks. Same threading as OnlineMapService: network on background
    // tasks, results marshalled back to the main thread through Dispatch.
    public class VoiceClipService
    {
        // A clip the server doesn't have yet (map not re-published with it) is asked for
        // again at most this often, instead of on every trigger reference.
        private const float RETRY_SECONDS = 60f;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public bool Configured => Supabase.Configured;

        // Main-thread dispatcher (EditorController.RunOnMainThread): the library's indexes
        // are read every frame by the menu and play mode, so they're only mutated there.
        public Action<Action> Dispatch;

        // Main thread only.
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly Dictionary<string, float> _retryAt = new Dictionary<string, float>();

        public bool IsDownloading(string hash) => !string.IsNullOrEmpty(hash) && _inFlight.Contains(hash);

        // ── Download ─────────────────────────────────────────────────────────────

        public void EnsureAvailable(string hash, string name)
        {
            if (!Configured || string.IsNullOrEmpty(hash)) return;
            if (VoiceLineLibrary.Find(hash) != null || _inFlight.Contains(hash)) return;
            if (_retryAt.TryGetValue(hash, out float retryAt) && UnityEngine.Time.unscaledTime < retryAt) return;
            _inFlight.Add(hash);
            _ = DownloadAsync(hash, name);
        }

        private async Task DownloadAsync(string hash, string name)
        {
            string path = null;
            try
            {
                string body = await PostRpcAsync("fetch_voice_clip",
                    new Dictionary<string, object> { ["p_hash"] = hash }).ConfigureAwait(false);
                string b64 = body == null ? null : JsonSerializer.Deserialize<string>(body, Json);
                if (string.IsNullOrEmpty(b64))
                {
                    MapEditorPlugin.Logger.LogWarning(
                        $"[VOICE] Clip '{name}' ({hash}) is not on the server — the map owner hasn't published it yet.");
                }
                else
                {
                    byte[] bytes = Convert.FromBase64String(b64);
                    // The server already verified this, but a clip we play must be exactly
                    // the one the map references.
                    if (bytes.Length > VoiceLineLibrary.MAX_CLIP_BYTES || VoiceLineLibrary.Sha256Hex(bytes) != hash)
                        MapEditorPlugin.Logger.LogWarning($"[VOICE] Downloaded clip '{name}' ({hash}) failed verification; discarded.");
                    else
                        path = VoiceLineLibrary.WriteDownloaded(hash, name, bytes);
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] Download of '{name}' ({hash}) failed: {ex.Message}");
            }

            RunOnMain(() =>
            {
                _inFlight.Remove(hash);
                if (path != null)
                {
                    VoiceLineLibrary.RegisterDownloaded(hash, path);
                    MapEditorPlugin.Logger.LogInfo($"[VOICE] Downloaded clip '{name}' ({hash}).");
                }
                else
                {
                    _retryAt[hash] = UnityEngine.Time.unscaledTime + RETRY_SECONDS;
                }
            });
        }

        // ── Upload ───────────────────────────────────────────────────────────────

        // Attach every clip the map's triggers use to the just-uploaded map, sending bytes
        // only for clips the server doesn't store yet, then unlink clips it no longer uses.
        // onDone receives a player-facing summary on the main thread.
        public void UploadForMap(string mapId, string ownerToken, List<VoiceTriggerData> triggers, Action<string> onDone)
        {
            if (!Configured || string.IsNullOrEmpty(mapId)) return;

            // Resolve files on the main thread: the library isn't safe to read from a worker.
            var clips = new List<(string hash, string name, string path)>();
            var keep = new List<string>();
            var problems = new List<string>();
            foreach (var trigger in triggers ?? new List<VoiceTriggerData>())
            {
                if (string.IsNullOrEmpty(trigger?.AudioHash) || keep.Contains(trigger.AudioHash)) continue;
                var clip = VoiceLineLibrary.Find(trigger.AudioHash);
                if (clip != null && clip.Size > VoiceLineLibrary.MAX_CLIP_BYTES)
                {
                    problems.Add($"{trigger.AudioName}: over 1 MB");
                    continue;
                }
                keep.Add(trigger.AudioHash);
                // No local file is fine if the server already has the clip ("ok" on link).
                clips.Add((trigger.AudioHash, trigger.AudioName, clip?.FilePath));
            }
            _ = UploadAsync(mapId, ownerToken, clips, keep, problems, onDone);
        }

        private async Task UploadAsync(string mapId, string ownerToken, List<(string hash, string name, string path)> clips,
            List<string> keep, List<string> problems, Action<string> onDone)
        {
            int sent = 0;
            try
            {
                foreach (var clip in clips)
                {
                    string result = await UploadClipAsync(mapId, ownerToken, clip.hash, null).ConfigureAwait(false);
                    if (result == "missing")
                    {
                        if (clip.path == null)
                        {
                            problems.Add($"{clip.name}: not on this PC");
                            continue;
                        }
                        string data = Convert.ToBase64String(File.ReadAllBytes(clip.path));
                        result = await UploadClipAsync(mapId, ownerToken, clip.hash, data).ConfigureAwait(false);
                        if (result == "ok") sent++;
                    }
                    if (result != "ok") problems.Add($"{clip.name}: {result}");
                }

                string pruned = await PostRpcAsync("prune_map_voice_clips", new Dictionary<string, object>
                {
                    ["p_map_id"] = mapId,
                    ["p_owner_token"] = ownerToken,
                    ["p_keep"] = keep,
                }).ConfigureAwait(false);
                if (pruned == null) problems.Add("unused clips not unlinked");
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }

            string summary = problems.Count == 0
                ? $"Voice lines published ({keep.Count} clip(s), {sent} uploaded)"
                : $"Voice lines: {problems.Count} problem(s) — {string.Join("; ", problems)}";
            MapEditorPlugin.Logger.Log(problems.Count == 0 ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning,
                $"[VOICE] Upload for map {mapId}: {summary}");
            RunOnMain(() => onDone?.Invoke(summary));
        }

        private async Task<string> UploadClipAsync(string mapId, string ownerToken, string hash, string data)
        {
            string body = await PostRpcAsync("upload_voice_clip", new Dictionary<string, object>
            {
                ["p_map_id"] = mapId,
                ["p_owner_token"] = ownerToken,
                ["p_hash"] = hash,
                ["p_data"] = data,
            }).ConfigureAwait(false);
            return body == null ? "request failed" : JsonSerializer.Deserialize<string>(body, Json) ?? "no result";
        }

        // ── Transport ────────────────────────────────────────────────────────────

        // Response body, or null on an HTTP error (already logged).
        private static async Task<string> PostRpcAsync(string function, Dictionary<string, object> args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url}/rest/v1/rpc/{function}");
            Supabase.AddAuth(req);
            req.Content = new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json");
            using var resp = await Supabase.Http.SendAsync(req).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return body;
            MapEditorPlugin.Logger.LogWarning($"[VOICE] {function} failed {(int)resp.StatusCode}: {Supabase.Trim(body)}");
            return null;
        }

        private void RunOnMain(Action action)
        {
            var dispatch = Dispatch;
            if (dispatch != null) dispatch(action);
            else action();
        }
    }
}
