using System;
using UnityEngine;

namespace FIHMapEditor
{
    // World-space wireframe box drawn with a LineRenderer. Used for the selection
    // highlight, the goal zone and the spawn marker. No custom shaders: tries a list
    // of always-included shaders and degrades to invisible if none exist.
    public class LineBox
    {
        private GameObject _go;
        private LineRenderer _lr;
        private bool _initFailed;

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

                _go = new GameObject(_name);
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _lr = _go.AddComponent<LineRenderer>();
                _lr.useWorldSpace = true;
                _lr.material = new Material(shader);
                _lr.startWidth = 0.06f;
                _lr.endWidth = 0.06f;
                _lr.positionCount = 0;
                _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _lr.receiveShadows = false;
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
        {
            if (!EnsureCreated()) return;
            try
            {
                Vector3 min = bounds.min, max = bounds.max;
                Vector3 b0 = new Vector3(min.x, min.y, min.z);
                Vector3 b1 = new Vector3(max.x, min.y, min.z);
                Vector3 b2 = new Vector3(max.x, min.y, max.z);
                Vector3 b3 = new Vector3(min.x, min.y, max.z);
                Vector3 t0 = new Vector3(min.x, max.y, min.z);
                Vector3 t1 = new Vector3(max.x, max.y, min.z);
                Vector3 t2 = new Vector3(max.x, max.y, max.z);
                Vector3 t3 = new Vector3(min.x, max.y, max.z);

                var path = new Vector3[]
                {
                    b0, b1, b2, b3, b0,      // bottom loop
                    t0, t1, b1, t1,          // up + edge to b1 and back
                    t2, b2, t2,
                    t3, b3, t3,
                    t0,                      // close top loop
                };

                _lr.positionCount = path.Length;
                for (int i = 0; i < path.Length; i++)
                    _lr.SetPosition(i, path[i]);

                _lr.startColor = color;
                _lr.endColor = color;
                if (_lr.material != null)
                {
                    if (_lr.material.HasProperty("_BaseColor")) _lr.material.SetColor("_BaseColor", color);
                    else if (_lr.material.HasProperty("_Color")) _lr.material.color = color;
                }
                _go.SetActive(true);
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
                _lr.positionCount = SEGMENTS + 1;
                for (int i = 0; i <= SEGMENTS; i++)
                {
                    float a = i * Mathf.PI * 2f / SEGMENTS;
                    _lr.SetPosition(i, center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
                }

                _lr.startWidth = 0.09f;
                _lr.endWidth = 0.09f;
                _lr.startColor = color;
                _lr.endColor = color;
                if (_lr.material != null)
                {
                    if (_lr.material.HasProperty("_BaseColor")) _lr.material.SetColor("_BaseColor", color);
                    else if (_lr.material.HasProperty("_Color")) _lr.material.color = color;
                }
                _go.SetActive(true);
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

                var path = new Vector3[]
                {
                    new Vector3(min.x, y0, min.z), new Vector3(min.x, y1, min.z), new Vector3(min.x, y0, min.z),
                    new Vector3(max.x, y0, min.z), new Vector3(max.x, y1, min.z), new Vector3(max.x, y0, min.z),
                    new Vector3(max.x, y0, max.z), new Vector3(max.x, y1, max.z), new Vector3(max.x, y0, max.z),
                    new Vector3(min.x, y0, max.z), new Vector3(min.x, y1, max.z),
                };

                _lr.positionCount = path.Length;
                for (int i = 0; i < path.Length; i++)
                    _lr.SetPosition(i, path[i]);

                _lr.startColor = color;
                _lr.endColor = color;
                _go.SetActive(true);
            }
            catch { }
        }

        public void Hide()
        {
            if (_go != null) _go.SetActive(false);
        }
    }

    // Owns the pulsing selection wireframe; updated every frame in editor mode.
    public class HighlightRenderer
    {
        private readonly LineBox _box = new LineBox("FIH_Line_Selection");

        public void UpdateHighlight(Selection selection)
        {
            var target = selection?.Target;
            if (target == null)
            {
                _box.Hide();
                return;
            }

            var bounds = ObjectCatalog.ComputeBounds(target);
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
