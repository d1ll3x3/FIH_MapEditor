using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace FIHMapEditor
{
    // Voice-line clips on this machine. Two sources:
    //  - the map author's own recordings, dropped into <plugins>/FIHMapEditor/Audio (the
    //    only way to add audio: IMGUI has no file picker under IL2CPP); these are what the
    //    editor offers when choosing a trigger's clip;
    //  - clips downloaded for other people's maps (Audio/Downloaded), used only to play.
    // Maps reference a clip by the SHA-256 of its bytes, not by file name, so renaming a file
    // doesn't break a map and identical recordings are one clip.
    public static class VoiceLineLibrary
    {
        // Mirrors the server-side cap in the voice_clips table (README).
        public const long MAX_CLIP_BYTES = 1024 * 1024;

        public sealed class Clip
        {
            public string Name;
            public string Hash;
            public string FilePath;
            public long Size;
        }

        // Formats FMOD decodes natively — no extra codec in the mod.
        private static readonly string[] Extensions = { ".ogg", ".mp3", ".wav", ".flac" };

        private static readonly List<Clip> _clips = new List<Clip>();
        private static readonly Dictionary<string, Clip> _byHash = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Clip> _downloaded = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
        // Rescans run on every Play entry: only re-hash files whose size or timestamp changed.
        private static readonly Dictionary<string, (long length, DateTime written, string hash)> _hashCache
            = new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static bool _scanned;

        public static string Folder => Path.Combine(BepInEx.Paths.PluginPath, "FIHMapEditor", "Audio");
        public static string DownloadedFolder => Path.Combine(Folder, "Downloaded");

        // The author's own clips (not downloads), sorted by name.
        public static IReadOnlyList<Clip> Clips
        {
            get
            {
                if (!_scanned) Rescan();
                return _clips;
            }
        }

        public static Clip Find(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            if (!_scanned) Rescan();
            if (_byHash.TryGetValue(hash, out var own)) return own;
            return _downloaded.TryGetValue(hash, out var downloaded) ? downloaded : null;
        }

        public static void Rescan()
        {
            _scanned = true;
            _clips.Clear();
            _byHash.Clear();
            _downloaded.Clear();
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var file in Directory.GetFiles(Folder))
                {
                    if (!IsAudio(file)) continue;
                    string hash = HashOf(file);
                    if (hash == null || _byHash.ContainsKey(hash)) continue;
                    var clip = new Clip { Name = Path.GetFileName(file), Hash = hash, FilePath = file,
                        Size = new FileInfo(file).Length };
                    _clips.Add(clip);
                    _byHash[hash] = clip;
                }
                _clips.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                // Downloads are verified before they're written and named after their hash,
                // so the name is trusted here instead of re-hashing every file.
                if (Directory.Exists(DownloadedFolder))
                    foreach (var file in Directory.GetFiles(DownloadedFolder))
                        if (IsAudio(file)) RegisterDownloaded(Path.GetFileNameWithoutExtension(file), file);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] Audio folder scan failed ({Folder}): {ex.Message}");
            }
        }

        // Copies an audio file from anywhere on the PC into the Audio folder, so the author can
        // assign it and publish it with the map. Returns the clip, or null with a player-facing
        // reason. The same recording already in the folder is reused, not copied again; a
        // different recording with a clashing name gets " (2)", " (3)"… instead of overwriting.
        public static Clip Import(string sourcePath, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!IsAudio(sourcePath)) { error = "Only .ogg, .mp3, .wav or .flac files"; return null; }
                if (!info.Exists) { error = $"File not found: {sourcePath}"; return null; }
                if (info.Length > MAX_CLIP_BYTES) { error = $"'{info.Name}' is over 1 MB"; return null; }

                if (!_scanned) Rescan();
                byte[] bytes = File.ReadAllBytes(sourcePath);
                string hash = Sha256Hex(bytes);
                if (_byHash.TryGetValue(hash, out var existing)) return existing;

                Directory.CreateDirectory(Folder);
                string dest = Path.Combine(Folder, info.Name);
                string stem = Path.GetFileNameWithoutExtension(info.Name);
                for (int n = 2; File.Exists(dest); n++)
                    dest = Path.Combine(Folder, $"{stem} ({n}){info.Extension}");
                File.WriteAllBytes(dest, bytes);

                var clip = new Clip { Name = Path.GetFileName(dest), Hash = hash, FilePath = dest, Size = bytes.Length };
                _clips.Add(clip);
                _byHash[hash] = clip;
                _clips.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return clip;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] Import of '{sourcePath}' failed: {ex.Message}");
                error = $"Could not copy the file: {ex.Message}";
                return null;
            }
        }

        // Writes a verified download; returns its path. File IO only, so it's safe to call
        // from a worker thread — indexing it is RegisterDownloaded's job on the main thread.
        public static string WriteDownloaded(string hash, string originalName, byte[] bytes)
        {
            string ext = Path.GetExtension(originalName ?? "").ToLowerInvariant();
            if (Array.IndexOf(Extensions, ext) < 0) ext = ".ogg";
            Directory.CreateDirectory(DownloadedFolder);
            string path = Path.Combine(DownloadedFolder, hash + ext);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public static void RegisterDownloaded(string hash, string path)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 64) return;
            _downloaded[hash] = new Clip { Name = Path.GetFileName(path), Hash = hash, FilePath = path,
                Size = new FileInfo(path).Length };
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        private static bool IsAudio(string file)
            => Array.IndexOf(Extensions, Path.GetExtension(file).ToLowerInvariant()) >= 0;

        private static string HashOf(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (_hashCache.TryGetValue(file, out var cached)
                    && cached.length == info.Length && cached.written == info.LastWriteTimeUtc)
                    return cached.hash;

                using var sha = SHA256.Create();
                using var stream = File.OpenRead(file);
                string hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                _hashCache[file] = (info.Length, info.LastWriteTimeUtc, hash);
                return hash;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[VOICE] Could not read '{file}': {ex.Message}");
                return null;
            }
        }
    }
}
