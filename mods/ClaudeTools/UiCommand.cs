using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClaudeTools
{
    /// <summary>
    /// "screen": test a mod's windows the way a player uses them. See what is on screen (every button, box, slider and text, where it is, and
    /// which overlap), click a button or an item cell by its words, type in a text box, move a slider, press a key for one frame.
    /// Looking is open to everyone; clicking, typing and keys act as the player would, so they follow the game's cheat rule (single player,
    /// the host, or devcommands on), like shoot use. Keys, E and the pause menu go through the game's own input: its adapter does them.
    /// </summary>
    public partial class Plugin
    {
        private const string UiUsage =
            "screen list [text] | texts [text] | overlaps [text] | click <words> [n] | type <text> | submit | slider <n> <value> | use [name] [alt] | key <tab|escape|e|key name> [frames=1] | pause: " +
            "see and use what is on screen (buttons, item cells, text boxes, sliders) by their words; use presses E on what the player looks at (as the game does); pause opens the game's pause menu (what Escape does with nothing open: at the end of a session, as the player asked); keys reach mods' windows (the game's own reading of E may not see them: use screen use); click, type, slider, use and key act as the player " +
            "(only where cheats are allowed)";

        private void RegisterUiCommands() => Builtin("screen", UiUsage, (a, output, error) => UiCommand(a, output, error));

        private class UiThing
        {
            public string Kind, Label, Window;
            public GameObject Go;
            public Rect Screen;   // pixels, y down from the top
            public bool On;       // can be clicked now
            public Action Click;  // the game's own way to press it (its adapter's), or null for pointer events
        }

        private static IEnumerator UiCommand(string[] a, Action<JObject> output, Action<string> error)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "list";
            string rest = a.Length > 2 ? string.Join(" ", a.Skip(2)) : "";
            bool acts = sub == "click" || sub == "type" || sub == "submit" || sub == "slider" || sub == "key" || sub == "use";
            if (acts && !CheatsAllowed()) { error(CheatRefusal("screen " + sub)); yield break; }
            switch (sub)
            {
                case "list":
                {
                    List<UiThing> all = UiThings(true, false).Where(t => UiMatches(t, rest)).ToList();
                    output(new JObject { ["count"] = all.Count, ["things"] = new JArray(all.Take(250).Select(UiJson)), ["note"] = all.Count > 250 ? "first 250: add words to narrow it" : null });
                    yield break;
                }
                case "texts":
                {
                    List<UiThing> all = UiThings(false, true).Where(t => UiMatches(t, rest)).ToList();
                    output(new JObject { ["count"] = all.Count, ["texts"] = new JArray(all.Take(300).Select(UiJson)), ["note"] = all.Count > 300 ? "first 300: add words to narrow it" : null });
                    yield break;
                }
                case "overlaps":
                {
                    List<UiThing> all = UiThings(true, true).Where(t => UiMatches(t, rest)).ToList();
                    var pairs = new JArray();
                    for (int i = 0; i < all.Count; i++)
                        for (int j = i + 1; j < all.Count; j++)
                        {
                            UiThing p = all[i], q = all[j];
                            if (p.Go.transform.IsChildOf(q.Go.transform) || q.Go.transform.IsChildOf(p.Go.transform)) continue;   // (a button and its own label)
                            if (p.Go.transform.root != q.Go.transform.root || p.Go.GetComponentInParent<Canvas>()?.rootCanvas != q.Go.GetComponentInParent<Canvas>()?.rootCanvas) continue;   // (one layer over another: the HUD under the inventory)
                            if (p.Kind == "text" && q.Kind == "text" && p.Go.transform.parent == q.Go.transform.parent && p.Label == q.Label) continue; // (a text and its shadow)
                            float area = UiOverlap(p.Screen, q.Screen), smaller = Mathf.Min(p.Screen.width * p.Screen.height, q.Screen.width * q.Screen.height);
                            if (smaller < 1f || area < 0.2f * smaller) continue;
                            // the game's invisible catch-alls (the inventory's drop-on-the-ground area covers the whole screen) cover everything
                            float screenArea = UnityEngine.Screen.width * (float)UnityEngine.Screen.height;
                            if (p.Screen.width * p.Screen.height > 0.4f * screenArea || q.Screen.width * q.Screen.height > 0.4f * screenArea) continue;
                            pairs.Add(new JObject { ["a"] = UiJson(p), ["b"] = UiJson(q), ["covered"] = Math.Round(area / smaller, 2) });
                            if (pairs.Count >= 60) break;
                        }
                    output(new JObject { ["overlaps"] = pairs, ["note"] = pairs.Count == 0 ? "nothing covers anything else by more than a fifth" : "covered: the share of the smaller one under the other" });
                    yield break;
                }
                case "click":
                {
                    // the last word can be which one (1 = first) when several match
                    int n = 1;
                    string words = rest;
                    Match m = Regex.Match(rest, @"^(.*\S)\s+(\d+)$");
                    if (m.Success) { words = m.Groups[1].Value; n = int.Parse(m.Groups[2].Value); }
                    List<UiThing> clickable = UiThings(true, false).Where(t => t.Kind != "slider").ToList();   // (a text box: clicking it puts the cursor in it)
                    // by its words: exactly, then containing them; else by its object's name (a button showing only an icon)
                    List<UiThing> found = clickable.Where(t => string.Equals(t.Label, words, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (found.Count == 0) found = clickable.Where(t => t.Label.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (found.Count == 0) found = clickable.Where(t => string.Equals(t.Go.name, words, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (found.Count == 0) found = clickable.Where(t => t.Go.name.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (found.Count == 0) { error($"nothing on screen says \"{words}\" (screen list shows what is there)"); yield break; }
                    if (n < 1 || n > found.Count) { error($"{found.Count} say \"{words}\": pick 1 to {found.Count}"); yield break; }
                    UiThing t = found[n - 1];
                    if (!t.On) { error($"\"{t.Label}\" can't be clicked now (greyed out)"); yield break; }
                    TMP_InputField box = t.Go.GetComponent<TMP_InputField>();
                    if (box != null) { box.Select(); box.ActivateInputField(); }   // (then screen type types in it)
                    else if (t.Click != null) t.Click();
                    else UiClick(t);
                    yield return null; yield return null;   // (mods act on a click in their next Update)
                    output(new JObject { ["clicked"] = UiJson(t), ["alsoMatched"] = found.Count > 1 ? found.Count - 1 : 0 });
                    yield break;
                }
                case "type":
                case "submit":
                {
                    TMP_InputField field = UiFocusedField() ?? UiThings(true, false).Where(x => x.Kind == "input" && x.On).Select(x => x.Go.GetComponent<TMP_InputField>()).FirstOrDefault();
                    if (field == null) { error("no text box on screen"); yield break; }
                    if (sub == "type")
                    {
                        field.ActivateInputField();
                        field.text = rest;   // (fires onValueChanged, as typing does)
                    }
                    else { field.onSubmit.Invoke(field.text); field.onEndEdit.Invoke(field.text); }
                    yield return null; yield return null;
                    output(new JObject { [sub == "type" ? "typed" : "submitted"] = field.text, ["box"] = UiPathOf(field.transform) });
                    yield break;
                }
                case "slider":
                {
                    List<UiThing> sliders = UiThings(true, false).Where(x => x.Kind == "slider").ToList();
                    if (a.Length < 4 || !int.TryParse(a[2], out int i) || !float.TryParse(a[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    { error("screen slider <n> <value>: n is which slider (screen list shows them, 1 = first)"); yield break; }
                    if (i < 1 || i > sliders.Count) { error($"{sliders.Count} sliders on screen"); yield break; }
                    Slider s = sliders[i - 1].Go.GetComponent<Slider>();
                    s.value = v;   // (fires onValueChanged, as dragging does)
                    yield return null; yield return null;
                    output(new JObject { ["slider"] = UiJson(sliders[i - 1]), ["value"] = s.value });
                    yield break;
                }
                default:
                {
                    // the game's own (use, pause, key: through the game's input, so its adapter does them), stepped here so its errors are reported
                    IEnumerator game = Adapter.Screen(sub, a, rest, output, error);
                    if (game == null)
                    {
                        error(sub == "use" || sub == "pause" || sub == "key" ? $"screen {sub} isn't there for {Adapter.Game} (its adapter doesn't do it)" : UiUsage);
                        yield break;
                    }
                    while (game.MoveNext()) yield return game.Current;
                    yield break;
                }
            }
        }

        private static bool UiMatches(UiThing t, string words) =>
            string.IsNullOrEmpty(words) || t.Label.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0 || t.Window.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0;

        private static JObject UiJson(UiThing t) => new JObject
        {
            ["kind"] = t.Kind, ["label"] = t.Label, ["window"] = t.Window, ["on"] = t.On,
            ["x"] = Mathf.RoundToInt(t.Screen.x), ["y"] = Mathf.RoundToInt(t.Screen.y), ["w"] = Mathf.RoundToInt(t.Screen.width), ["h"] = Mathf.RoundToInt(t.Screen.height),
        };

        /// <summary>What is on screen now: buttons, toggles, sliders, text boxes and item cells (controls), and/or texts. Only what can be seen.</summary>
        private static List<UiThing> UiThings(bool controls, bool texts)
        {
            var list = new List<UiThing>();
            var screen = new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height);
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.InstanceID).Where(c => c.isRootCanvas && c.enabled && c.gameObject.activeInHierarchy))
            {
                if (controls)
                {
                    foreach (Selectable s in canvas.GetComponentsInChildren<Selectable>(false))
                    {
                        if (s is Scrollbar || Adapter.HideFromScreen(s)) continue;   // (an item cell is listed once, as its item)
                        string kind = s is Button ? "button" : s is Toggle ? "toggle" : s is Slider ? "slider" : s is TMP_InputField ? "input" : null;
                        if (kind == null) continue;
                        UiAdd(list, canvas, s.gameObject, kind, kind == "input" ? UiInputLabel((TMP_InputField)s) : UiLabelOf(s.gameObject), s.IsInteractable(), screen);
                    }
                    // item cells: the game's own (Valheim's inventory grids and mods' copies), by the item in them
                    foreach (GameAdapter.ScreenItem item in Adapter.ScreenItems(canvas))
                        UiAdd(list, canvas, item.Go, item.Kind, item.Label, item.On, screen, item.Click);
                }
                if (texts)
                    foreach (TMP_Text t in canvas.GetComponentsInChildren<TMP_Text>(false))
                    {
                        if (!t.enabled || string.IsNullOrWhiteSpace(UiPlain(t.text)) || t.color.a < 0.05f) continue;
                        UiAdd(list, canvas, t.gameObject, "text", UiPlain(t.text), true, screen);
                    }
            }
            return list;
        }

        private static void UiAdd(List<UiThing> list, Canvas canvas, GameObject go, string kind, string label, bool on, Rect screen, Action click = null)
        {
            var rt = go.transform as RectTransform;
            if (rt == null) return;
            Rect r = UiScreenRect(rt);
            // a text by its letters, not its box (a short line in a wide box would "overlap" whatever sits in the empty part)
            if (kind == "text" && go.GetComponent<TMP_Text>() is TMP_Text tt && tt.textBounds.size.x > 0.5f)
                r = UiScreenRect(rt, tt.textBounds);
            if (r.width < 1f || r.height < 1f || UiOverlap(r, screen) < 1f) return;   // (off screen: the HUD hides by moving away)
            if (UiFaded(go.transform)) return;
            // scrolled out of a list's view
            foreach (RectMask2D mask in go.GetComponentsInParent<RectMask2D>())
                if (UiOverlap(r, UiScreenRect(mask.rectTransform)) < 1f) return;
            list.Add(new UiThing { Kind = kind, Label = label ?? "", Window = UiWindowOf(canvas, go.transform), Go = go, Screen = r, On = on, Click = click });
        }

        private static bool UiFaded(Transform t)
        {
            float alpha = 1f;
            foreach (CanvasGroup g in t.GetComponentsInParent<CanvasGroup>())
            {
                alpha *= g.alpha;
                if (g.ignoreParentGroups) break;
            }
            return alpha < 0.05f;
        }

        /// <summary>Screen pixels, y down from the top (overlay canvases: world corners are screen points).</summary>
        private static Rect UiScreenRect(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = UiCamera(canvas);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]), b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            return new Rect(x0, UnityEngine.Screen.height - y1, x1 - x0, y1 - y0);
        }

        /// <summary>Part of a rect (local bounds, as TMP's textBounds) in screen pixels, y down from the top.</summary>
        private static Rect UiScreenRect(RectTransform rt, Bounds local)
        {
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = UiCamera(canvas);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(local.min)), b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(local.max));
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            return new Rect(x0, UnityEngine.Screen.height - y1, x1 - x0, y1 - y0);
        }

        /// <summary>The camera a canvas is drawn with: none for an overlay; its own, or else the scene's main camera (the main menu's
        /// canvas has none set: without one, everything on it measured off screen).</summary>
        private static Camera UiCamera(Canvas canvas)
        {
            if (canvas == null) return null;
            Canvas root = canvas.rootCanvas;
            if (root.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return root.worldCamera != null ? root.worldCamera : Camera.main;
        }

        private static float UiOverlap(Rect p, Rect q)
        {
            float w = Mathf.Min(p.xMax, q.xMax) - Mathf.Max(p.xMin, q.xMin), h = Mathf.Min(p.yMax, q.yMax) - Mathf.Max(p.yMin, q.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        private static string UiWindowOf(Canvas canvas, Transform t)
        {
            // the outermost object under the canvas that isn't just a holder: what a player would call the window
            Transform w = t;
            while (w.parent != null && w.parent != canvas.transform) w = w.parent;
            return canvas.name + "/" + w.name;
        }

        private static string UiLabelOf(GameObject go)
        {
            TMP_Text t = go.GetComponentsInChildren<TMP_Text>(false).FirstOrDefault(x => x.enabled && !string.IsNullOrWhiteSpace(x.text));
            if (t != null) return UiPlain(t.text);
            Text legacy = go.GetComponentsInChildren<Text>(false).FirstOrDefault(x => x.enabled && !string.IsNullOrWhiteSpace(x.text));
            return legacy != null ? UiPlain(legacy.text) : go.name;
        }

        private static string UiInputLabel(TMP_InputField f) =>
            !string.IsNullOrEmpty(f.text) ? f.text : f.placeholder is TMP_Text p && !string.IsNullOrEmpty(p.text) ? "(" + UiPlain(p.text) + ")" : "(text box)";

        private static string UiPlain(string s) => Regex.Replace(s ?? "", "<[^>]*>", "").Replace("\n", " ").Replace("​", "").Trim();   // (an empty text box holds a zero-width space)

        private static string UiPathOf(Transform t)
        {
            var parts = new List<string>();
            for (Transform x = t; x != null && parts.Count < 6; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts);
        }

        private static TMP_InputField UiFocusedField()
        {
            GameObject sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            TMP_InputField f = sel != null ? sel.GetComponent<TMP_InputField>() : null;
            return f != null && f.isActiveAndEnabled ? f : null;
        }

        /// <summary>
        /// A real click: pointer down, up and click, as the event system sends them (buttons and the inventory's cells both answer), at the
        /// middle of the thing (buttons that check the pointer is still over them on release).
        /// </summary>
        private static void UiClick(UiThing t)
        {
            GameObject go = t.Go;
            var middle = new Vector2(t.Screen.center.x, UnityEngine.Screen.height - t.Screen.center.y);   // (event positions run up from the bottom)
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1, position = middle, pressPosition = middle };
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerClickHandler);
        }
    }
}
