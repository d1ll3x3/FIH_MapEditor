using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    // Ctrl+Z stack: every destructive edit pushes a closure that puts things back.
    // Cleared on map load / scene change, where the captured targets no longer exist.
    public class UndoSystem
    {
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
            _stack.Add(new Entry { Label = label, Apply = apply });
            if (_stack.Count > MAX_ENTRIES) _stack.RemoveAt(0);
        }

        public bool Undo(out string label)
        {
            label = null;
            if (_stack.Count == 0) return false;

            var e = _stack[_stack.Count - 1];
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
