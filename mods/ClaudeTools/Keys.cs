using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>
    /// Reading the keyboard under either of Unity's input systems. Valheim has the old one (UnityEngine.Input); Mycopunk only the new Input
    /// System, where Input throws. The new one is read by reflection, so Claude Tools needs no reference to it: Keyboard.current[Key.F12].
    /// </summary>
    public static class Keys
    {
        private static bool _noLegacy;
        private static bool _looked;
        private static PropertyInfo _current, _pressed, _down;
        private static MethodInfo _key;
        private static Type _keyType;
        private static readonly Dictionary<KeyCode, object> Names = new Dictionary<KeyCode, object>();

        /// <summary>The key went down this frame.</summary>
        public static bool Down(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            if (!_noLegacy)
                try { return Input.GetKeyDown(key); }
                catch (InvalidOperationException) { _noLegacy = true; }   // (the game reads only the new Input System)
            return Read(key, true);
        }

        /// <summary>The key is held.</summary>
        public static bool Held(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            if (!_noLegacy)
                try { return Input.GetKey(key); }
                catch (InvalidOperationException) { _noLegacy = true; }
            return Read(key, false);
        }

        private static bool Read(KeyCode key, bool down)
        {
            if (!_looked)
            {
                _looked = true;
                Type keyboard = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                _keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
                _current = keyboard?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _key = _keyType != null ? keyboard?.GetMethod("get_Item", new[] { _keyType }) : null;
                Type button = Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem");
                _pressed = button?.GetProperty("isPressed");
                _down = button?.GetProperty("wasPressedThisFrame");
            }
            if (_current == null || _key == null || _pressed == null || _down == null) return false;
            object board = _current.GetValue(null, null);
            object name = KeyName(key);
            if (board == null || name == null) return false;
            object control = _key.Invoke(board, new[] { name });
            return control != null && (bool)(down ? _down : _pressed).GetValue(control, null);
        }

        /// <summary>The new Input System's Key for an old KeyCode (mostly the same name: LeftControl is LeftCtrl, Alpha1 is Digit1).</summary>
        private static object KeyName(KeyCode key)
        {
            if (Names.TryGetValue(key, out object known)) return known;
            string n = key.ToString();
            if (n.StartsWith("Alpha")) n = "Digit" + n.Substring(5);
            else if (n.StartsWith("Keypad") && n.Length == 7) n = "Numpad" + n.Substring(6);
            else switch (key)
            {
                case KeyCode.LeftControl: n = "LeftCtrl"; break;
                case KeyCode.RightControl: n = "RightCtrl"; break;
                case KeyCode.Return: n = "Enter"; break;
                case KeyCode.KeypadEnter: n = "NumpadEnter"; break;
                case KeyCode.LeftCommand: case KeyCode.LeftWindows: n = "LeftMeta"; break;
                case KeyCode.RightCommand: case KeyCode.RightWindows: n = "RightMeta"; break;
                case KeyCode.BackQuote: n = "Backquote"; break;
            }
            object value = null;
            try { value = Enum.Parse(_keyType, n, true); } catch (Exception) { }
            Names[key] = value;
            return value;
        }
    }
}
