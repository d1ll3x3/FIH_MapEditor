using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FIHMapEditor
{
    // World-space wireframe box drawn with a LineRenderer. Used for the selection
    // highlight, the goal zone and the spawn marker. No custom shaders: tries a list
    // of always-included shaders and degrades to invisible if none exist.
    //
    // Markers are redrawn every frame but almost never change, and every LineRenderer
    // or Material write is an IL2CPP interop call. So the box remembers what it last
    // handed to Unity and only writes what changed: points go up in one SetPositions
    // call, colors and widths only when they differ, SetActive only on a real toggle.
    public class LineBox
    {
        private GameObject _go;
        private LineRenderer _lr;
        private Material _mat;
        private int _colorProp = -1;   // _BaseColor or _Color id; -1 = shader has neither
        private bool _initFailed;
        private bool _shown;

        private Vector3[] _scratch = new Vector3[32];
        private Vector3[] _uploaded = Array.Empty<Vector3>();
        private int _uploadedCount = -1;
        private Il2CppStructArray<Vector3> _native;
        private float _width = 0.06f;
        private Color _vertexColor;
        private bool _hasVertexColor;
        private Color _matColor;
        private bool _hasMatColor;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly string _name;

        public LineBox(string name)
        {
            _name = name;
        }

        private static readonly string[] ShaderCandidates =
        {
            "Sprites/Default",
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Legacy Shaders/Particles/Alpha Blended Premultiply",
            "UI/Default",
        };

        private bool EnsureCreated()
        {
            // Create lazily because shader lookup before the game scene is initialized can
            // incorrectly report that an otherwise available built-in shader is missing.
            if (_go != null) return true;
            if (_initFailed) return false;
            try
            {
                Shader shader = null;
                foreach (var s in ShaderCandidates)
                {
                    shader = Shader.Find(s);
                    if (shader != null) break;
                }
                if (shader == null)
                {
                    MapEditorPlugin.Logger.LogWarning("[HIGHLIGHT] No usable shader found; highlights disabled.");
                    _initFailed = true;
                    return false;
                }

                // Visual helpers survive scene reloads and are explicitly hidden/reused by
                // their owner, avoiding allocation every selection frame.
                _go = new GameObject(_name);
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _lr = _go.AddComponent<LineRenderer>();
                _lr.useWorldSpace = true;
                _lr.material = new Material(shader);
                _mat = _lr.material;
                _colorProp = _mat == null ? -1
                    : _mat.HasProperty(BaseColorId) ? BaseColorId
                    : _mat.HasProperty(ColorId) ? ColorId
                    : -1;
                _lr.startWidth = 0.06f;
                _lr.endWidth = 0.06f;
                _lr.positionCount = 0;
                _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _lr.receiveShadows = false;
                // A recreated object starts from scratch: nothing of it is cached yet.
                _shown = true;
                _uploadedCount = -1;
                _width = 0.06f;
                _hasVertexColor = false;
                _hasMatColor = false;
                return true;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[HIGHLIGHT] Init failed: {ex.Message}");
                _initFailed = true;
                return false;
            }
        }

        // Single line strip visiting all 12 edges of the box (16 points).
        public void ShowBox(Bounds bounds, Color color)
            => ShowBox(bounds.center, bounds.size, Quaternion.identity, color);

        // Oriented variant: same wireframe with the corners rotated around the center.
        public void ShowBox(Vector3 center, Vector3 size, Quaternion rot, Color color)
        {
            if (!EnsureCreated()) return;
            try
            {
                Vector3 h = size * 0.5f;
                Vector3 b0 = center + rot * new Vector3(-h.x, -h.y, -h.z);
                Vector3 b1 = center + rot * new Vector3(h.x, -h.y, -h.z);
                Vector3 b2 = center + rot * new Vector3(h.x, -h.y, h.z);
                Vector3 b3 = center + rot * new Vector3(-h.x, -h.y, h.z);
                Vector3 t0 = center + rot * new Vector3(-h.x, h.y, -h.z);
                Vector3 t1 = center + rot * new Vector3(h.x, h.y, -h.z);
                Vector3 t2 = center + rot * new Vector3(h.x, h.y, h.z);
                Vector3 t3 = center + rot * new Vector3(-h.x, h.y, h.z);

                var path = Scratch(16);
                path[0] = b0; path[1] = b1; path[2] = b2; path[3] = b3; path[4] = b0;   // bottom loop
                path[5] = t0; path[6] = t1; path[7] = b1; path[8] = t1;                 // up + edge to b1 and back
                path[9] = t2; path[10] = b2; path[11] = t2;
                path[12] = t3; path[13] = b3; path[14] = t3;
                path[15] = t0;                                                          // close top loop

                Upload(path, 16);
                ApplyColor(color, tintMaterial: true);
                Show();
            }
            catch { }
        }

        // Straight segment between two points (cannon → target aim line).
        public void ShowLine(Vector3 a, Vector3 b, Color color)
        {
            if (!EnsureCreated()) return;
            try
            {
                var path = Scratch(2);
                path[0] = a;
                path[1] = b;
                Upload(path, 2);
                ApplyWidth(0.05f);
                ApplyColor(color, tintMaterial: true);
                Show();
            }
            catch { }
        }

        // Arbitrary world-space polyline, used for sampled trajectory previews.
        public void ShowPath(IReadOnlyList<Vector3> points, Color color)
        {
            if (points == null || points.Count < 2 || !EnsureCreated()) { Hide(); return; }
            try
            {
                var path = Scratch(points.Count);
                for (int i = 0; i < points.Count; i++) path[i] = points[i];
                Upload(path, points.Count);
                ApplyWidth(0.07f);
                ApplyColor(color, tintMaterial: true);
                Show();
            }
            catch { }
        }

        // Spinning vertical ring — the coin-style checkpoint marker. Redrawn every
        // frame, so the spin is just a time-based rotation of the circle's plane.
        public void ShowRing(Vector3 center, float radius, Color color)
        {
            if (!EnsureCreated()) return;
            try
            {
                float spin = Time.time * 1.6f;
                Vector3 normal = new Vector3(Mathf.Cos(spin), 0f, Mathf.Sin(spin));
                Vector3 u = Vector3.up;
                Vector3 v = Vector3.Cross(normal, u).normalized;

                const int SEGMENTS = 28;
                var path = Scratch(SEGMENTS + 1);
                for (int i = 0; i <= SEGMENTS; i++)
                {
                    float a = i * Mathf.PI * 2f / SEGMENTS;
                    path[i] = center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius;
                }
                Upload(path, SEGMENTS + 1);

                ApplyWidth(0.09f);
                ApplyColor(color, tintMaterial: true);
                Show();
            }
            catch { }
        }

        // Four vertical edges only — a subtle beacon for the goal in play mode.
        public void ShowBeacon(Bounds bounds, Color color, float height)
        {
            if (!EnsureCreated()) return;
            try
            {
                Vector3 min = bounds.min, max = bounds.max;
                float y0 = min.y, y1 = min.y + height;

                var path = Scratch(11);
                path[0] = new Vector3(min.x, y0, min.z); path[1] = new Vector3(min.x, y1, min.z); path[2] = new Vector3(min.x, y0, min.z);
                path[3] = new Vector3(max.x, y0, min.z); path[4] = new Vector3(max.x, y1, min.z); path[5] = new Vector3(max.x, y0, min.z);
                path[6] = new Vector3(max.x, y0, max.z); path[7] = new Vector3(max.x, y1, max.z); path[8] = new Vector3(max.x, y0, max.z);
                path[9] = new Vector3(min.x, y0, max.z); path[10] = new Vector3(min.x, y1, max.z);

                Upload(path, 11);
                ApplyColor(color, tintMaterial: false);
                Show();
            }
            catch { }
        }

        public bool Visible => _go != null && _shown;

        public void Hide()
        {
            if (_go == null || !_shown) return;
            _go.SetActive(false);
            _shown = false;
        }

        private void Show()
        {
            if (_shown) return;
            _go.SetActive(true);
            _shown = true;
        }

        private Vector3[] Scratch(int count)
        {
            if (_scratch.Length < count) _scratch = new Vector3[count];
            return _scratch;
        }

        private void Upload(Vector3[] points, int count)
        {
            if (count == _uploadedCount)
            {
                bool same = true;
                for (int i = 0; i < count; i++)
                    if (points[i] != _uploaded[i]) { same = false; break; }
                if (same) return;
            }
            if (_uploaded.Length < count) _uploaded = new Vector3[count];
            Array.Copy(points, _uploaded, count);
            _uploadedCount = count;

            if (_native == null || _native.Length != count) _native = new Il2CppStructArray<Vector3>(count);
            for (int i = 0; i < count; i++) _native[i] = points[i];
            _lr.positionCount = count;
            _lr.SetPositions(_native);
        }

        private void ApplyWidth(float width)
        {
            if (width == _width) return;
            _lr.startWidth = width;
            _lr.endWidth = width;
            _width = width;
        }

        private void ApplyColor(Color color, bool tintMaterial)
        {
            if (!_hasVertexColor || color != _vertexColor)
            {
                _lr.startColor = color;
                _lr.endColor = color;
                _vertexColor = color;
                _hasVertexColor = true;
            }
            if (tintMaterial && _colorProp >= 0 && (!_hasMatColor || color != _matColor))
            {
                _mat.SetColor(_colorProp, color);
                _matColor = color;
                _hasMatColor = true;
            }
        }
    }

    // Visual bounds of one object, recomputed only when it moves or every half second
    // (its children can still change on their own). ComputeVisualBounds walks every
    // renderer of the hierarchy, which per frame was the highlight's main cost on big
    // level pieces and multi-selections.
    public class VisualBoundsCache
    {
        private const float REFRESH_SECONDS = 0.5f;

        private GameObject _go;
        private Vector3 _pos;
        private Quaternion _rot;
        private Vector3 _scale;
        private Bounds _bounds;
        private float _refreshAt;

        public Bounds Get(GameObject go)
        {
            var t = go.transform;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;
            Vector3 scale = t.lossyScale;
            if (go != _go || pos != _pos || rot != _rot || scale != _scale || Time.unscaledTime >= _refreshAt)
            {
                _go = go;
                _pos = pos;
                _rot = rot;
                _scale = scale;
                _bounds = ObjectCatalog.ComputeVisualBounds(go);
                _refreshAt = Time.unscaledTime + REFRESH_SECONDS;
            }
            return _bounds;
        }
    }

    // Owns the pulsing selection wireframe; updated every frame in editor mode.
    public class HighlightRenderer
    {
        private readonly LineBox _box = new LineBox("FIH_Line_Selection");
        private readonly VisualBoundsCache _bounds = new VisualBoundsCache();

        public void UpdateHighlight(Selection selection)
        {
            var target = selection?.Target;
            if (target == null)
            {
                _box.Hide();
                return;
            }

            // Visual bounds: a piece of a mesh-combined level has no renderer of its own
            // switched on until it is edited.
            var bounds = _bounds.Get(target);
            if (bounds.size == Vector3.zero)
            {
                _box.Hide();
                return;
            }

            float pulse = 0.55f + 0.45f * Mathf.PingPong(Time.unscaledTime * 2f, 1f);
            var color = selection.IsPlaced
                ? new Color(0.2f, 1f, 1f, pulse)     // cyan: our clone
                : new Color(1f, 0.7f, 0.2f, pulse);  // orange: original scene object (unlock)
            _box.ShowBox(bounds, color);
        }

        public void Hide() => _box.Hide();
    }
}
