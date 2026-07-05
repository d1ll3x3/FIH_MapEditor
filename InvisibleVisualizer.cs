using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // "X-ray" view: wireframe boxes around colliders that have no visible mesh
    // (trigger zones, invisible walls, slide colliders) so they can be seen — and,
    // with "Pick invisible" ON, clicked — while editing.
    public class InvisibleVisualizer
    {
        public bool Enabled { get; private set; }
        public int Count => _entries.Count;

        private class Entry
        {
            public GameObject Root;
            public Bounds Bounds;
            public bool Trigger;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<LineBox> _boxes = new List<LineBox>();
        private float _nextRefresh;

        // Big levels can have hundreds of invisible colliders: only draw the ones near
        // the camera, with a hard cap so the frame never drowns in LineRenderers.
        private const float SHOW_RANGE = 90f;
        private const int MAX_BOXES = 120;
        private const float REFRESH_INTERVAL = 8f;

        private static readonly Color TriggerColor = new Color(1f, 0.35f, 1f, 0.85f);  // magenta: triggers
        private static readonly Color SolidColor = new Color(1f, 1f, 1f, 0.75f);       // white: invisible walls

        public void SetEnabled(bool on)
        {
            Enabled = on;
            if (on) Refresh();
            else HideAll();
        }

        public void OnSceneChanged()
        {
            _entries.Clear();
            HideAll();
        }

        // Scan the scene for collider-bearing objects with no enabled renderer anywhere
        // in their logical root. Re-run periodically: hides/unhides change the set.
        public void Refresh()
        {
            _entries.Clear();
            _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;
            try
            {
                var byRoot = new Dictionary<int, Entry>();
                foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (col == null || !col.enabled) continue;
                    var t = col.transform;
                    if (t == null || t.name.StartsWith("FIH_")) continue;

                    var root = SelectionSystem.ClimbToLogicalRoot(t);
                    if (root == null || root.name.StartsWith("FIH_")) continue;
                    if (root.GetComponentInChildren<Camera>(true) != null) continue; // player rig etc.

                    int id = root.GetInstanceID();
                    if (byRoot.TryGetValue(id, out var entry))
                    {
                        var b = entry.Bounds;
                        b.Encapsulate(col.bounds);
                        entry.Bounds = b;
                        entry.Trigger |= col.isTrigger;
                        continue;
                    }

                    // Objects with a visible mesh don't need a helper box.
                    if (ObjectCatalog.ComputeBounds(root.gameObject).size != Vector3.zero) continue;

                    entry = new Entry { Root = root.gameObject, Bounds = col.bounds, Trigger = col.isTrigger };
                    byRoot[id] = entry;
                    _entries.Add(entry);
                }
                MapEditorPlugin.Logger.LogInfo($"[XRAY] {_entries.Count} invisible collider object(s) in the scene.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[XRAY] Refresh error: {ex.Message}");
            }
        }

        // Call every frame in editor mode.
        public void Update(Camera cam)
        {
            if (!Enabled || cam == null) return;
            if (Time.unscaledTime >= _nextRefresh) Refresh();

            Vector3 camPos = cam.transform.position;
            int used = 0;
            foreach (var e in _entries)
            {
                if (used >= MAX_BOXES) break;
                if (e.Root == null || !e.Root.activeInHierarchy) continue;
                if (Vector3.Distance(camPos, e.Bounds.center) > SHOW_RANGE + e.Bounds.extents.magnitude)
                    continue;

                while (_boxes.Count <= used)
                    _boxes.Add(new LineBox($"FIH_Line_Xray{_boxes.Count}"));
                _boxes[used].ShowBox(e.Bounds, e.Trigger ? TriggerColor : SolidColor);
                used++;
            }
            for (int i = used; i < _boxes.Count; i++)
                _boxes[i].Hide();
        }

        public void HideAll()
        {
            foreach (var b in _boxes) b.Hide();
        }
    }
}
