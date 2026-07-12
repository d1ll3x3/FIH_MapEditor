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

        // The transform the user wants. Kept separately from the live transform because
        // game systems (FishNet sync, animators) can overwrite the live one — we save
        // and enforce from these values, never from a possibly-reset live transform.
        public Vector3 EditedPos;
        public Quaternion EditedRot;
        public Vector3 EditedScale;

        // Exact components disabled by Hide, so unhide is lossless.
        public List<Renderer> HiddenRenderers;
        public List<Collider> HiddenColliders;

        // Transform-controlling components (FishNet sync, animators) disabled while the
        // object is edited, so nothing fights our transform. Re-enabled on revert.
        public List<Behaviour> NeutralizedBehaviours;
        public Rigidbody Rb;
        public bool RbWasKinematic;

        public bool EnforceWarned;   // log the "something is fighting us" line only once

        public LevelEditData ToData()
        {
            var data = new LevelEditData { Path = Path, Name = Name, Hidden = Hidden };
            if (TransformEdited)
            {
                data.Pos = VecUtil.ToArray(EditedPos);
                data.Rot = VecUtil.ToArray(EditedRot.eulerAngles);
                data.Scale = VecUtil.ToArray(EditedScale);
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

            // Level objects are live game objects: FishNet components re-sync their
            // transform from the network tick and animators re-pose them every frame,
            // silently undoing any client-side edit. Disable those controllers (never
            // destroy — everything is restored on revert).
            Neutralize(record);
            return record;
        }

        private static void Neutralize(LevelEditRecord record)
        {
            try
            {
                record.NeutralizedBehaviours = new List<Behaviour>();
                foreach (var comp in record.Target.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;

                    string fullName = null;
                    try { fullName = comp.GetIl2CppType()?.FullName; }
                    catch { }
                    if (fullName == null) continue;

                    bool isController =
                        (fullName.StartsWith("FishNet") && fullName != "FishNet.Object.NetworkObject") ||
                        fullName == "UnityEngine.Animator" ||
                        fullName == "UnityEngine.Animation" ||
                        fullName == "UnityEngine.Playables.PlayableDirector";

                    if (isController)
                    {
                        var beh = comp.TryCast<Behaviour>();
                        if (beh != null && beh.enabled)
                        {
                            beh.enabled = false;
                            record.NeutralizedBehaviours.Add(beh);
                        }
                        continue;
                    }

                    var rb = comp.TryCast<Rigidbody>();
                    if (rb != null && record.Rb == null)
                    {
                        record.Rb = rb;
                        record.RbWasKinematic = rb.isKinematic;
                        rb.isKinematic = true; // physics must not drag the object away
                    }
                }
                if (record.NeutralizedBehaviours.Count > 0 || record.Rb != null)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[LEVELEDIT] '{record.Name}': neutralized {record.NeutralizedBehaviours.Count} controller(s)" +
                        (record.Rb != null ? " + rigidbody" : ""));
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[LEVELEDIT] Neutralize error on '{record.Name}': {ex.Message}");
            }
        }

        // Undo Neutralize: give the object back to the game exactly as it was.
        private static void Reactivate(LevelEditRecord record)
        {
            try
            {
                if (record.NeutralizedBehaviours != null)
                {
                    foreach (var beh in record.NeutralizedBehaviours)
                        if (beh != null) beh.enabled = true;
                    record.NeutralizedBehaviours = null;
                }
                if (record.Rb != null)
                {
                    record.Rb.isKinematic = record.RbWasKinematic;
                    record.Rb = null;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[LEVELEDIT] Reactivate error on '{record.Name}': {ex.Message}");
            }
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
            if (record.TransformEdited)
            {
                record.EditedPos = t.position;
                record.EditedRot = t.rotation;
                record.EditedScale = t.localScale;
            }
            PruneIfPristine(record);
        }

        // Called every frame while a map is active. Some game systems keep fighting the
        // edits even after Neutralize (server-driven resets, pooled visibility managers);
        // whatever they undo, this puts back.
        public void EnforceEdits(Transform beingDragged)
        {
            foreach (var record in _records)
            {
                if (record.Target == null) continue;
                try
                {
                    var t = record.Target.transform;

                    if (record.TransformEdited && t != beingDragged)
                    {
                        if (Vector3.Distance(t.position, record.EditedPos) > 0.01f ||
                            Quaternion.Angle(t.rotation, record.EditedRot) > 0.1f ||
                            Vector3.Distance(t.localScale, record.EditedScale) > 0.01f)
                        {
                            if (!record.EnforceWarned)
                            {
                                record.EnforceWarned = true;
                                MapEditorPlugin.Logger.LogInfo(
                                    $"[LEVELEDIT] '{record.Name}' is being reset by the game — enforcing the edit every frame.");
                            }
                            t.position = record.EditedPos;
                            t.rotation = record.EditedRot;
                            t.localScale = record.EditedScale;
                        }
                    }

                    if (record.Hidden)
                    {
                        if (record.HiddenRenderers != null)
                            foreach (var r in record.HiddenRenderers)
                                if (r != null && r.enabled) r.enabled = false;
                        if (record.HiddenColliders != null)
                            foreach (var c in record.HiddenColliders)
                                if (c != null && c.enabled)
                                {
                                    GroundRegistrar.Unregister(c); // re-enabled by the game → unregister before re-hiding
                                    c.enabled = false;
                                }
                    }
                }
                catch { }
            }
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
                    // Remove it from the game's ground system BEFORE disabling — a
                    // disabled collider never fires OnCollisionExit, so the player can be
                    // left permanently "grounded" on it (endless jump reset).
                    GroundRegistrar.Unregister(c);
                    c.enabled = false;
                    record.HiddenColliders.Add(c);
                }
                record.Hidden = true;
                MapEditorPlugin.Logger.LogInfo(
                    $"[LEVELEDIT] Hid '{record.Name}': {record.HiddenRenderers.Count} renderer(s), " +
                    $"{record.HiddenColliders.Count} collider(s) disabled.");
                if (record.HiddenRenderers.Count == 0)
                    MapEditorPlugin.Logger.LogWarning(
                        $"[LEVELEDIT] '{record.Name}' had no enabled renderers — it was already invisible (collision-only object?).");
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
                        if (c != null)
                        {
                            c.enabled = true;
                            GroundRegistrar.RegisterLevelCollider(c); // put it back in the ground system
                        }
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

        // Fully undo one edit: unhide + pristine transform + controllers back on, and
        // forget the record.
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
            Reactivate(record);
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
            {
                Reactivate(record);   // give the untouched object back to the game
                Remove(record);
            }
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
                    record.EditedPos = t.position;
                    record.EditedRot = t.rotation;
                    record.EditedScale = t.localScale;
                }
                if (edit.Hidden) Hide(target);
                applied++;
            }
        }
    }
}
