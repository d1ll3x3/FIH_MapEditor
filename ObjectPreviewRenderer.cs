using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine;

namespace FIHMapEditor
{
    // Runtime replacement for UnityEditor.AssetPreview. Catalog rows enqueue thumbnails;
    // Update renders at most one prefab per frame and caches the resulting Texture2D.
    public sealed class ObjectPreviewRenderer
    {
        private readonly ObjectCatalog _catalog;
        private readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();
        private readonly Queue<CatalogEntry> _queue = new Queue<CatalogEntry>();
        private readonly HashSet<string> _queued = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly Dictionary<string, float> _retryAfter = new Dictionary<string, float>();
        private readonly string _diskCacheDirectory;
        private readonly string _bundledPreviewDirectory;
        private float _nextWorkAt;
        private int _preparedCatalogVersion = -1;
        private const float WORK_INTERVAL = 0.10f;

        public ObjectPreviewRenderer(ObjectCatalog catalog)
        {
            _catalog = catalog;
            _diskCacheDirectory = Path.Combine(Paths.CachePath, "FIHMapEditor", "previews-v1");
            string assemblyDirectory = Path.GetDirectoryName(typeof(ObjectPreviewRenderer).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDirectory))
                assemblyDirectory = Path.Combine(Paths.PluginPath, "MapEditor");
            _bundledPreviewDirectory = Path.Combine(assemblyDirectory, "previews-v1");
            MapEditorPlugin.Logger.LogInfo(
                $"[PREVIEW] Generated cache='{_diskCacheDirectory}', bundled='{_bundledPreviewDirectory}'.");
        }

        public Texture2D Request(CatalogEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.SourcePath)) return null;
            if (_cache.TryGetValue(entry.SourcePath, out var texture) && texture != null) return texture;
            if (_failed.Contains(entry.SourcePath)) return null;

            // Disk hits are cheap and should appear immediately. Only actual prefab
            // generation belongs in the throttled queue; delaying cached files made a
            // populated bundle look indistinguishable from six fresh renders.
            var diskTexture = LoadFromDisk(entry.SourcePath);
            if (diskTexture != null)
            {
                _cache[entry.SourcePath] = diskTexture;
                return diskTexture;
            }

            if (_retryAfter.TryGetValue(entry.SourcePath, out float retryAt)
                && Time.unscaledTime < retryAt) return null;
            if (_queued.Add(entry.SourcePath)) _queue.Enqueue(entry);
            return null;
        }

        public void Update(bool catalogVisible, IReadOnlyList<CatalogEntry> entries, int catalogVersion)
        {
            if (!catalogVisible) return;
            PrepareMissing(entries, catalogVersion);
            if (_queue.Count == 0 || Time.unscaledTime < _nextWorkAt) return;
            _nextWorkAt = Time.unscaledTime + WORK_INTERVAL;
            var entry = _queue.Dequeue();
            _queued.Remove(entry.SourcePath);
            AsyncOperationHandle<GameObject> previewHandle = default;
            bool temporaryHandle = false;
            try
            {
                var source = _catalog.GetPreviewSource(entry, out previewHandle, out temporaryHandle);
                if (source == null)
                {
                    // Addressables discovery may still be settling. Back off instead of
                    // retrying a synchronous lookup every frame.
                    _retryAfter[entry.SourcePath] = Time.unscaledTime + 2f;
                    return;
                }
                if (!ObjectCatalog.HasSolidCollider(source))
                {
                    _failed.Add(entry.SourcePath);
                    _catalog.OmitUnplaceable(entry);
                    return;
                }
                var texture = Render(source);
                if (texture != null)
                {
                    _cache[entry.SourcePath] = texture;
                    SaveToDisk(entry.SourcePath, texture);
                }
                else
                {
                    _failed.Add(entry.SourcePath);
                    _catalog.OmitUnplaceable(entry);
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PREVIEW] '{entry.DisplayName}' failed: {ex.Message}");
            }
            finally
            {
                _catalog.ReleasePreviewSource(previewHandle, temporaryHandle);
            }
        }

        private void PrepareMissing(IReadOnlyList<CatalogEntry> entries, int catalogVersion)
        {
            if (entries == null || _preparedCatalogVersion == catalogVersion) return;
            _preparedCatalogVersion = catalogVersion;

            // Entries are already display-name sorted, so the initially visible page is
            // filled first. Continue through the rest while Catalog stays open, building
            // a complete reusable/distributable cache rather than only caching pages the
            // user happened to scroll through.
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.SourcePath)
                    || _cache.ContainsKey(entry.SourcePath) || _failed.Contains(entry.SourcePath)) continue;
                string fileName = CacheFileName(entry.SourcePath);
                if (File.Exists(Path.Combine(_diskCacheDirectory, fileName))
                    || File.Exists(Path.Combine(_bundledPreviewDirectory, fileName))) continue;
                if (_queued.Add(entry.SourcePath)) _queue.Enqueue(entry);
            }

            MapEditorPlugin.Logger.LogInfo($"[PREVIEW] {_queue.Count} missing thumbnail(s) queued for background caching.");
        }

        private Texture2D LoadFromDisk(string sourcePath)
        {
            string fileName = CacheFileName(sourcePath);
            // User-generated cache wins, allowing a regenerated image to supersede a
            // bundled thumbnail without modifying the installed mod files.
            var texture = LoadRawTexture(Path.Combine(_diskCacheDirectory, fileName), true);
            if (texture != null) return texture;
            return LoadRawTexture(Path.Combine(_bundledPreviewDirectory, fileName), false);
        }

        private static Texture2D LoadRawTexture(string path, bool deleteIfInvalid)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                const int size = 128;
                if (bytes.Length != size * size * 4) throw new InvalidDataException("wrong preview size");
                var loaded = new Texture2D(size, size, TextureFormat.RGBA32, false);
                loaded.LoadRawTextureData(new Il2CppStructArray<byte>(bytes));
                loaded.Apply(false, false);
                return loaded;
            }
            catch
            {
                if (deleteIfInvalid)
                    try { File.Delete(path); } catch { }
            }
            return null;
        }

        private void SaveToDisk(string sourcePath, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(_diskCacheDirectory);
                // Unity 6's IL2CPP wrapper for ImageConversion.EncodeToPNG can corrupt
                // native memory in this build. Store the tiny 128x128 RGBA buffer as-is;
                // it is deterministic, fast, and avoids that unsafe injected call.
                var raw = texture.GetRawTextureData();
                var bytes = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i++) bytes[i] = raw[i];
                File.WriteAllBytes(Path.Combine(_diskCacheDirectory, CacheFileName(sourcePath)), bytes);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PREVIEW] Cache write failed: {ex.Message}");
            }
        }

        private static string CacheFileName(string sourcePath)
        {
            // Stable FNV-1a name avoids invalid filename characters in Addressable keys.
            ulong hash = 14695981039346656037UL;
            foreach (char c in sourcePath)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("X16") + ".rgba";
        }

        private static Texture2D Render(GameObject source)
        {
            const int size = 128;
            var stage = new GameObject("FIH_PreviewStage");
            stage.SetActive(false);
            var clone = UnityEngine.Object.Instantiate(source, stage.transform);
            clone.name = "FIH_PreviewObject";

            // Addressable/pool templates often ship with their visual branch disabled.
            // A preview is intentionally visual-only, so reveal all descendants and
            // renderers just as placement does when a clone would otherwise be invisible.
            foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                if (child != null) child.gameObject.SetActive(true);
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = true;

            // Preview copies are visual only. Removing behavior before activation prevents
            // network/gameplay scripts from running in the editor scene.
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null) UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (var collider in clone.GetComponentsInChildren<Collider>(true))
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in clone.GetComponentsInChildren<Rigidbody>(true))
                if (body != null) UnityEngine.Object.DestroyImmediate(body);

            stage.transform.position = new Vector3(100000f, 100000f, 100000f);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.Euler(18f, -32f, 0f);
            stage.SetActive(true);

            Bounds bounds = default;
            bool hasBounds = false;
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (renderer.bounds.size.sqrMagnitude < 0.000001f) continue;
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds)
            {
                foreach (var filter in clone.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter?.sharedMesh == null) continue;
                    var local = filter.sharedMesh.bounds;
                    for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = filter.transform.TransformPoint(local.center
                            + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                        if (!hasBounds) { bounds = new Bounds(corner, Vector3.zero); hasBounds = true; }
                        else bounds.Encapsulate(corner);
                    }
                }
            }
            if (!hasBounds)
            {
                UnityEngine.Object.DestroyImmediate(stage);
                return null;
            }

            var cameraGo = new GameObject("FIH_PreviewCamera");
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.055f, 0.07f, 0f);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = Mathf.Max(100f, bounds.size.magnitude * 8f);
            camera.orthographicSize = Mathf.Max(0.25f, Mathf.Max(bounds.extents.y,
                Mathf.Max(bounds.extents.x, bounds.extents.z)) * 1.35f);
            camera.transform.position = bounds.center + new Vector3(1f, 0.7f, -1f).normalized
                * Mathf.Max(5f, bounds.size.magnitude * 3f);
            camera.transform.LookAt(bounds.center);

            var lightGo = new GameObject("FIH_PreviewLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.transform.rotation = Quaternion.Euler(35f, -35f, 0f);

            var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            target.Create();
            camera.targetTexture = target;
            camera.Render();
            var old = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            texture.Apply();
            RenderTexture.active = old;
            camera.targetTexture = null;
            target.Release();

            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(cameraGo);
            UnityEngine.Object.DestroyImmediate(lightGo);
            UnityEngine.Object.DestroyImmediate(stage);
            return texture;
        }
    }
}
