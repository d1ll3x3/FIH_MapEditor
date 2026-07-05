using System;
using UnityEngine;

namespace FIHMapEditor
{
    public enum GizmoMode { Move, Rotate, Scale }

    // Blender/Unity-style transform gizmo. Drawn with LineRenderers and driven by
    // manual ray math (IMGUI interactive controls don't fire in this build).
    //   Move   — drag an arrow to translate along that world axis
    //   Rotate — drag a ring to spin around that local axis
    //   Scale  — drag an axis to stretch that local axis (per-axis scaling)
    public class GizmoController
    {
        public GizmoMode Mode = GizmoMode.Move;
        public bool IsDragging => _dragAxis >= 0;

        private GameObject _root;
        private readonly LineRenderer[] _lines = new LineRenderer[3];
        private bool _initFailed;

        private static readonly Color[] AxisColors =
        {
            new Color(1f, 0.25f, 0.3f, 0.95f),   // X
            new Color(0.35f, 1f, 0.35f, 0.95f),  // Y
            new Color(0.3f, 0.55f, 1f, 0.95f),   // Z
        };
        private static readonly Color ActiveColor = new Color(1f, 0.95f, 0.3f, 1f);

        private const int CIRCLE_SEGMENTS = 40;
        private const float GRAB_TOLERANCE = 0.18f;   // fraction of gizmo size

        // Drag state
        private int _dragAxis = -1;
        private Transform _target;
        private Vector3 _center;         // gizmo center at drag start
        private Vector3 _axisDir;        // world-space direction of the grabbed axis
        private Vector3 _startPos;
        private Quaternion _startRot;
        private Vector3 _startScale;
        private float _grabParam;        // move/scale: t along the axis at grab time
        private Vector3 _grabVector;     // rotate: center→grab direction on the ring plane

        // ─────────────────────────────────────────────────────────── rendering ──

        private bool EnsureCreated()
        {
            if (_root != null) return true;
            if (_initFailed) return false;
            try
            {
                Shader shader = null;
                foreach (var s in new[] { "Sprites/Default", "Universal Render Pipeline/Unlit", "Unlit/Color", "UI/Default" })
                {
                    shader = Shader.Find(s);
                    if (shader != null) break;
                }
                if (shader == null) { _initFailed = true; return false; }

                _root = new GameObject("FIH_Gizmo");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                for (int i = 0; i < 3; i++)
                {
                    var go = new GameObject($"FIH_Gizmo_Axis{i}");
                    go.transform.SetParent(_root.transform, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.material = new Material(shader);
                    lr.positionCount = 0;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    _lines[i] = lr;
                }
                return true;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[GIZMO] Init failed: {ex.Message}");
                _initFailed = true;
                return false;
            }
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        // Call every frame in editor mode. Draws the gizmo on the given target
        // (a placed clone, an original level object, or a marker proxy).
        public void UpdateVisual(Transform t, Camera cam)
        {
            if (t == null || cam == null)
            {
                if (!IsDragging) Hide();
                return;
            }
            if (!EnsureCreated()) return;

            Vector3 center = t.position;
            float size = GizmoSize(cam, center);

            _root.SetActive(true);
            for (int i = 0; i < 3; i++)
            {
                bool active = _dragAxis == i;
                var color = active ? ActiveColor : AxisColors[i];
                Vector3 dir = AxisDirection(i, t);

                if (Mode == GizmoMode.Rotate)
                    DrawCircle(_lines[i], center, dir, size, color, size * (active ? 0.035f : 0.02f));
                else
                    DrawAxisLine(_lines[i], center, dir, size, color, size * (active ? 0.05f : 0.03f),
                                 Mode == GizmoMode.Scale);
            }
        }

        // Move uses world axes (like Unity's global handle); rotate/scale must use the
        // object's local axes because localScale is local by definition.
        private Vector3 AxisDirection(int axis, Transform t)
        {
            if (Mode == GizmoMode.Move)
                return axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            return axis == 0 ? t.right : axis == 1 ? t.up : t.forward;
        }

        private static float GizmoSize(Camera cam, Vector3 center)
        {
            float d = Vector3.Distance(cam.transform.position, center);
            return Mathf.Clamp(d * 0.16f, 0.8f, 20f);
        }

        private static void DrawAxisLine(LineRenderer lr, Vector3 center, Vector3 dir, float len,
                                         Color color, float width, bool boxTip)
        {
            Vector3 tip = center + dir * len;
            // A short "flag" at the tip makes the handle end obvious (arrow/box stand-in).
            Vector3 side = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            float tipLen = len * 0.12f;

            Vector3[] path;
            if (boxTip)
            {
                Vector3 s = side * tipLen * 0.5f;
                path = new[] { center, tip, tip + s, tip - s, tip };
            }
            else
            {
                path = new[] { center, tip, tip - dir * tipLen + side * tipLen * 0.6f,
                               tip, tip - dir * tipLen - side * tipLen * 0.6f };
            }

            lr.positionCount = path.Length;
            for (int i = 0; i < path.Length; i++) lr.SetPosition(i, path[i]);
            lr.startWidth = width;
            lr.endWidth = width;
            SetLineColor(lr, color);
        }

        private static void DrawCircle(LineRenderer lr, Vector3 center, Vector3 axis, float radius,
                                       Color color, float width)
        {
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 v = Vector3.Cross(axis, u).normalized;

            lr.positionCount = CIRCLE_SEGMENTS + 1;
            for (int i = 0; i <= CIRCLE_SEGMENTS; i++)
            {
                float a = i * Mathf.PI * 2f / CIRCLE_SEGMENTS;
                lr.SetPosition(i, center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
            }
            lr.startWidth = width;
            lr.endWidth = width;
            SetLineColor(lr, color);
        }

        private static void SetLineColor(LineRenderer lr, Color color)
        {
            lr.startColor = color;
            lr.endColor = color;
            if (lr.material != null)
            {
                if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", color);
                else if (lr.material.HasProperty("_Color")) lr.material.color = color;
            }
        }

        // ──────────────────────────────────────────────────────────── dragging ──

        // Mouse-down test. Returns true when a handle was grabbed (the click must then
        // not fall through to selection picking).
        public bool TryBeginDrag(Ray ray, Transform target, Camera cam)
        {
            if (target == null || cam == null) return false;

            Vector3 center = target.position;
            float size = GizmoSize(cam, center);

            int bestAxis = -1;
            float bestScore = float.MaxValue;
            float bestParam = 0f;
            Vector3 bestGrabVec = Vector3.zero;

            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = AxisDirection(i, target);

                if (Mode == GizmoMode.Rotate)
                {
                    if (!RayPlane(ray, center, dir, out Vector3 p)) continue;
                    float radial = Vector3.Distance(p, center);
                    float score = Mathf.Abs(radial - size);
                    if (score < size * GRAB_TOLERANCE && score < bestScore)
                    {
                        bestScore = score;
                        bestAxis = i;
                        bestGrabVec = (p - center).normalized;
                    }
                }
                else
                {
                    ClosestRayLine(ray, center, dir, out float tRay, out float tLine);
                    if (tRay < 0f) continue;
                    if (tLine < size * 0.15f || tLine > size * 1.25f) continue;
                    float dist = Vector3.Distance(ray.GetPoint(tRay), center + dir * tLine);
                    if (dist < size * GRAB_TOLERANCE && dist < bestScore)
                    {
                        bestScore = dist;
                        bestAxis = i;
                        bestParam = tLine;
                    }
                }
            }

            if (bestAxis < 0) return false;

            _dragAxis = bestAxis;
            _target = target;
            _center = center;
            _axisDir = AxisDirection(bestAxis, target);
            _startPos = target.position;
            _startRot = target.rotation;
            _startScale = target.localScale;
            _grabParam = bestParam;
            _grabVector = bestGrabVec;
            return true;
        }

        // Mouse held: apply the transform for the current mouse ray.
        public void UpdateDrag(Ray ray)
        {
            if (_dragAxis < 0) return;
            if (_target == null) { CancelDrag(); return; }

            try
            {
                switch (Mode)
                {
                    case GizmoMode.Move:
                    {
                        ClosestRayLine(ray, _center, _axisDir, out _, out float tLine);
                        _target.position = _startPos + _axisDir * (tLine - _grabParam);
                        break;
                    }
                    case GizmoMode.Rotate:
                    {
                        if (!RayPlane(ray, _center, _axisDir, out Vector3 p)) return;
                        Vector3 v = (p - _center).normalized;
                        if (v == Vector3.zero) return;
                        float angle = Vector3.SignedAngle(_grabVector, v, _axisDir);
                        _target.rotation = Quaternion.AngleAxis(angle, _axisDir) * _startRot;
                        break;
                    }
                    case GizmoMode.Scale:
                    {
                        ClosestRayLine(ray, _center, _axisDir, out _, out float tLine);
                        if (Mathf.Abs(_grabParam) < 0.001f) return;
                        float factor = Mathf.Max(0.01f, tLine / _grabParam);
                        Vector3 s = _startScale;
                        if (_dragAxis == 0) s.x = Mathf.Max(0.05f, s.x * factor);
                        else if (_dragAxis == 1) s.y = Mathf.Max(0.05f, s.y * factor);
                        else s.z = Mathf.Max(0.05f, s.z * factor);
                        _target.localScale = s;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[GIZMO] Drag error: {ex.Message}");
                CancelDrag();
            }
        }

        public void EndDrag() => _dragAxis = -1;

        public void CancelDrag() => _dragAxis = -1;

        // ──────────────────────────────────────────────────────────── ray math ──

        // Closest points between a ray and an infinite line (both directions normalized).
        private static void ClosestRayLine(Ray ray, Vector3 lineOrigin, Vector3 lineDir,
                                           out float tRay, out float tLine)
        {
            Vector3 r = ray.origin - lineOrigin;
            float b = Vector3.Dot(ray.direction, lineDir);
            float d = Vector3.Dot(ray.direction, r);
            float e = Vector3.Dot(lineDir, r);
            float denom = 1f - b * b;
            if (Mathf.Abs(denom) < 1e-6f) { tRay = 0f; tLine = e; return; }
            tRay = (b * e - d) / denom;
            tLine = (e - b * d) / denom;
        }

        private static bool RayPlane(Ray ray, Vector3 planePoint, Vector3 normal, out Vector3 hit)
        {
            hit = Vector3.zero;
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-5f) return false;
            float t = Vector3.Dot(planePoint - ray.origin, normal) / denom;
            if (t < 0f) return false;
            hit = ray.GetPoint(t);
            return true;
        }
    }
}
