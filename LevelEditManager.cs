using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Runtime record of one edited ORIGINAL level object: what it was before we touched
    // it (for revert / restore) and what the edit is now.
    public class LevelEditRecord
    {
        public GameObject Target;
        public string Path;
        public string Name;

        public Vector3 OrigPos;
        public Quaternion OrigRot;
        public Vector3 OrigScale;

        public bool TransformEdited;
        public bool Hidden;

        // Exact components disabled by Hide, so unhide is lossless.
        public List<Renderer> HiddenRenderers;
        public List<Collider> HiddenColliders;

        public LevelEditData ToData()
        {
            var data = new LevelEditData { Path = Path, Name = Name, Hidden = Hidden };
            if (TransformEdited && Target != null)
            {
                var t = Target.transform;
                data.Pos = VecUtil.ToArray(t.position);
                data.Rot = VecUtil.ToArray(t.eulerAngles);
                data.Scale = VecUtil.ToArray(t.localScale);
            }
            return data;
        }
    }

    // Edits to the game's own level geometry: move/rotate/scale overrides and
    // "deletions" (renderers+colliders disabled — never destroyed, so everything is
    // revertible and nothing gameplay-critical can be lost). Persisted in the map file
    // and re-applied on load / scene reload.
    public class LevelEditManager
    {
        private readonly List<LevelEditRecord> _records = new List<LevelEditRecord>();
        private readonly Dictionary<int, LevelEditRecord> _byId = new Dictionary<int, LevelEditRecord>();

        public IReadOnlyList<LevelEditRecord> Records => _records;
        public int Count => _records.Count;

        // Get-or-create the record for an original object, capturing its pristine state
        // the first time. Call BEFORE mutating the transform.
        public LevelEditRecord CaptureOriginal(GameObject go)
        {
            if (go == null) return null;
            int id = go.GetInstanceID();
            if (_byId.TryGetValue(id, out var existing)) return existing;

            var t = go.transform;
            var record = new LevelEditRecord
            {
                Target = go,
                Path = ObjectCatalog.BuildPath(t),
                Name = go.name,
                OrigPos = t.position,
                OrigRot = t.rotation,
                OrigScale = t.localScale,
            };
            _records.Add(record);
            _byId[id] = record;
            return record;
        }

        public LevelEditRecord Find(GameObject go)
        {
            if (go == null) return null;
            return _byId.TryGetValue(go.GetInstanceID(), out var r) ? r : null;
        }

        // Call AFTER mutating the transform: flags the record (or clears the flag when
        // the object is back at its pristine state).
        public void RecordTransform(GameObject go)
        {
            var record = CaptureOriginal(go);
            if (record?.Target == null) return;
            var t = record.Target.transform;
            record.TransformEdited =
                Vector3.Distance(t.position, record.OrigPos) > 0.001f ||
                Quaternion.Angle(t.rotation, record.OrigRot) > 0.01f ||
                Vector3.Distance(t.localScale, record.OrigScale) > 0.001f;
            PruneIfPristine(record);
        }

        public void Hide(GameObject go)
        {
            var record = CaptureOriginal(go);
            if (record == null || record.Hidden || record.Target == null) return;
            try
            {
                record.HiddenRenderers = new List<Renderer>();
                record.HiddenColliders = new List<Collider>();
                foreach (var r in record.Target.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    record.HiddenRenderers.Add(r);
                }
                foreach (var c in record.Target.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled) continue;
                    c.enabled = false;
                    record.HiddenColliders.Add(c);
                }
                record.Hidden = true;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[LEVELEDIT] Hide error: {ex.Message}");
            }
        }

        public void Unhide(LevelEditRecord record)
        {
            if (record == null || !record.Hidden) return;
            try
            {
                if (record.HiddenRenderers != null)
                    foreach (var r in record.HiddenRenderers)
                        if (r != null) r.enabled = true;
                if (record.HiddenColliders != null)
                    foreach (var c in record.HiddenColliders)
                        if (c != null) c.enabled = true;
                record.HiddenRenderers = null;
                record.HiddenColliders = null;
                record.Hidden = false;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[LEVELEDIT] Unhide error: {ex.Message}");
            }
        }

        // Restore the pristine transform (keeps a hide, if any).
        public void RevertTransform(LevelEditRecord record)
        {
            if (record?.Target == null) return;
            var t = record.Target.transform;
            t.position = record.OrigPos;
            t.rotation = record.OrigRot;
            t.localScale = record.OrigScale;
            record.TransformEdited = false;
            PruneIfPristine(record);
        }

        // Fully undo one edit: unhide + pristine transform, and forget the record.
        public void Revert(LevelEditRecord record)
        {
            if (record == null) return;
            Unhide(record);
            if (record.Target != null)
            {
                var t = record.Target.transform;
                t.position = record.OrigPos;
                t.rotation = record.OrigRot;
                t.localScale = record.OrigScale;
            }
            Remove(record);
        }

        public void RestoreAll()
        {
            foreach (var record in _records.ToArray())
                Revert(record);
        }

        private void PruneIfPristine(LevelEditRecord record)
        {
            if (!record.TransformEdited && !record.Hidden)
                Remove(record);
        }

        private void Remove(LevelEditRecord record)
        {
            _records.Remove(record);
            if (record.Target != null) _byId.Remove(record.Target.GetInstanceID());
        }

        // Scene reload destroyed the objects and undid every edit with them.
        public void OnSceneChanged()
        {
            _records.Clear();
            _byId.Clear();
        }

        public List<LevelEditData> Snapshot()
        {
            var list = new List<LevelEditData>();
            foreach (var r in _records)
            {
                if (r.Target == null) continue;
                list.Add(r.ToData());
            }
            return list;
        }

        // Re-apply saved edits after a load / scene reload.
        public void Apply(List<LevelEditData> edits, ObjectCatalog catalog, out int applied, out int skipped)
        {
            applied = 0;
            skipped = 0;
            if (edits == null) return;

            foreach (var edit in edits)
            {
                GameObject target = null;
                try { target = catalog.ResolveSource(edit.Path, edit.Name); }
                catch { }
                if (target == null)
                {
                    skipped++;
                    MapEditorPlugin.Logger.LogWarning($"[LEVELEDIT] Target not found for '{edit.Name}' ({edit.Path})");
                    continue;
                }

                var record = CaptureOriginal(target);
                if (edit.Pos != null)
                {
                    var t = target.transform;
                    t.position = VecUtil.ToVector3(edit.Pos);
                    t.eulerAngles = VecUtil.ToVector3(edit.Rot);
                    t.localScale = VecUtil.ToVector3(edit.Scale, Vector3.one);
                    record.TransformEdited = true;
                }
                if (edit.Hidden) Hide(target);
                applied++;
            }
        }
    }
}
