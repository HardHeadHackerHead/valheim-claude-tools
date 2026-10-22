using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace ClaudeTools.Mycopunk
{
    /// <summary>
    /// Mycopunk's part of "screen" (and "ui", the same command under the name Mycopunk players use): the game's own buttons
    /// (Pigeon.UI.Button, which aren't uGUI Selectables) listed by their text and pressed as the game presses them, and keys through the
    /// new Input System, so game code reading Keyboard.current (fKey.wasPressedThisFrame, or an input action bound to it) sees the press.
    /// </summary>
    internal sealed partial class MycopunkGame
    {
        // (a uGUI control that is also one of the game's buttons is listed once, as the game's)
        public override bool HideFromScreen(Selectable control) => control.GetComponent<Pigeon.UI.Button>() != null;

        public override IEnumerable<ScreenItem> ScreenItems(Canvas canvas)
        {
            foreach (Pigeon.UI.Button b in canvas.GetComponentsInChildren<Pigeon.UI.Button>(false))
            {
                if (b == null || !b.isActiveAndEnabled) continue;
                TMP_Text text = b.GetComponentsInChildren<TMP_Text>(false).FirstOrDefault(t => t.enabled && !string.IsNullOrWhiteSpace(t.text));
                Pigeon.UI.Button button = b;
                yield return new ScreenItem
                {
                    Go = b.gameObject, Kind = "button", Label = text != null ? Plain(text.text) : b.gameObject.name, On = true,
                    Click = () => Press(button),
                };
            }
        }

        /// <summary>
        /// As the game relays a press from one button to another (a null event: it skips the pointer checks, IgnoreEvents included): the
        /// pointer comes over it, goes down, comes up over it (the click: OnClickUp), and leaves.
        /// </summary>
        private static void Press(Pigeon.UI.Button b)
        {
            b.OnPointerEnter(null);
            b.OnPointerDown(null);
            b.OnPointerUp(null);
            b.OnPointerExit(null);
        }

        private static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "").Replace("\n", " ").Trim();

        public override IEnumerator Screen(string sub, string[] a, string rest, Action<JObject> output, Action<string> error) =>
            sub == "key" ? PressKey(a, output, error) : null;

        /// <summary>
        /// screen key &lt;key&gt; [frames=1]: the key down on the keyboard for that many frames, then up, as the new Input System's own events
        /// (the keys the player holds stay held). Names as the Input System has them (F8, Escape, Enter, Space, Tab, E, Digit1, LeftShift...).
        /// </summary>
        private static IEnumerator PressKey(string[] a, Action<JObject> output, Action<string> error)
        {
            if (a.Length < 3) { error("screen key <key> [frames=1], e.g. screen key F8 (the Input System's key names: Escape, Enter, Space, Tab, E, Digit1...)"); yield break; }
            if (!KeyNamed(a[2], out Key key)) { error($"\"{a[2]}\" is not a key (the Input System's names: F8, Escape, Enter, Space, Tab, E, Digit1, LeftShift...)"); yield break; }
            int frames = a.Length > 3 && int.TryParse(a[3], out int f) ? Mathf.Clamp(f, 1, 600) : 1;
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) { error("no keyboard"); yield break; }
            InputSystem.QueueStateEvent(keyboard, Held(keyboard, key, true));
            for (int k = 0; k < frames; k++) yield return null;
            InputSystem.QueueStateEvent(keyboard, Held(keyboard, key, false));
            yield return null; yield return null;   // (the game acts on it in its next Update)
            output(new JObject { ["pressed"] = key.ToString(), ["frames"] = frames });
        }

        /// <summary>The keyboard as it is now, with that key down (or up): a state event replaces every key, so the others are kept.</summary>
        private static KeyboardState Held(Keyboard keyboard, Key key, bool down)
        {
            var state = new KeyboardState();
            foreach (KeyControl k in keyboard.allKeys)
                if (k != null && k.isPressed && k.keyCode != key) state.Press(k.keyCode);
            state.Set(key, down);
            return state;
        }

        private static bool KeyNamed(string name, out Key key)
        {
            switch (name.ToLowerInvariant())
            {
                case "esc": key = UnityEngine.InputSystem.Key.Escape; return true;
                case "return": key = UnityEngine.InputSystem.Key.Enter; return true;
                case "ctrl": key = UnityEngine.InputSystem.Key.LeftCtrl; return true;
                case "shift": key = UnityEngine.InputSystem.Key.LeftShift; return true;
                case "alt": key = UnityEngine.InputSystem.Key.LeftAlt; return true;
            }
            if (name.Length == 1 && char.IsDigit(name[0])) name = "Digit" + name;
            return Enum.TryParse(name, true, out key) && key != UnityEngine.InputSystem.Key.None;
        }
    }
}
