using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// Commands for testing mods without anyone at the keyboard: what the game shows for a word key, skill levels, the character's saved data,
    /// status effects with their time and carried state, an item's data, dragging items between slots as the inventory window does, and
    /// crafting or upgrading at the station the player has open. The ones that change things only where cheats are allowed.
    /// </summary>
    public partial class ValheimPlugin
    {
        private static readonly FieldInfo CraftRecipe = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");
        private static readonly FieldInfo CraftUpgrade = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");
        private static readonly FieldInfo CraftVariant = AccessTools.Field(typeof(InventoryGui), "m_craftVariant");
        private static readonly MethodInfo DoCraftingMethod = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
        private static readonly FieldInfo CurrentStation = AccessTools.Field(typeof(Player), "m_currentStation");

        private void RegisterTesting()
        {
            Builtin("raven", "raven [seen [munin|hugin|all] | seenlist | unseen <key...> | dismiss]: what the ravens (Hugin, and Munin) have to say to this character now: the current message, the ones " +
                "waiting (from world keys and earlier messages), the fixed ones near guide points within 100 m, and whether this character has seen each; " +
                "seen marks the waiting and nearby ones as read for this character (as talking to the raven does); seenlist lists what this character has " +
                "seen; unseen takes keys off that list (the raven tells them again when they come up); dismiss sends the ravens away now and empties the queue " +
                "of messages waiting (the game keeps it across worlds and characters)", (a, output, error) =>
                {
                    Vector3 me = Player.m_localPlayer.transform.position;
                    var temp = AccessTools.Field(typeof(Raven), "m_tempTexts")?.GetValue(null) as List<Raven.RavenText> ?? new List<Raven.RavenText>();
                    var fixedTexts = AccessTools.Field(typeof(Raven), "m_staticTexts")?.GetValue(null) as List<Raven.RavenText> ?? new List<Raven.RavenText>();
                    var near = fixedTexts.Where(t => t != null && t.m_guidePoint != null && Vector3.Distance(me, t.m_guidePoint.transform.position) < 100f).ToList();
                    var shown = AccessTools.Field(typeof(Player), "m_shownTutorials")?.GetValue(Player.m_localPlayer) as HashSet<string>;
                    string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                    if (sub == "seenlist")
                    {
                        output(new JObject { ["character"] = Player.m_localPlayer.GetPlayerName(), ["seen"] = new JArray((shown ?? new HashSet<string>()).OrderBy(k => k)) });
                        return null;
                    }
                    if (sub == "unseen")
                    {
                        var removed = new JArray();
                        foreach (string k in a.Skip(2)) if (shown != null && shown.Remove(k)) removed.Add(k);
                        output(new JObject { ["character"] = Player.m_localPlayer.GetPlayerName(), ["noLongerSeen"] = removed,
                            ["note"] = "the raven will tell these again when they come up; saved with the character the next time it saves (console save, or logging out)" });
                        return null;
                    }
                    if (sub == "dismiss")
                    {
                        int queued = temp.Count;
                        temp.Clear();   // (the game keeps this queue across worlds and characters)
                        var gone = new JArray();
                        foreach (Raven r in UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None))
                        {
                            AccessTools.Field(typeof(Raven), "m_currentText")?.SetValue(r, null);
                            try { AccessTools.Method(typeof(Raven), "FlyAway")?.Invoke(r, new object[] { true }); } catch (Exception) { }
                            gone.Add(r.m_name);
                        }
                        output(new JObject { ["sentAway"] = gone, ["queueCleared"] = queued });
                        return null;
                    }
                    if (a.Length > 1 && a[1].Equals("seen", StringComparison.OrdinalIgnoreCase))
                    {
                        string which = a.Length > 2 ? a[2].ToLowerInvariant() : "munin";
                        var marked = new JArray();
                        foreach (Raven.RavenText t in temp.Concat(near))
                        {
                            if (t == null || t.m_key.Length == 0 || (which == "munin" && !t.m_munin) || (which == "hugin" && t.m_munin)) continue;
                            if (!Player.m_localPlayer.HaveSeenTutorial(t.m_key)) { Player.m_localPlayer.SetSeenTutorial(t.m_key); marked.Add(t.m_key); }
                        }
                        output(new JObject { ["markedSeen"] = marked, ["note"] = "saved with the character the next time it saves (console save, or logging out)" });
                        return null;
                    }
                    var ravens = new JArray();
                    foreach (Raven r in UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None))
                    {
                        var cur = AccessTools.Field(typeof(Raven), "m_currentText")?.GetValue(r) as Raven.RavenText;
                        ravens.Add(new JObject { ["name"] = r.m_name, ["munin"] = r.m_isMunin, ["at"] = Vec(r.transform.position), ["saying"] = cur != null ? RavenTextJson(cur, me) : null });
                    }
                    output(new JObject
                    {
                        ["ravens"] = ravens,
                        ["waiting"] = new JArray(temp.Where(t => t != null).Select(t => RavenTextJson(t, me))),
                        ["nearby"] = new JArray(near.Select(t => RavenTextJson(t, me))),
                    });
                    return null;
                });

            Builtin("words", "words <$key|text>...: what the game shows for each key now (in its current language, with any setting a mod fills in): words $recycler_piece_desc",
                (a, output, error) =>
                {
                    if (Localization.instance == null) { error("the game's words aren't loaded yet"); return null; }
                    if (a.Length < 2) { error("words <$key>... e.g. words $piece_workbench"); return null; }
                    var o = new JObject();
                    foreach (string k in a.Skip(1)) o[k] = Localization.instance.Localize(k.StartsWith("$") ? k : "$" + k);
                    output(o);
                    return null;
                });

            Builtin("skills", "skills [text]: every skill the player has, mods' too, with its level (0-100) and progress to the next; text: only those whose name contains it",
                (a, output, error) =>
                {
                    Skills skills = Player.m_localPlayer.GetSkills();
                    string want = a.Length > 1 ? a[1].ToLowerInvariant() : null;
                    var list = new JArray();
                    foreach (Skills.Skill s in skills.GetSkillList().OrderByDescending(s => s.m_level))
                    {
                        string type = s.m_info.m_skill.ToString();
                        string name = Localization.instance.Localize("$skill_" + type.ToLowerInvariant());
                        if (want != null && !name.ToLowerInvariant().Contains(want) && !type.ToLowerInvariant().Contains(want)) continue;
                        list.Add(new JObject { ["name"] = name, ["type"] = type, ["level"] = Math.Round(s.m_level, 2), ["progress"] = Math.Round(s.m_accumulator, 2) });
                    }
                    output(new JObject { ["count"] = list.Count, ["skills"] = list });
                    return null;
                });

            Builtin("playerdata", "playerdata [prefix]: the character's saved data (Player.m_customData: every mod's keys, e.g. a skill's backup), only keys starting with the prefix if given",
                (a, output, error) =>
                {
                    string prefix = a.Length > 1 ? a[1] : "";
                    var o = new JObject();
                    foreach (var kv in Player.m_localPlayer.m_customData.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
                        o[kv.Key] = kv.Value.Length > 300 ? kv.Value.Substring(0, 300) + "... (" + kv.Value.Length + " characters)" : kv.Value;
                    output(new JObject { ["count"] = o.Count, ["data"] = o });
                    return null;
                });

            Builtin("effects", "effects: the player's status effects: name, time left (seconds; null: no end), length, and any state a mod carries over a reload (Carry)",
                (a, output, error) =>
                {
                    var list = new JArray();
                    foreach (StatusEffect se in Player.m_localPlayer.GetSEMan().GetStatusEffects())
                    {
                        if (se == null) continue;
                        var o = new JObject
                        {
                            ["name"] = se.name, ["shown"] = Localization.instance.Localize(se.m_name), ["type"] = se.GetType().Name,
                            ["timeLeft"] = se.m_ttl > 0f ? (JToken)Math.Round(se.GetRemaningTime(), 1) : JValue.CreateNull(), ["length"] = se.m_ttl,
                        };
                        PropertyInfo carry = se.GetType().GetProperty("Carry", BindingFlags.Public | BindingFlags.Instance);
                        if (carry != null && carry.PropertyType == typeof(float)) o["carry"] = Math.Round((float)carry.GetValue(se), 3);
                        list.Add(o);
                    }
                    output(new JObject { ["count"] = list.Count, ["effects"] = list });
                    return null;
                });

            Builtin("itemdata", "itemdata <x,y> [set <key> <value> | remove <key>]: an item in the player's bag (by its slot, as inventory lists it) with its saved data " +
                "(m_customData: mods' keys); set or remove one key (only where cheats are allowed)", (a, output, error) =>
                {
                    if (a.Length < 2 || !Slot(a[1], out Vector2i at)) { error("itemdata <x,y> [set <key> <value> | remove <key>]"); return null; }
                    ItemDrop.ItemData item = Player.m_localPlayer.GetInventory().GetItemAt(at.x, at.y);
                    if (item == null) { error($"nothing in slot {at.x},{at.y}"); return null; }
                    if (a.Length > 2)
                    {
                        if (!CheatsAllowed()) { error(CheatRefusal("itemdata set")); return null; }
                        string op = a[2].ToLowerInvariant();
                        if (op == "set" && a.Length > 4) item.m_customData[a[3]] = string.Join(" ", a.Skip(4));
                        else if (op == "remove" && a.Length > 3) item.m_customData.Remove(a[3]);
                        else { error("itemdata <x,y> set <key> <value> | remove <key>"); return null; }
                        Player.m_localPlayer.GetInventory().m_onChanged?.Invoke();   // (saved like any change to the bag)
                    }
                    output(ItemJson(item));
                    return null;
                });

            Builtin("inv", "inv move <x,y> <x,y> [amount]: drag the item in one bag slot (or that many of its stack) onto another slot, exactly as the inventory window " +
                "does (it stacks, splits, swaps, or is refused as it would be by hand); the bag before and after. Only where cheats are allowed", (a, output, error) =>
                {
                    if (!CheatsAllowed()) { error(CheatRefusal("inv")); return null; }
                    if (a.Length < 4 || !a[1].Equals("move", StringComparison.OrdinalIgnoreCase) || !Slot(a[2], out Vector2i from) || !Slot(a[3], out Vector2i to))
                    { error("inv move <x,y> <x,y> [amount]"); return null; }
                    Inventory inv = Player.m_localPlayer.GetInventory();
                    ItemDrop.ItemData item = inv.GetItemAt(from.x, from.y);
                    if (item == null) { error($"nothing in slot {from.x},{from.y}"); return null; }
                    int amount = a.Length > 4 && int.TryParse(a[4], out int n) ? Mathf.Clamp(n, 1, item.m_stack) : item.m_stack;
                    InventoryGrid grid = InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null;
                    if (grid == null || grid.GetInventory() == null) { error("the inventory window hasn't been opened yet this session: shoot inv, then shoot close, first"); return null; }
                    var before = new JArray(inv.GetAllItems().Select(ItemJson));
                    bool done = grid.DropItem(inv, item, amount, to);
                    output(new JObject { ["moved"] = done, ["from"] = a[2], ["to"] = a[3], ["amount"] = amount, ["before"] = before, ["after"] = new JArray(inv.GetAllItems().Select(ItemJson)) });
                    return null;
                });

            Builtin("craft", "craft <item prefab> [times] [at <station>] | craft upgrade <x,y> [at <station>]: make an item (or upgrade the one in that bag slot) at the " +
                "station the player has open (shoot use <station>), or at the nearest station of that name within 20 m (at piece_workbench: as if standing at it, roof " +
                "or not), as the craft button does: the station's level and the cost are the game's checks. Only where cheats are allowed", (a, output, error) =>
                {
                    if (!CheatsAllowed()) { error(CheatRefusal("craft")); return null; }
                    int atWord = Array.FindIndex(a, w => w.Equals("at", StringComparison.OrdinalIgnoreCase));
                    if (atWord > 0)
                    {
                        string want = atWord + 1 < a.Length ? a[atWord + 1].ToLowerInvariant() : "";
                        a = a.Take(atWord).ToArray();
                        Vector3 me = Player.m_localPlayer.transform.position;
                        CraftingStation st = UnityEngine.Object.FindObjectsByType<CraftingStation>(FindObjectsSortMode.None)
                            .Where(c => c != null && c.transform.root.name.ToLowerInvariant().Contains(want) && Vector3.Distance(c.transform.position, me) <= 20f)
                            .OrderBy(c => Vector3.Distance(c.transform.position, me)).FirstOrDefault();
                        if (st == null) { error($"no station called {want} within 20 m"); return null; }
                        CurrentStation?.SetValue(Player.m_localPlayer, st);
                    }
                    InventoryGui gui = InventoryGui.instance;
                    if (gui == null || CraftRecipe == null || DoCraftingMethod == null) { error("the crafting window isn't there"); return null; }
                    Player p = Player.m_localPlayer;
                    Inventory inv = p.GetInventory();
                    Recipe recipe;
                    ItemDrop.ItemData upgrade = null;
                    int times = 1;
                    if (a.Length > 2 && a[1].Equals("upgrade", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Slot(a[2], out Vector2i at) || (upgrade = inv.GetItemAt(at.x, at.y)) == null) { error($"nothing to upgrade in slot {a[2]}"); return null; }
                        recipe = ObjectDB.instance.GetRecipe(upgrade);
                        if (recipe == null) { error($"{upgrade.m_shared.m_name} has no recipe"); return null; }
                    }
                    else if (a.Length > 1)
                    {
                        CraftingStation here = p.GetCurrentCraftingStation();
                        var options = ObjectDB.instance.m_recipes.Where(r => r != null && r.m_item != null && r.m_item.name.Equals(a[1], StringComparison.OrdinalIgnoreCase)).ToList();
                        recipe = options.FirstOrDefault(r => r.m_craftingStation == null ? here == null : here != null && r.m_craftingStation.m_name == here.m_name) ?? options.FirstOrDefault();
                        if (recipe == null) { error($"no recipe makes {a[1]}"); return null; }
                        if (a.Length > 2 && int.TryParse(a[2], out int t)) times = Mathf.Clamp(t, 1, 50);
                    }
                    else { error("craft <item prefab> [times] | craft upgrade <x,y>"); return null; }

                    var before = inv.GetAllItems().GroupBy(i => i.m_dropPrefab != null ? i.m_dropPrefab.name : i.m_shared.m_name).ToDictionary(g => g.Key, g => g.Sum(i => i.m_stack));
                    int qualityBefore = upgrade?.m_quality ?? 0;
                    for (int i = 0; i < times; i++)
                    {
                        CraftRecipe.SetValue(gui, recipe);
                        CraftUpgrade?.SetValue(gui, upgrade);
                        CraftVariant?.SetValue(gui, upgrade?.m_variant ?? 0);
                        try { DoCraftingMethod.Invoke(gui, new object[] { p }); }
                        catch (TargetInvocationException e) { error("crafting threw: " + (e.InnerException ?? e).Message); break; }
                        finally { CraftRecipe.SetValue(gui, null); CraftUpgrade?.SetValue(gui, null); }
                    }
                    var after = inv.GetAllItems().GroupBy(i => i.m_dropPrefab != null ? i.m_dropPrefab.name : i.m_shared.m_name).ToDictionary(g => g.Key, g => g.Sum(i => i.m_stack));
                    var change = new JObject();
                    foreach (string k in before.Keys.Union(after.Keys))
                    {
                        int d = (after.TryGetValue(k, out int x) ? x : 0) - (before.TryGetValue(k, out int y) ? y : 0);
                        if (d != 0) change[k] = d;
                    }
                    var o = new JObject
                    {
                        ["recipe"] = recipe.name, ["station"] = p.GetCurrentCraftingStation() != null ? p.GetCurrentCraftingStation().name + " (level " + p.GetCurrentCraftingStation().GetLevel() + ")" : null,
                        ["times"] = times, ["bagChange"] = change,
                    };
                    if (upgrade != null)
                    {
                        ItemDrop.ItemData now = inv.GetItemAt(upgrade.m_gridPos.x, upgrade.m_gridPos.y);
                        o["qualityBefore"] = qualityBefore;
                        o["itemNow"] = now != null ? ItemJson(now) : null;
                    }
                    if (change.Count == 0) o["note"] = "nothing changed: not enough materials, the wrong station or level, or no free slot";
                    output(o);
                    return null;
                });
        }

        private static JObject RavenTextJson(Raven.RavenText t, Vector3 me)
        {
            var o = new JObject
            {
                ["raven"] = t.m_munin ? "Munin" : "Hugin", ["key"] = t.m_key, ["topic"] = Localization.instance.Localize(t.m_topic),
                ["text"] = Localization.instance.Localize(t.m_text), ["seen"] = t.m_key.Length > 0 && Player.m_localPlayer.HaveSeenTutorial(t.m_key),
                ["alwaysSpawn"] = t.m_alwaysSpawn, ["fixed"] = t.m_static,
            };
            if (t.m_guidePoint != null) { o["at"] = Vec(t.m_guidePoint.transform.position); o["distance"] = Math.Round(Vector3.Distance(me, t.m_guidePoint.transform.position)); }
            return o;
        }

        private static bool Slot(string s, out Vector2i at)
        {
            at = new Vector2i(0, 0);
            string[] b = (s ?? "").Split(',');
            if (b.Length != 2 || !int.TryParse(b[0], out int x) || !int.TryParse(b[1], out int y)) return false;
            at = new Vector2i(x, y);
            return true;
        }

        private static JObject ItemJson(ItemDrop.ItemData it)
        {
            var o = new JObject
            {
                ["name"] = Localization.instance.Localize(it.m_shared.m_name), ["prefab"] = it.m_dropPrefab != null ? it.m_dropPrefab.name : null,
                ["stack"] = it.m_stack, ["quality"] = it.m_quality, ["slot"] = new JArray(it.m_gridPos.x, it.m_gridPos.y),
            };
            if (it.m_customData != null && it.m_customData.Count > 0) o["data"] = new JObject(it.m_customData.OrderBy(kv => kv.Key).Select(kv => new JProperty(kv.Key, kv.Value)));
            return o;
        }
    }
}
