using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FIHMapEditor
{
    // Edge-detected hotkeys + fly movement keys. Legacy Input stops reporting keyboard
    // state in this build while the New Input System keyboard device is disabled
    // (cursor-free mode), so every key read falls back to Win32 GetAsyncKeyState,
    // which bypasses Unity's input stack entirely.
    public class InputHandler
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly Dictionary<string, bool> _wasPressed = new Dictionary<string, bool>();

        private const float STICK_DEADZONE = 0.3f;

        // Clear edge-detection state so the first frame after regaining window focus
        // doesn't register a stale "just pressed".
        public void ResetEdges() => _wasPressed.Clear();

        public static bool KeyHeld(KeyCode key)
        {
            try { if (Input.GetKey(key)) return true; } catch { }

            // GetAsyncKeyState is global; never react while the game is unfocused.
            if (!Application.isFocused) return false;
            int vk = KeyCodeToVirtualKey(key);
            return vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public bool WasKeyPressed(string id, KeyCode key)
        {
            bool held = KeyHeld(key);

            // First query after a reset: absorb keys that are already physically held.
            // Without this, the very key that caused the reset (e.g. the cursor-toggle
            // key calling ResetEdges inside SetCursorFree) re-fires on the next frame
            // while the finger is still down, instantly undoing the toggle.
            if (!_wasPressed.TryGetValue(id, out bool was))
            {
                _wasPressed[id] = held;
                return false;
            }

            _wasPressed[id] = held;
            return held && !was;
        }

        public bool IsCtrlHeld() => KeyHeld(KeyCode.LeftControl) || KeyHeld(KeyCode.RightControl);
        public bool IsShiftHeld() => KeyHeld(KeyCode.LeftShift) || KeyHeld(KeyCode.RightShift)
            || (Gamepad.current?[UnityEngine.InputSystem.LowLevel.GamepadButton.East].isPressed == true);

        // Fly mode movement — keyboard + gamepad left stick / triggers
        public bool IsFlyForward() => KeyHeld(KeyCode.W) || GetLeftStickY() > STICK_DEADZONE;
        public bool IsFlyBack() => KeyHeld(KeyCode.S) || GetLeftStickY() < -STICK_DEADZONE;
        public bool IsFlyLeft() => KeyHeld(KeyCode.A) || GetLeftStickX() < -STICK_DEADZONE;
        public bool IsFlyRight() => KeyHeld(KeyCode.D) || GetLeftStickX() > STICK_DEADZONE;
        public bool IsFlyUp() => KeyHeld(KeyCode.Space) || GetRightTrigger() > STICK_DEADZONE;
        public bool IsFlyDown() => KeyHeld(KeyCode.LeftControl) || KeyHeld(KeyCode.RightControl) || GetLeftTrigger() > STICK_DEADZONE;

        private float GetLeftStickX() => Gamepad.current?.leftStick.x.ReadValue() ?? 0f;
        private float GetLeftStickY() => Gamepad.current?.leftStick.y.ReadValue() ?? 0f;
        private float GetRightTrigger() => Gamepad.current?.rightTrigger.ReadValue() ?? 0f;
        private float GetLeftTrigger() => Gamepad.current?.leftTrigger.ReadValue() ?? 0f;

        private static int KeyCodeToVirtualKey(KeyCode key)
        {
            // Letters
            if (key >= KeyCode.A && key <= KeyCode.Z) return 0x41 + (key - KeyCode.A);
            // Top-row digits
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 0x30 + (key - KeyCode.Alpha0);
            // Numpad digits
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return 0x60 + (key - KeyCode.Keypad0);
            // Function keys
            if (key >= KeyCode.F1 && key <= KeyCode.F15) return 0x70 + (key - KeyCode.F1);

            switch (key)
            {
                case KeyCode.Space: return 0x20;
                case KeyCode.Return: return 0x0D;
                case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.LeftShift: return 0xA0;
                case KeyCode.RightShift: return 0xA1;
                case KeyCode.LeftControl: return 0xA2;
                case KeyCode.RightControl: return 0xA3;
                case KeyCode.LeftAlt: return 0xA4;
                case KeyCode.RightAlt: return 0xA5;
                case KeyCode.CapsLock: return 0x14;
                case KeyCode.KeypadPlus: return 0x6B;
                case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadDivide: return 0x6F;
                case KeyCode.KeypadPeriod: return 0x6E;
                // OEM keys (US layout positions; fine for hotkey purposes)
                case KeyCode.Equals: return 0xBB;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Quote: return 0xDE;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.BackQuote: return 0xC0;
                default: return 0;
            }
        }
    }
}
