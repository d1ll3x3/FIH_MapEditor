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
    // Update renders at most one prefab per work slot and caches the resulting texture.
    // Every render instantiates the object, so cards on screen go first, nothing renders
    // while the list is being scrolled, and the background caching of the rest runs at
    // a slow pace. The image is read back from the GPU a couple of frames after the
    // render, once the GPU is done with it, instead of stalling for it.
    public sealed class ObjectPreviewRenderer
    {
        private readonly ObjectCatalog _catalog;
        // A RenderTexture while its readback is pending, a Texture2D afterwards.
        private readonly Dictionary<string, Texture> _cache = new Dictionary<string, Texture>();
        private readonly Queue<CatalogEntry> _visibleQueue = new Queue<CatalogEntry>();
        private readonly HashSet<string> _queuedVisible = new HashSet<string>();
        private readonly Queue<CatalogEntry> _queue = new Queue<CatalogEntry>();
        private readonly HashSet<string> _queued = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly Dictionary<string, float> _retryAfter = new Dictionary<string, float>();
        private readonly string _diskCacheDirectory;
        private readonly string _bundledPreviewDirectory;
        // File names present in each directory, listed once: IMGUI asks for every card
        // several times per frame, and probing the disk each time added up.
        private HashSet<string> _diskFiles;
        private HashSet<string> _bundledFiles;
        private float _nextWorkAt;
        private float _nextBackgroundAt;
        private int _preparedCatalogVersion = -1;
        private const float WORK_INTERVAL = 0.10f;
        private const float BACKGROUND_INTERVAL = 1.0f;
        private const double SLOW_RENDER_MS = 100.0;

        // When each card was last asked for: IMGUI asks every frame while it is on screen.
        private readonly Dictionary<string, float> _lastRequested = new Dictionary<string, float>();
        // Last time a card came into view, i.e. the list is being scrolled.
        private float _viewChangedAt = -99f;
        private const float ON_SCREEN_SECONDS = 0.25f;
        private const float SCROLL_SETTLE_SECONDS = 0.35f;

        // Dragging the scrollbar brings many cached cards in at once; their file reads
        // and texture uploads are spread over a few frames.
        private int _diskLoadFrame = -1;
        private int _diskLoadsThisFrame;
        private const int MAX_DISK_LOADS_PER_FRAME = 2;

        // The render whose GPU readback is still pending.
        private string _pendingPath;
        private RenderTexture _pendingTarget;
        private int _pendingFrame;
        private const int READBACK_DELAY_FRAMES = 2;

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

        public Texture Request(CatalogEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.SourcePath)) return null;
            string path = entry.SourcePath;
            float now = Time.unscaledTime;
            if (!_lastRequested.TryGetValue(path, out float lastAsked) || now - lastAsked > ON_SCREEN_SECONDS)
                _viewChangedAt = now;
            _lastRequested[path] = now;

            if (_cache.TryGetValue(path, out var texture) && texture != null) return texture;
            if (_failed.Contains(path)) return null;

            // Disk hits are cheap and should appear immediately. Only actual prefab
            // generation belongs in the throttled queue; delaying cached files made a
            // populated bundle look indistinguishable from six fresh renders.
            string fileName = CacheFileName(path);
            if (DiskFiles.Contains(fileName) || BundledFiles.Contains(fileName))
            {
                if (Time.frameCount != _diskLoadFrame)
                {
                    _diskLoadFrame = Time.frameCount;
                    _diskLoadsThisFrame = 0;
                }
                if (_diskLoadsThisFrame >= MAX_DISK_LOADS_PER_FRAME) return null;   // next frame
                _diskLoadsThisFrame++;
                var diskTexture = LoadFromDisk(fileName);
                if (diskTexture != null)
                {
                    _cache[path] = diskTexture;
                    return diskTexture;
                }
            }

            if (EditorConfig.Settings.LowPerformanceMode) return null;
            if (_retryAfter.TryGetValue(entry.SourcePath, out float retryAt)
                && Time.unscaledTime < retryAt) return null;
            if (_queuedVisible.Add(entry.SourcePath)) _visibleQueue.Enqueue(entry);
            return null;
        }

        public void Update(bool catalogVisible, IReadOnlyList<CatalogEntry> entries, int catalogVersion)
        {
            FinishPendingReadback();
            if (!catalogVisible || EditorConfig.Settings.LowPerformanceMode) return;
            PrepareMissing(entries, catalogVersion);
            if (_pendingTarget != null || Time.unscaledTime < _nextWorkAt) return;
            if (Time.unscaledTime - _viewChangedAt < SCROLL_SETTLE_SECONDS) return;

            var entry = NextPending(_visibleQueue, _queuedVisible, onScreenOnly: true);
            if (entry == null && Time.unscaledTime >= _nextBackgroundAt)
            {
                entry = NextPending(_queue, _queued, onScreenOnly: false);
                if (entry != null) _nextBackgroundAt = Time.unscaledTime + BACKGROUND_INTERVAL;
            }
            if (entry == null) return;
            _nextWorkAt = Time.unscaledTime + WORK_INTERVAL;
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
                // A missing thumbnail is a rendering outcome, never a statement about the
                // object: _failed only stops us retrying it. Dropping the entry from the
                // catalog here made objects vanish from the list one tick at a time while
                // previews generated, and the next Scan re-added them — the "objects
                // appear and disappear" churn (which also re-pushed them to the cloud
                // catalog as fresh discoveries every pass).
                //
                // Collider-less sources are rendered too: a preview is visual-only, and
                // decor without colliders is placeable here (PlacedObjectManager backfills
                // a box collider on the clone).
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var target = RenderToTarget(source);
                if (clock.Elapsed.TotalMilliseconds > SLOW_RENDER_MS)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[PERF] Preview of '{entry.DisplayName}' took {clock.Elapsed.TotalMilliseconds:0}ms.");
                if (target != null)
                {
                    // Shown right away; FinishPendingReadback swaps in the saved copy.
                    _cache[entry.SourcePath] = target;
                    _pendingPath = entry.SourcePath;
                    _pendingTarget = target;
                    _pendingFrame = Time.frameCount;
                }
                else
                {
                    _failed.Add(entry.SourcePath);
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

        // Next queued entry that still needs a render (a card may have been served from
        // the other queue or from disk since it was queued). Cards scrolled out of view
        // are dropped; Request queues them again if they come back.
        private CatalogEntry NextPending(Queue<CatalogEntry> queue, HashSet<string> queued, bool onScreenOnly)
        {
            float now = Time.unscaledTime;
            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                queued.Remove(entry.SourcePath);
                if (_cache.ContainsKey(entry.SourcePath) || _failed.Contains(entry.SourcePath)) continue;
                if (onScreenOnly && (!_lastRequested.TryGetValue(entry.SourcePath, out float asked)
                    || now - asked > ON_SCREEN_SECONDS)) continue;
                return entry;
            }
            return null;
        }

        // Reads a finished render back from the GPU and stores it. Done a couple of
        // frames after the render so ReadPixels finds the GPU work already complete.
        private void FinishPendingReadback()
        {
            if (_pendingTarget == null || Time.frameCount < _pendingFrame + READBACK_DELAY_FRAMES) return;
            var target = _pendingTarget;
            string path = _pendingPath;
            _pendingTarget = null;
            _pendingPath = null;
            try
            {
                var texture = ReadBack(target);
                _cache[path] = texture;
                SaveToDisk(path, texture);
            }
            catch (Exception ex)
            {
                _cache.Remove(path);
                _failed.Add(path);
                MapEditorPlugin.Logger.LogWarning($"[PREVIEW] Readback of '{path}' failed: {ex.Message}");
            }
            finally
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
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
                if (DiskFiles.Contains(fileName) || BundledFiles.Contains(fileName)) continue;
                if (_queued.Add(entry.SourcePath)) _queue.Enqueue(entry);
            }

            MapEditorPlugin.Logger.LogInfo($"[PREVIEW] {_queue.Count} missing thumbnail(s) queued for background caching.");
        }

        private HashSet<string> DiskFiles => _diskFiles ??= ListFiles(_diskCacheDirectory);
        private HashSet<string> BundledFiles => _bundledFiles ??= ListFiles(_bundledPreviewDirectory);

        private static HashSet<string> ListFiles(string directory)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(directory))
                    foreach (var file in Directory.EnumerateFiles(directory, "*.rgba"))
                        names.Add(Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PREVIEW] Could not list '{directory}': {ex.Message}");
            }
            return names;
        }

        private Texture2D LoadFromDisk(string fileName)
        {
            // User-generated cache wins, allowing a regenerated image to supersede a
            // bundled thumbnail without modifying the installed mod files.
            if (DiskFiles.Contains(fileName))
            {
                var texture = LoadRawTexture(Path.Combine(_diskCacheDirectory, fileName), true);
                if (texture != null) return texture;
                DiskFiles.Remove(fileName);   // unreadable: LoadRawTexture deleted it
            }
            if (!BundledFiles.Contains(fileName)) return null;
            return LoadRawTexture(Path.Combine(_bundledPreviewDirectory, fileName), false);
        }

        private static Texture2D LoadRawTexture(string path, bool deleteIfInvalid)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                const int size = PREVIEW_SIZE;
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
                string fileName = CacheFileName(sourcePath);
                File.WriteAllBytes(Path.Combine(_diskCacheDirectory, fileName), bytes);
                DiskFiles.Add(fileName);
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

        private const int PREVIEW_SIZE = 128;

        private static RenderTexture RenderToTarget(GameObject source)
        {
            const int size = PREVIEW_SIZE;
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
            camera.targetTexture = null;

            UnityEngine.Object.DestroyImmediate(cameraGo);
            UnityEngine.Object.DestroyImmediate(lightGo);
            UnityEngine.Object.DestroyImmediate(stage);
            return target;
        }

        private static Texture2D ReadBack(RenderTexture target)
        {
            var old = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(PREVIEW_SIZE, PREVIEW_SIZE, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, PREVIEW_SIZE, PREVIEW_SIZE), 0, 0);
            texture.Apply();
            RenderTexture.active = old;
            return texture;
        }
    }
}
