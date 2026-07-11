using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // World-space scoreboard: two numbers (blue = team A, red = team B) drawn as
    // 7-segment digits with LineRenderers — no fonts, so nothing IL2CPP can strip.
    // The board is a marker (ScoreboardData Pos/Rot/Scale) the user places, moves,
    // rotates and scales with the gizmo; digits live on the board's local XY plane.
    public class ScoreboardRenderer
    {
        private GameObject _root;
        private readonly List<LineRenderer> _pool = new List<LineRenderer>();
        private bool _initFailed;

        private static readonly Color BlueColor = new Color(0.35f, 0.55f, 1f, 1f);
        private static readonly Color RedColor = new Color(1f, 0.3f, 0.3f, 1f);
        private static readonly Color DashColor = new Color(0.95f, 0.95f, 0.95f, 0.9f);

        // 7-segment truth table. Segments: 0=top, 1=top-right, 2=bottom-right,
        // 3=bottom, 4=bottom-left, 5=top-left, 6=middle.
        private static readonly bool[][] Segs =
        {
            new[] { true, true, true, true, true, true, false },     // 0
            new[] { false, true, true, false, false, false, false }, // 1
            new[] { true, true, false, true, true, false, true },    // 2
            new[] { true, true, true, true, false, false, true },    // 3
            new[] { false, true, true, false, false, true, true },   // 4
            new[] { true, false, true, true, false, true, true },    // 5
            new[] { true, false, true, true, true, true, true },     // 6
            new[] { true, true, true, false, false, false, false },  // 7
            new[] { true, true, true, true, true, true, true },      // 8
            new[] { true, true, true, true, false, true, true },     // 9
        };

        // Segment endpoints in digit-local space (digit is 1 wide × 2 tall, origin at
        // its center). (a, b) pairs.
        private static readonly (Vector2 a, Vector2 b)[] SegLines =
        {
            (new Vector2(-0.5f, 1f), new Vector2(0.5f, 1f)),     // top
            (new Vector2(0.5f, 1f), new Vector2(0.5f, 0f)),      // top-right
            (new Vector2(0.5f, 0f), new Vector2(0.5f, -1f)),     // bottom-right
            (new Vector2(-0.5f, -1f), new Vector2(0.5f, -1f)),   // bottom
            (new Vector2(-0.5f, 0f), new Vector2(-0.5f, -1f)),   // bottom-left
            (new Vector2(-0.5f, 1f), new Vector2(-0.5f, 0f)),    // top-left
            (new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f)),     // middle
        };

        private bool EnsureCreated()
        {
            if (_root != null) return true;
            if (_initFailed) return false;
            try
            {
                _root = new GameObject("FIH_Scoreboard");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                return true;
            }
            catch
            {
                _initFailed = true;
                return false;
            }
        }

        private LineRenderer GetLine(int index)
        {
            while (_pool.Count <= index)
            {
                var go = new GameObject($"FIH_ScoreSeg{_pool.Count}");
                go.transform.SetParent(_root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                Shader shader = Shader.Find("Hidden/Internal-Colored")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Universal Render Pipeline/Unlit");
                var mat = shader != null ? new Material(shader) : null;
                if (mat != null)
                {
                    try
                    {
                        if (mat.HasProperty("_ZTest"))
                            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    }
                    catch { }
                    lr.material = mat;
                }
                lr.positionCount = 0;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                _pool.Add(lr);
            }
            return _pool[index];
        }

        // Draw the board for the given scores. Call every frame while visible.
        public void Draw(ScoreboardData sb, int scoreA, int scoreB)
        {
            if (sb?.Pos == null || !EnsureCreated()) return;
            try
            {
                _root.SetActive(true);
                Vector3 center = VecUtil.ToVector3(sb.Pos);
                Quaternion rot = VecUtil.ToRotation(sb.Rot);
                float scale = Mathf.Clamp(sb.Scale <= 0 ? 1f : sb.Scale, 0.2f, 40f);
                float width = scale * 0.09f;

                int used = 0;

                // Layout: [blue digits]  -  [red digits], centred on the board origin.
                string a = Mathf.Clamp(scoreA, 0, 99).ToString();
                string b = Mathf.Clamp(scoreB, 0, 99).ToString();
                float digitAdvance = 1.5f;   // digit width 1 + spacing (digit-local units)
                float dashHalf = 0.45f;
                float gap = 0.55f;           // between numbers and the dash

                float aWidth = a.Length * digitAdvance - 0.5f;
                float bWidth = b.Length * digitAdvance - 0.5f;

                // Blue number ends at (-gap - dashHalf); red starts at (gap + dashHalf).
                float aRight = -(dashHalf + gap);
                float bLeft = dashHalf + gap;

                for (int i = 0; i < a.Length; i++)
                {
                    float cx = aRight - aWidth + (i * digitAdvance) + 0.5f;
                    used = DrawDigit(a[i] - '0', new Vector2(cx, 0), center, rot, scale, width, BlueColor, used);
                }
                for (int i = 0; i < b.Length; i++)
                {
                    float cx = bLeft + 0.5f + i * digitAdvance;
                    used = DrawDigit(b[i] - '0', new Vector2(cx, 0), center, rot, scale, width, RedColor, used);
                }

                // Dash
                var dash = GetLine(used++);
                SetLine(dash, ToWorld(new Vector2(-dashHalf, 0), center, rot, scale),
                        ToWorld(new Vector2(dashHalf, 0), center, rot, scale), width, DashColor);

                for (int i = used; i < _pool.Count; i++)
                    if (_pool[i] != null) _pool[i].positionCount = 0;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[SCORE] Draw error: {ex.Message}");
            }
        }

        private int DrawDigit(int digit, Vector2 digitCenter, Vector3 boardCenter,
                              Quaternion rot, float scale, float width, Color color, int used)
        {
            if (digit < 0 || digit > 9) return used;
            var on = Segs[digit];
            for (int s = 0; s < 7; s++)
            {
                if (!on[s]) continue;
                var (la, lb) = SegLines[s];
                var lr = GetLine(used++);
                SetLine(lr,
                    ToWorld(digitCenter + la, boardCenter, rot, scale),
                    ToWorld(digitCenter + lb, boardCenter, rot, scale),
                    width, color);
            }
            return used;
        }

        // Digit-local (x right, y up on the board plane) → world.
        private static Vector3 ToWorld(Vector2 local, Vector3 center, Quaternion rot, float scale)
            => center + rot * (new Vector3(local.x, local.y, 0f) * (scale * 0.5f));

        private static void SetLine(LineRenderer lr, Vector3 a, Vector3 b, float width, Color color)
        {
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startWidth = width;
            lr.endWidth = width;
            lr.startColor = color;
            lr.endColor = color;
            if (lr.material != null)
            {
                if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", color);
                else if (lr.material.HasProperty("_Color")) lr.material.color = color;
            }
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }
    }
}
