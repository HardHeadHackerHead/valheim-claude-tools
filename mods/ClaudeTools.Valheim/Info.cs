using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static ClaudeTools.Plugin;

namespace ClaudeTools.Valheim
{
    /// <summary>
    /// What the assistant can find out in Valheim (status, inventory, what is nearby, what the player looks at) and the few things it can do
    /// for the player that change nothing in the world (a message on screen, a map pin). The mods, their settings and the log are Claude
    /// Tools' own (Commands.cs there).
    /// </summary>
    public partial class ValheimPlugin
    {
        private void RegisterBuiltIns()
        {
            RegisterTerrain();   // clear, terrain, fields (Terrain.cs)
            RegisterTesting();   // words, skills, playerdata, effects, itemdata, inv, craft (Testing.cs)
            RegisterSurvey();    // survey (Survey.cs)
            RegisterBuilding();  // colliders, support, comfort (Building.cs)
            RegisterShootCommands();
            RegisterObjectCommands();
            RegisterTcDoctor();     // tcdoctor (TcDoctor.cs)
            RegisterAdmin();        // players, bring, goto, tp, heal, effect, tell, announce, worldtime, save, kick, ban (Admin.cs)
            RegisterClawd();        // clawd (Clawd.cs)

            Builtin("status", "status: world, where the player is, health, stamina, biome, day, time, weather, who is online", (a, output, error) =>
            {
                Player p = Player.m_localPlayer;
                Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
                EnvMan env = EnvMan.instance;
                float frac = env != null ? env.GetDayFraction() : 0f;
                int minutes = Mathf.RoundToInt(frac * 24f * 60f) % (24 * 60);
                output(new JObject
                {
                    ["world"] = ZNet.instance != null ? ZNet.instance.GetWorldName() : "",
                    ["player"] = p.GetPlayerName(),
                    ["position"] = Vec(p.transform.position), ["yaw"] = Math.Round(p.transform.eulerAngles.y, 1),
                    ["camera"] = cam != null ? Vec(cam.position) : null,
                    ["health"] = Math.Round(p.GetHealth(), 1), ["maxHealth"] = Math.Round(p.GetMaxHealth(), 1),
                    ["stamina"] = Math.Round(p.GetStamina(), 1), ["maxStamina"] = Math.Round(p.GetMaxStamina(), 1),
                    ["biome"] = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.transform.position).ToString() : "",
                    ["day"] = env != null ? env.GetDay() : 0,
                    ["time"] = $"{minutes / 60:00}:{minutes % 60:00}",
                    ["weather"] = env != null && env.GetCurrentEnvironment() != null ? env.GetCurrentEnvironment().m_name : "",
                    // the server's list: everyone connected (Player.GetAllPlayers only has those loaded near you)
                    ["online"] = new JArray(OnlinePlayers().Select(x => new JObject { ["name"] = x.Name, ["distance"] = x.Position is Vector3 at ? Math.Round(Vector3.Distance(at, p.transform.position), 1) : (double?)null })),
                });
                return null;
            });

            Builtin("land", "land <x> <z>: the land at a spot anywhere in the world, loaded or not (biome, the world generator's ground height, under water or not): " +
                "check where a teleport would land (a Mountain is freezing without frost resistance) before sending the player there", (a, output, error) =>
            {
                if (WorldGenerator.instance == null || a.Length < 3) { error("land <x> <z>"); return null; }
                float x = F(a, 1, 0f), z = F(a, 2, 0f);
                float ground = WorldGenerator.instance.GetHeight(x, z);
                float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                Heightmap.Biome biome = WorldGenerator.instance.GetBiome(new Vector3(x, ground, z));
                output(new JObject
                {
                    ["at"] = new JArray(x, z), ["biome"] = biome.ToString(), ["generatorGround"] = Math.Round(ground, 1),
                    ["underwater"] = ground < water, ["cold"] = biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth,
                    ["note"] = "generator height: terrain work and buildings there are not counted",
                });
                return null;
            });

            Builtin("inventory","inventory: what the player carries (name, stack, quality, durability, equipped) and totals", (a, output, error) =>
            {
                Player p = Player.m_localPlayer;
                Inventory inv = p.GetInventory();
                var items = new JArray();
                var totals = new Dictionary<string, int>();
                foreach (ItemDrop.ItemData it in inv.GetAllItems())
                {
                    string name = Localization.instance.Localize(it.m_shared.m_name);
                    items.Add(new JObject
                    {
                        ["name"] = name, ["id"] = it.m_shared.m_name, ["stack"] = it.m_stack, ["quality"] = it.m_quality,
                        ["durability"] = it.m_shared.m_useDurability ? Math.Round(100f * it.m_durability / Mathf.Max(1f, it.GetMaxDurability())) + "%" : null,
                        ["equipped"] = it.m_equipped, ["slot"] = new JArray(it.m_gridPos.x, it.m_gridPos.y),
                        ["data"] = it.m_customData != null && it.m_customData.Count > 0 ? new JObject(it.m_customData.OrderBy(kv => kv.Key).Select(kv => new JProperty(kv.Key, kv.Value))) : null,
                    });
                    totals[name] = (totals.TryGetValue(name, out int n) ? n : 0) + it.m_stack;
                }
                output(new JObject
                {
                    ["items"] = items,
                    ["totals"] = new JObject(totals.OrderBy(kv => kv.Key).Select(kv => new JProperty(kv.Key, kv.Value))),
                    ["weight"] = Math.Round(inv.GetTotalWeight(), 1), ["maxWeight"] = Math.Round(p.GetMaxCarryWeight(), 1),
                    ["freeSlots"] = inv.GetEmptySlots(),
                });
                return null;
            });

            Builtin("nearby", "nearby [radius]: creatures and players around the player (health, level), buildings by kind, items on the ground", (a, output, error) =>
            {
                Player p = Player.m_localPlayer;
                Vector3 me = p.transform.position;
                float radius = Mathf.Clamp(F(a, 1, 30f), 2f, 100f);
                var creatures = new JArray();
                foreach (Character c in Character.GetAllCharacters().Where(c => c != null && !c.IsPlayer() && Vector3.Distance(c.transform.position, me) <= radius)
                                                                   .OrderBy(c => Vector3.Distance(c.transform.position, me)).Take(60))
                    creatures.Add(new JObject
                    {
                        ["name"] = Localization.instance.Localize(c.m_name), ["prefab"] = Utils.GetPrefabName(c.gameObject), ["level"] = c.GetLevel(),
                        ["health"] = Math.Round(c.GetHealth()), ["maxHealth"] = Math.Round(c.GetMaxHealth()),
                        ["distance"] = Math.Round(Vector3.Distance(c.transform.position, me), 1), ["tame"] = c.IsTamed(), ["boss"] = c.IsBoss(),
                        ["position"] = Vec(c.transform.position),
                    });
                var players = new JArray(Player.GetAllPlayers().Where(x => x != null && x != p && Vector3.Distance(x.transform.position, me) <= radius)
                    .Select(x => new JObject { ["name"] = x.GetPlayerName(), ["distance"] = Math.Round(Vector3.Distance(x.transform.position, me), 1), ["health"] = Math.Round(x.GetHealth()) }));
                var pieces = new List<Piece>();
                Piece.GetAllPiecesInRadius(me, radius, pieces);
                var byKind = pieces.Where(x => x != null).GroupBy(x => Utils.GetPrefabName(x.gameObject)).OrderByDescending(g => g.Count()).Take(30)
                                   .Select(g => new JProperty(g.Key, g.Count()));
                var items = new Dictionary<string, int>();
                foreach (Collider col in Physics.OverlapSphere(me, radius, LayerMask.GetMask("item"), QueryTriggerInteraction.Collide))
                {
                    ItemDrop drop = col.GetComponentInParent<ItemDrop>();
                    if (drop == null) continue;
                    string n = Localization.instance.Localize(drop.m_itemData.m_shared.m_name);
                    items[n] = (items.TryGetValue(n, out int k) ? k : 0) + drop.m_itemData.m_stack;
                }
                output(new JObject
                {
                    ["radius"] = radius, ["creatures"] = creatures, ["players"] = players,
                    ["buildings"] = pieces.Count, ["buildingsByKind"] = new JObject(byKind),
                    ["itemsOnGround"] = new JObject(items.OrderByDescending(kv => kv.Value).Select(kv => new JProperty(kv.Key, kv.Value))),
                });
                return null;
            });

            // Chests and their QualityOfLife assignments ("Stack to chests" rules, the K menu): looked at, and set, without touching what is in them.
            int rulesKey = "DHack_StackRules".GetStableHashCode();
            Builtin("chests", "chests [radius]: the chests around the player: where, what is in them (by name and by the game's item name), their assignment (QualityOfLife), whose", (a, output, error) =>
            {
                Player p = Player.m_localPlayer;
                Vector3 me = p.transform.position;
                float radius = Mathf.Clamp(F(a, 1, 15f), 2f, 60f);
                var list = new JArray();
                foreach (Container c in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None)
                             .Where(c => c != null && c.GetInventory() != null && c.GetComponentInParent<Piece>() != null && c.GetComponent<TombStone>() == null && Vector3.Distance(c.transform.position, me) <= radius)   // (one not set up yet has no inventory)
                             .OrderBy(c => Vector3.Distance(c.transform.position, me)))
                {
                    ZNetView v = c.GetComponent<ZNetView>();
                    ZDO z = v != null && v.IsValid() ? v.GetZDO() : null;
                    var contents = c.GetInventory().GetAllItems().GroupBy(i => i.m_shared.m_name)
                        .Select(g => $"{Localization.instance.Localize(g.Key)} x{g.Sum(i => i.m_stack)} ({g.Key})").ToArray();
                    list.Add(new JObject
                    {
                        ["chest"] = Utils.GetPrefabName(c.gameObject), ["position"] = Vec(c.transform.position), ["distance"] = Math.Round(Vector3.Distance(c.transform.position, me), 1),
                        ["slots"] = $"{c.GetInventory().NrOfItems()}/{c.GetInventory().GetWidth() * c.GetInventory().GetHeight()}",
                        ["rule"] = z?.GetString(rulesKey, "") ?? "", ["companions"] = (z?.GetLong("dhc_home", 0L) ?? 0L) != 0L ? z.GetString("dhc_homename", "a companion's") : "",
                        ["contents"] = new JArray(contents),
                    });
                }
                output(new JObject { ["radius"] = radius, ["chests"] = list });
                return null;
            });

            Builtin("chestrule", "chestrule <x> <y> <z> <rule>: set the QualityOfLife assignment of the chest at x,y,z (\"C=Food,Tools|I=$item_wood\"; \"-\" clears it). Only the assignment: nothing in the chest is touched", (a, output, error) =>
            {
                if (a.Length < 5) { error("chestrule <x> <y> <z> <rule>   (rule: C=<categories>|I=<item names>, or - to clear)"); return null; }
                var at = new Vector3(F(a, 1, 0f), F(a, 2, 0f), F(a, 3, 0f));
                string rule = string.Join(" ", a.Skip(4)).Trim();
                if (rule == "-") rule = "";
                Container c = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None)
                    .Where(k => k != null && k.GetComponentInParent<Piece>() != null && Vector3.Distance(k.transform.position, at) < 0.5f)
                    .OrderBy(k => Vector3.Distance(k.transform.position, at)).FirstOrDefault(); // (exactly that one: chests stacked on each other share x and z)
                if (c == null) { error($"no chest at {at}"); return null; }
                if (c.IsInUse()) { error("someone has that chest open"); return null; }
                ZNetView v = c.GetComponent<ZNetView>();
                if (v == null || !v.IsValid()) { error("that chest is not ready"); return null; }
                if (!v.IsOwner()) v.ClaimOwnership();
                v.GetZDO().Set(rulesKey, rule);
                output(new JObject { ["chest"] = Utils.GetPrefabName(c.gameObject), ["position"] = Vec(c.transform.position), ["rule"] = rule });
                return null;
            });

            Builtin("looking", "looking: what the player's crosshair is on (name, kind, distance, health)", (a, output, error) =>
            {
                if (GameCamera.instance == null) { error("no camera"); return null; }
                Transform cam = GameCamera.instance.transform;
                Player p = Player.m_localPlayer;
                var hits = Physics.RaycastAll(cam.position, cam.forward, 60f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider.GetComponentInParent<Player>() == p) continue;   // the player's own body
                    GameObject root = hit.collider.GetComponentInParent<ZNetView>()?.gameObject ?? hit.collider.transform.root.gameObject;
                    var info = new JObject
                    {
                        ["object"] = Utils.GetPrefabName(root), ["distance"] = Math.Round(Vector3.Distance(p.transform.position, hit.point), 1),
                        ["point"] = Vec(hit.point), ["layer"] = LayerMask.LayerToName(hit.collider.gameObject.layer),
                    };
                    Hoverable hover = hit.collider.GetComponentInParent<Hoverable>();
                    if (hover != null) info["name"] = Localization.instance.Localize(hover.GetHoverName());
                    Piece piece = root.GetComponent<Piece>();
                    WearNTear wear = root.GetComponent<WearNTear>();
                    if (piece != null) { info["kind"] = "building piece"; info["name"] = Localization.instance.Localize(piece.m_name); }
                    if (wear != null) info["condition"] = Math.Round(100f * wear.GetHealthPercentage()) + "%";
                    Character ch = root.GetComponent<Character>();
                    if (ch != null) { info["kind"] = "creature"; info["name"] = Localization.instance.Localize(ch.m_name); info["health"] = Math.Round(ch.GetHealth()); }
                    if (root.GetComponent<ItemDrop>() != null) info["kind"] = "item";
                    if (hit.collider.gameObject.layer == LayerMask.NameToLayer("terrain")) info["kind"] = "ground";
                    output(info);
                    return null;
                }
                output(new JObject { ["object"] = "nothing within 60 m (sky)" });
                return null;
            });

            Builtin("daytime", "daytime <hh:mm | 0-1 | off>: hold this game at that time of day (a picture in daylight), as the game's console tod does; off: back to the " +
                "world's own time. Only on this computer, and only where cheats are allowed", (a, output, error) =>
            {
                if (EnvMan.instance == null) { error("no world loaded"); return null; }
                if (!CheatsAllowed()) { error(CheatRefusal("daytime")); return null; }
                if (a.Length < 2) { output(new JObject { ["held"] = EnvMan.instance.m_debugTimeOfDay, ["at"] = EnvMan.instance.m_debugTimeOfDay ? Clock(EnvMan.instance.m_debugTime) : null }); return null; }
                string w = a[1].ToLowerInvariant();
                if (w == "off" || w == "-1") { EnvMan.instance.m_debugTimeOfDay = false; output(new JObject { ["held"] = false }); return null; }
                float day;
                string[] hm = w.Split(':');
                if (hm.Length == 2 && int.TryParse(hm[0], out int h) && int.TryParse(hm[1], out int m)) day = Mathf.Clamp01((h * 60 + m) / 1440f);
                else if (float.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) day = Mathf.Clamp01(f);
                else { error("daytime <hh:mm | 0-1 | off>, e.g. daytime 12:00"); return null; }
                EnvMan.instance.m_debugTimeOfDay = true;
                EnvMan.instance.m_debugTime = day;
                output(new JObject { ["held"] = true, ["at"] = Clock(day) });
                return null;
            });

            Builtin("play", "play [character] [world]: from the main menu, start that character in that single-player world (names as the menu shows them; " +
                "left out: the ones the menu has selected) and wait until the player is in it (up to 3 minutes). The same as Start Game, the character, " +
                "Start, the world, Start: for getting back into a test world after a restart", (a, output, error) => Play(a, output, error));

            Builtin("logout", "logout: save and go back to the main menu, as the pause menu's Log Out does, and wait until it is there", (a, output, error) => LogOut(output, error));

            Builtin("quit", "quit: save and close the game, as the pause menu's Quit does (in a world, the game saves the character and the world as it " +
                "closes). Ask the player first; start it again with Steam (steam://rungameid/892970), then play", (a, output, error) =>
            {
                output(new JObject { ["quitting"] = true, ["saves"] = Player.m_localPlayer != null });
                StartCoroutine(QuitSoon());   // (after this request's answer is written)
                return null;
            });

            Builtin("console", "console <game console command>: run one of the game's own console commands (raiseskill all 100, setkey defeated_eikthyr, " +
                "setpower GP_Eikthyr, god, spawn Boar 3, tod 0.5...) and get back what it printed. A cheat command only where cheats are allowed (single " +
                "player, the host): devcommands is switched on for that one command and back off. The game also gates cheat commands per character: the " +
                "first time, `console confirmcheats` marks the character as cheated for good (no achievements): ask the player first", (a, output, error) =>
            {
                Terminal term = global::Console.instance;
                if (term == null) { error("the game's console isn't there (no world loaded?)"); return null; }
                string line = string.Join(" ", a.Skip(1)).Trim();
                if (line.Length == 0) { error("console <command> [arguments], e.g. console raiseskill all 100 (the game's own `help` lists them: console help)"); return null; }
                string name = line.Split(' ')[0].ToLowerInvariant();
                var commands = AccessTools.Field(typeof(Terminal), "commands")?.GetValue(null) as IDictionary<string, Terminal.ConsoleCommand>;
                if (commands == null || !commands.TryGetValue(name, out Terminal.ConsoleCommand cmd)) { error($"'{name}' isn't one of the game's console commands (console help lists them)"); return null; }
                if (cmd.IsCheat && !CheatsAllowed()) { error(CheatRefusal("console " + name)); return null; }
                if (cmd.IsCheat && name != "confirmcheats" && !Achievements.IsCheatedAtAll())
                {
                    error($"the game takes cheat commands like {name} only from a character marked as cheated: `console confirmcheats` does it for good " +
                          "(this character then earns no achievements). Ask the player first");
                    return null;
                }
                // what it prints goes to the console's own list: this command's lines are taken from a cleared list, and the player's put back
                var buffer = AccessTools.Field(typeof(Terminal), "m_chatBuffer")?.GetValue(term) as List<string>;
                List<string> before = buffer != null ? new List<string>(buffer) : null;
                buffer?.Clear();
                bool cheat = Terminal.m_cheat;
                try
                {
                    if (cmd.IsCheat) Terminal.m_cheat = true;
                    term.TryRunCommand(line, silentFail: false, skipAllowedCheck: true);
                }
                catch (Exception e) { error($"{name} threw: {e.GetBaseException().Message}"); }
                finally
                {
                    if (cmd.IsCheat) Terminal.m_cheat = cheat;   // (devcommands itself isn't a cheat command: it keeps what it set)
                    if (buffer != null)
                    {
                        var printed = new List<string>(buffer);
                        buffer.Clear();
                        buffer.AddRange(before);
                        buffer.AddRange(printed);
                        while (buffer.Count > 300) buffer.RemoveAt(0);
                        output(new JObject
                        {
                            ["command"] = line,
                            ["printed"] = new JArray(printed.Select(s => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "")))
                        });
                    }
                }
                return null;
            });

            Builtin("language", "language [name | dump]: the game's language now and every one it has; dump saves all its words in the language shown now to BepInEx/claude/game-words/<Language>.json (to find the game's own words); with a name (as listed: German, Portuguese_Brazilian...) switch to it, " +
                "as the player's Settings would (for checking a mod's translations: switch back after)", (a, output, error) =>
            {
                Localization loc = Localization.instance;
                if (loc == null) { error("the game's translations aren't loaded yet"); return null; }
                List<string> all = loc.GetLanguages();
                string was = loc.GetSelectedLanguage();
                if (a.Length > 1 && a[1].Equals("dump", StringComparison.OrdinalIgnoreCase))
                {
                    // every word the game has in the language it shows now (key without "$" → text), for finding the game's own words
                    var words = AccessTools.Field(typeof(Localization), "m_translations")?.GetValue(loc) as Dictionary<string, string>;
                    if (words == null) { error("couldn't read the game's translations"); return null; }
                    string dir = Path.Combine(Paths.BepInExRootPath, "claude", "game-words");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, was + ".json");
                    var json = new JObject();
                    foreach (var kv in words.OrderBy(k => k.Key, StringComparer.Ordinal)) json[kv.Key] = kv.Value;
                    File.WriteAllText(file, json.ToString());
                    output(new JObject { ["language"] = was, ["words"] = words.Count, ["file"] = file });
                    return null;
                }
                if (a.Length > 1)
                {
                    string want = all.FirstOrDefault(l => l.Equals(a[1], StringComparison.OrdinalIgnoreCase));
                    if (want == null) { error($"no language \"{a[1]}\": the game has {string.Join(", ", all)}"); return null; }
                    loc.SetLanguage(want);
                }
                output(new JObject { ["language"] = loc.GetSelectedLanguage(), ["was"] = was, ["languages"] = new JArray(all) });
                return null;
            });

            Builtin("give", "give <item prefab> [amount]: put an item in the player's inventory (for trying out a mod's new item)", (a, output, error) =>
            {
                if (a.Length < 2) { error("give <item prefab> [amount]"); return null; }
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(a[1]) : null;
                if (prefab == null) { error($"no item called '{a[1]}'"); return null; }
                int amount = Mathf.Clamp(I(a, 2, 1), 1, 100);
                ItemDrop.ItemData added = Player.m_localPlayer.GetInventory().AddItem(prefab.name, amount, 1, 0, 0L, "", false);
                if (added == null) { error("the inventory is full"); return null; }
                added.m_worldLevel = (byte)Game.m_worldLevel;   // as an item picked up in this world has, or recipes would not count it
                output(new JObject { ["gave"] = prefab.name, ["amount"] = amount });
                return null;
            });

            Builtin("grow", "grow <seconds> [radius]: make the plants (cultivated seedlings) near the player <seconds> older, so they grow without the wait", (a, output, error) =>
            {
                if (a.Length < 2) { error("grow <seconds> [radius]"); return null; }
                float seconds = F(a, 1, 60f), radius = Mathf.Clamp(F(a, 2, 20f), 2f, 60f);
                Vector3 me = Player.m_localPlayer.transform.position;
                int key = "plantTime".GetStableHashCode();
                var done = new JArray();
                foreach (Plant plant in UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None).Where(x => x != null && Vector3.Distance(x.transform.position, me) <= radius))
                {
                    ZNetView view = plant.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid()) continue;
                    if (!view.IsOwner()) view.ClaimOwnership();
                    ZDO zdo = view.GetZDO();
                    long was = zdo.GetLong(key, 0L);
                    if (was == 0L) continue;
                    if (seconds == 0f)   // "grow 0": make it grow right now and say what came of it
                    {
                        try { GameObject made = plant.Grow(); done.Add(new JObject { ["plant"] = Utils.GetPrefabName(plant.gameObject), ["grewInto"] = made != null ? Utils.GetPrefabName(made) : "(nothing: " + plant.GetStatus() + ")" }); }
                        catch (Exception e) { done.Add(new JObject { ["plant"] = Utils.GetPrefabName(plant.gameObject), ["error"] = e.GetType().Name + ": " + e.Message }); }
                        continue;
                    }
                    zdo.Set(key, was - (long)(seconds * TimeSpan.TicksPerSecond));
                    done.Add(new JObject { ["plant"] = Utils.GetPrefabName(plant.gameObject), ["position"] = Vec(plant.transform.position), ["age"] = Math.Round((ZNet.instance.GetTime().Ticks - (was - (long)(seconds * TimeSpan.TicksPerSecond))) / (double)TimeSpan.TicksPerSecond),
                        ["status"] = plant.GetStatus().ToString(), ["growsInto"] = new JArray(plant.m_grownPrefabs.Select(g => g != null ? g.name : "NOTHING")), ["owner"] = view.IsOwner(), ["growTime"] = plant.m_growTime });
                }
                output(new JObject { ["aged"] = done, ["note"] = "plants check their growth every ten seconds or so" });
                return null;
            });

            Builtin("fixlevels", "fixlevels <prefix>: give every item in the bag whose prefab name starts with <prefix> this world's level (for items added by an older give, which recipes would not count)", (a, output, error) =>
            {
                if (a.Length < 2) { error("fixlevels <prefix>"); return null; }
                int n = 0;
                foreach (ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems())
                    if (item.m_dropPrefab != null && item.m_dropPrefab.name.StartsWith(a[1]) && item.m_worldLevel != (byte)Game.m_worldLevel) { item.m_worldLevel = (byte)Game.m_worldLevel; n++; }
                output(new JObject { ["fixed"] = n, ["worldLevel"] = Game.m_worldLevel });
                return null;
            });

            Builtin("drop", "drop <item prefab> [amount]: drop an item from the player's bag on the ground in front of them, as dragging it out of the bag does", (a, output, error) =>
            {
                if (a.Length < 2) { error("drop <item prefab> [amount]"); return null; }
                Player p = Player.m_localPlayer;
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(a[1]) : null;
                if (prefab == null) { error($"no item called '{a[1]}'"); return null; }
                string itemName = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
                ItemDrop.ItemData item = p.GetInventory().GetAllItems().FirstOrDefault(i => i.m_shared.m_name == itemName);
                if (item == null) { error($"the player has no '{a[1]}'"); return null; }
                bool dropped = p.DropItem(p.GetInventory(), item, Mathf.Clamp(I(a, 2, 1), 1, item.m_stack));
                output(new JObject { ["dropped"] = a[1], ["ok"] = dropped });
                return null;
            });

            Builtin("pickup", "pickup <item prefab>: pick up the nearest dropped item of that kind within 4 m, as walking up to it and pressing use does", (a, output, error) =>
            {
                if (a.Length < 2) { error("pickup <item prefab>"); return null; }
                Player p = Player.m_localPlayer;
                ItemDrop nearest = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
                    .Where(d => d != null && d.gameObject.name.StartsWith(a[1]) && Vector3.Distance(d.transform.position, p.transform.position) < 4f)
                    .OrderBy(d => Vector3.Distance(d.transform.position, p.transform.position)).FirstOrDefault();
                if (nearest == null) { error($"no dropped '{a[1]}' within 4 m"); return null; }
                bool ok = p.Pickup(nearest.gameObject, false, false);
                output(new JObject { ["pickedUp"] = a[1], ["ok"] = ok });
                return null;
            });

            Builtin("use", "use <item prefab>: use an item from the player's inventory, as a double-click does (eat, drink, light)", (a, output, error) =>
            {
                if (a.Length < 2) { error("use <item prefab>"); return null; }
                Player p = Player.m_localPlayer;
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(a[1]) : null;
                if (prefab == null) { error($"no item called '{a[1]}'"); return null; }
                string itemName = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
                Func<IEnumerable<ItemDrop.ItemData>> mine = () => p.GetInventory().GetAllItems().Where(i => i.m_shared.m_name == itemName);
                ItemDrop.ItemData item = mine().FirstOrDefault();
                if (item == null) { error($"the player has no '{a[1]}'"); return null; }
                int before = mine().Sum(i => i.m_stack);
                p.UseItem(p.GetInventory(), item, false);
                output(new JObject { ["used"] = a[1], ["countBefore"] = before, ["countAfter"] = mine().Sum(i => i.m_stack) });
                return null;
            });

            Builtin("say","say <text>: show a message in the middle of the player's screen", (a, output, error) =>
            {
                string text = Rest(a, 1);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, text.Length > 200 ? text.Substring(0, 200) : text);
                output(new JObject { ["said"] = text });
                return null;
            });

            Builtin("pin", "pin <place|x z> <text>: put a pin with a label on the player's map (place: here, look, or a name)", (a, output, error) =>
            {
                Vector3 pos; string label;
                if (a.Length >= 3 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                { pos = new Vector3(x, 0f, z); label = Rest(a, 3); }
                else
                {
                    if (!Frame(a.Length > 1 ? a[1] : "here", out pos, out float _, out string err)) { error(err); return null; }
                    label = Rest(a, 2);
                }
                if (!AddPin(pos, label.Length > 0 ? label : "Claude")) { error("could not add a pin (the map is not ready?)"); return null; }
                output(new JObject { ["pin"] = label, ["at"] = Vec(pos) });
                return null;
            });
        }

        private static string Clock(float day) { int min = Mathf.RoundToInt(day * 1440f) % 1440; return $"{min / 60:00}:{min % 60:00}"; }

        // ---- getting in and out of a world (play, logout, quit): the main menu's and the pause menu's own steps ----

        private IEnumerator Play(string[] a, Action<JObject> output, Action<string> error)
        {
            if (Player.m_localPlayer != null) { error("already in a world (logout first)"); yield break; }
            FejdStartup menu = FejdStartup.instance;
            if (menu == null) { error("not at the main menu yet (wait a moment after the game starts, then play again)"); yield break; }
            string wantCharacter = a.Length > 1 ? a[1].Replace('_', ' ') : null, wantWorld = a.Length > 2 ? a[2].Replace('_', ' ') : null;

            List<PlayerProfile> profiles = SaveSystem.GetAllPlayerProfiles();
            PlayerProfile profile = wantCharacter == null ? null
                : profiles.FirstOrDefault(p => string.Equals(p.GetName(), wantCharacter, StringComparison.OrdinalIgnoreCase) || string.Equals(p.GetFilename(), wantCharacter, StringComparison.OrdinalIgnoreCase));
            if (wantCharacter != null && profile == null) { error($"no character called {wantCharacter} (there are: {string.Join(", ", profiles.Select(p => p.GetName()).ToArray())})"); yield break; }

            menu.OnStartGame();   // the character screen
            yield return null;
            if (profile != null) AccessTools.Method(typeof(FejdStartup), "SetSelectedProfile")?.Invoke(menu, new object[] { profile.GetFilename() });
            yield return null;
            menu.OnCharacterStart();   // the world screen
            yield return null;
            var worlds = AccessTools.Field(typeof(FejdStartup), "m_worlds")?.GetValue(menu) as List<World>;
            if (worlds == null || worlds.Count == 0) { error("this game has no worlds (make one in the menu)"); yield break; }
            if (wantWorld != null)
            {
                int i = worlds.FindIndex(w => string.Equals(w.m_name, wantWorld, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { error($"no world called {wantWorld} (there are: {string.Join(", ", worlds.Select(w => w.m_name).ToArray())})"); yield break; }
                AccessTools.Method(typeof(FejdStartup), "SetSelectedWorld")?.Invoke(menu, new object[] { i, true });
            }
            if (menu.m_openServerToggle != null) menu.m_openServerToggle.isOn = false;   // single player, not hosting
            yield return null;
            var world = AccessTools.Field(typeof(FejdStartup), "m_world")?.GetValue(menu) as World;
            string character = profile?.GetName() ?? Game.instance?.GetPlayerProfile()?.GetName();
            menu.OnWorldStart();

            float started = Time.unscaledTime;
            while (Player.m_localPlayer == null && Time.unscaledTime - started < 180f) yield return null;
            if (Player.m_localPlayer == null) { error($"the world {world?.m_name} didn't load within 3 minutes (a question on screen? screen list shows it)"); yield break; }
            output(new JObject
            {
                ["character"] = Player.m_localPlayer.GetPlayerName(),
                ["world"] = world?.m_name,
                ["seconds"] = Math.Round(Time.unscaledTime - started, 1)
            });
        }

        private IEnumerator LogOut(Action<JObject> output, Action<string> error)
        {
            if (Game.instance == null || Player.m_localPlayer == null) { error("not in a world"); yield break; }
            Game.instance.Logout(save: true, changeToStartScene: true);
            float started = Time.unscaledTime;
            while (FejdStartup.instance == null && Time.unscaledTime - started < 60f) yield return null;
            if (FejdStartup.instance == null) { error("the main menu didn't come back within a minute"); yield break; }
            output(new JObject { ["atMainMenu"] = true, ["seconds"] = Math.Round(Time.unscaledTime - started, 1) });
        }

        private static IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();   // (in a world, Game.OnApplicationQuit saves the character and the world)
        }

        /// <summary>A pin on the map, through whichever AddPin the game has (its parameters change between versions).</summary>
        private static bool AddPin(Vector3 pos, string label)
        {
            if (Minimap.instance == null) return false;
            MethodInfo add = typeof(Minimap).GetMethods().FirstOrDefault(m => m.Name == "AddPin" && m.GetParameters().Length >= 3
                && m.GetParameters()[0].ParameterType == typeof(Vector3) && m.GetParameters()[2].ParameterType == typeof(string));
            if (add == null) return false;
            var args = add.GetParameters().Select((p, i) =>
                i == 0 ? (object)pos :
                i == 1 ? Minimap.PinType.Icon3 :
                i == 2 ? label :
                p.Name == "save" ? true :
                p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            add.Invoke(Minimap.instance, args);
            return true;
        }
    }
}
