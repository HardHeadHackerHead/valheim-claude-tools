using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// Valheim's part of "screen" (UiCommand.cs in Claude Tools): the inventory's item cells by the item in them, E on something as the game
    /// does it (screen use), the pause menu, and keys through the game's input (screen key). Claude Tools checks the cheat rule first.
    /// </summary>
    internal sealed partial class ValheimGame
    {
        public override bool HideFromScreen(Selectable control) => control.GetComponent<InventoryElement>() != null;

        // item cells: the inventory's own (the game's grids and mods' copies), by the item in them
        public override IEnumerable<ScreenItem> ScreenItems(Canvas canvas)
        {
            foreach (InventoryElement e in canvas.GetComponentsInChildren<InventoryElement>(false))
            {
                if (e.m_icon == null || !e.m_icon.enabled) continue;
                string name = e.m_tooltip != null && !string.IsNullOrEmpty(e.m_tooltip.m_topic) ? Localization.instance.Localize(e.m_tooltip.m_topic) : "";
                string amount = e.m_amount != null && e.m_amount.enabled ? " " + e.m_amount.text : "";
                UIInputHandler h = e.GetComponentInChildren<UIInputHandler>();
                yield return new ScreenItem { Go = h != null ? h.gameObject : e.gameObject, Kind = "item", Label = (name + amount).Trim(), On = true };
            }
        }

        public override IEnumerator Screen(string sub, string[] a, string rest, Action<JObject> output, Action<string> error)
        {
            switch (sub)
            {
                case "use": return Use(rest, output, error);
                case "pause": return Pause(output, error);
                case "key": return Key(a, output, error);
                default: return null;
            }
        }

        private static IEnumerator Use(string rest, Action<JObject> output, Action<string> error)
        {
            // E on something, as the game does it (Player.Interact: mods that hook E see it). "screen use <name>": the thing of that name
            // within a player's reach (3 m), whatever the crosshair is on (the game aims E from the camera behind the player, which a
            // fence can block); "screen use": what the crosshair is on. "alt" at the end: with the alternate key (Shift).
            global::Player p = global::Player.m_localPlayer;
            if (p == null) { error("no player"); yield break; }
            bool alt = rest.Trim().ToLowerInvariant().EndsWith(" alt") || rest.Trim().ToLowerInvariant() == "alt";
            string want = alt ? rest.Trim().Substring(0, rest.Trim().Length - 3).Trim().ToLowerInvariant() : rest.Trim().ToLowerInvariant();
            GameObject hover = p.GetHoverObject(), target = null;
            string Whole(GameObject g) => (g.GetComponentInParent<Piece>()?.name ?? g.transform.root.name).Replace("(Clone)", "");
            bool HoverHas(bool exact) => hover != null && (exact ? Whole(hover).ToLowerInvariant() == want
                : Whole(hover).ToLowerInvariant().Contains(want) || hover.name.ToLowerInvariant().Contains(want));
            if (want.Length == 0) target = hover;
            else if (HoverHas(true)) target = hover;
            else
            {
                // the nearest within reach called exactly that ("piece_workbench", not the extension beside it), or else containing it
                GameObject exact = null, part = null;
                float de = 3f, dp = 3f;
                foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (!(mb is Interactable)) continue;
                    string whole = Whole(mb.gameObject).ToLowerInvariant();
                    if (!(whole.Contains(want) || mb.name.ToLowerInvariant().Contains(want))) continue;
                    float d = Vector3.Distance(mb.transform.position, p.transform.position);
                    if (whole == want) { if (d < de) { de = d; exact = mb.gameObject; } }
                    else if (d < dp) { dp = d; part = mb.gameObject; }
                }
                target = exact != null ? exact : HoverHas(false) ? hover : part;
            }
            if (target == null) { error(want.Length == 0 ? "the player isn't looking at anything to use" : $"no \"{want}\" within reach (3 m): shoot near {want} first"); yield break; }
            var interact = AccessTools.Method(typeof(global::Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) });
            if (interact == null) { error("the game's Player.Interact wasn't found (a game update?)"); yield break; }
            interact.Invoke(p, new object[] { target, false, alt });
            yield return null; yield return null;
            output(new JObject { ["used"] = target.name.Replace("(Clone)", ""), ["on"] = Whole(target), ["alt"] = alt, ["byCrosshair"] = target == hover });
        }

        private static IEnumerator Pause(Action<JObject> output, Action<string> error)
        {
            // the game's own pause menu, on purpose ("screen key escape" closes one it opened by mistake): single player pauses
            if (global::Menu.instance == null) { error("the game's menu isn't there (at the main menu?)"); yield break; }
            if (!global::Menu.IsVisible()) global::Menu.instance.Show();
            yield return null;
            output(new JObject { ["paused"] = global::Menu.IsVisible() });
        }

        private static IEnumerator Key(string[] a, Action<JObject> output, Action<string> error)
        {
            if (a.Length < 3) { error("screen key <tab|escape|e|key name> [frames=1]"); yield break; }
            int frames = a.Length > 3 && int.TryParse(a[3], out int f) ? Mathf.Clamp(f, 1, 600) : 1;
            bool menuBefore = global::Menu.IsVisible();
            if (!FakeKeys.Press(a[2], frames, out string what)) { error($"\"{a[2]}\" is not a key or a game button"); yield break; }
            for (int k = 0; k < frames + 2; k++) yield return null;
            // Escape with nothing open opens the pause menu, which pauses single player (and every wait after it): closed again
            if (!menuBefore && global::Menu.IsVisible() && global::Menu.instance != null)
            {
                global::Menu.instance.Hide();
                output(new JObject { ["pressed"] = what, ["frames"] = frames, ["note"] = "nothing was open for it to close: it opened the pause menu, closed again" });
                yield break;
            }
            output(new JObject { ["pressed"] = what, ["frames"] = frames });
        }
    }

    /// <summary>
    /// Keys pressed by "screen key": for a few frames the game's input (ZInput) says the key or button is down, exactly as if the player
    /// pressed it. Pressing E also says "Use" (the game's button for it), Tab "Inventory", Escape "JoyMenu"'s keyboard twin Escape.
    /// </summary>
    internal static class FakeKeys
    {
        private static readonly HashSet<string> Buttons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<KeyCode> Keys = new HashSet<KeyCode>();
        private static int _from = -1, _until = -1;

        internal static bool Press(string name, int frames, out string what)
        {
            Buttons.Clear(); Keys.Clear();
            switch (name.ToLowerInvariant())
            {
                case "tab": case "inventory": Keys.Add(KeyCode.Tab); Buttons.Add("Inventory"); break;
                case "escape": case "esc": Keys.Add(KeyCode.Escape); break;
                case "e": case "use": Keys.Add(KeyCode.E); Buttons.Add("Use"); break;
                case "enter": case "return": Keys.Add(KeyCode.Return); break;
                default:
                    if (Enum.TryParse(name, true, out KeyCode k)) Keys.Add(k);
                    else Buttons.Add(name);   // a game button by its name ("Jump", "Map", ...)
                    break;
            }
            what = string.Join(", ", Keys.Select(k => k.ToString()).Concat(Buttons));
            _from = Time.frameCount + 1;   // (from the next frame: every reader sees the same press)
            _until = _from + frames - 1;
            return Keys.Count + Buttons.Count > 0;
        }

        private static bool Now(int frame) => frame >= _from && frame <= _until;
        private static bool Down() => Time.frameCount == _from;   // "pressed this frame": only the first frame

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown), typeof(string))]
        private static class ButtonDown { private static void Postfix(string name, ref bool __result) { if (!__result && Down() && Buttons.Contains(name)) __result = true; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), typeof(string))]
        private static class ButtonHeld { private static void Postfix(string name, ref bool __result) { if (!__result && Now(Time.frameCount) && Buttons.Contains(name)) __result = true; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown), typeof(KeyCode), typeof(bool))]
        private static class KeyDown { private static void Postfix(KeyCode key, ref bool __result) { if (!__result && Down() && Keys.Contains(key)) __result = true; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKey), typeof(KeyCode), typeof(bool))]
        private static class KeyHeld { private static void Postfix(KeyCode key, ref bool __result) { if (!__result && Now(Time.frameCount) && Keys.Contains(key)) __result = true; } }

        // a mod (or the game) that "uses up" a press resets it: then it is gone for the rest of the frame, as a real one
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.ResetButtonStatus), typeof(string))]
        private static class Reset
        {
            private static void Postfix(string name)
            {
                if (!Buttons.Remove(name)) return;
                if (string.Equals(name, "Inventory", StringComparison.OrdinalIgnoreCase)) Keys.Remove(KeyCode.Tab);
                if (string.Equals(name, "Use", StringComparison.OrdinalIgnoreCase)) Keys.Remove(KeyCode.E);
            }
        }
    }
}
