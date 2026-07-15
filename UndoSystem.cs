using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    // Ctrl+Z stack: every destructive edit pushes a closure that puts things back.
    // Cleared on map load / scene change, where the captured targets no longer exist.
    public class UndoSystem
    {
        // Entries capture runtime objects and therefore cannot be serialized or safely kept
        // across a whole-map/scene replacement.
        private class Entry
        {
            public string Label;
            public Action Apply;
        }

        private readonly List<Entry> _stack = new List<Entry>();
        private const int MAX_ENTRIES = 60;

        public int Count => _stack.Count;

        public void Push(string label, Action apply)
        {
            // Bound retained closures: many capture Unity object graphs that would otherwise
            // remain alive long after an editing session.
            _stack.Add(new Entry { Label = label, Apply = apply });
            if (_stack.Count > MAX_ENTRIES) _stack.RemoveAt(0);
        }

        public bool Undo(out string label)
        {
            label = null;
            if (_stack.Count == 0) return false;

            var e = _stack[_stack.Count - 1];
            // Remove before invoking. A failed or partially-applied operation must not become
            // an endlessly retryable top entry.
            _stack.RemoveAt(_stack.Count - 1);
            label = e.Label;
            try
            {
                e.Apply();
                return true;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[UNDO] '{e.Label}' failed: {ex.Message}");
                return false;
            }
        }

        public void Clear() => _stack.Clear();
    }
}
