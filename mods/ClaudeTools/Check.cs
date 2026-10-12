using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

namespace ClaudeTools
{
    /// <summary>
    /// The pre-release check: reads a mod's DLL (never runs it) and points out the mistakes that have cost players items and buildings or
    /// broken other mods, each with why it matters and how to fix it. Every rule comes from a real bug (see docs/modding-pitfalls.md in the valheim-claude-tools
    /// repo). It finds candidates, not certainties: read the code at "where" before changing anything.
    /// </summary>
    internal static class Check
    {
        public class Others
        {
            public Func<string, List<string>> SameMethod = t => new List<string>();   // other mods' risky patches on a game method
            public Func<string, List<string>> SameSystem = s => new List<string>();   // other mods' risky patches in a game system
            public Func<string, string> SystemOf = t => null;
            public Func<string, List<string>> SameAssembly = a => new List<string>(); // popular mods whose DLL has this assembly name
        }

        // The game's own default keys (ZInput.AddButton): a mod's default on one of these also triggers the game's action.
        private static readonly Dictionary<int, string> GameKeys = new Dictionary<int, string>
        {
            [118] = "V (auto-pickup)", [120] = "X (sit)", [99] = "C (walk)", [113] = "Q (auto-run)", [103] = "G (radial menu)", [102] = "F (forsaken power)",
            [114] = "R (hide weapons)", [116] = "T (emotes)", [101] = "E (use)", [9] = "Tab (inventory)", [109] = "M (map)",
            [49] = "1 (hotbar)", [50] = "2 (hotbar)", [51] = "3 (hotbar)", [52] = "4 (hotbar)", [53] = "5 (hotbar)", [54] = "6 (hotbar)", [55] = "7 (hotbar)", [56] = "8 (hotbar)",
        };

        public static JObject Run(string dll, IEnumerable<string> searchDirs, Others others)
        {
            var findings = new List<JObject>();
            void Add(string level, string rule, string where, string what, string why, string fix) =>
                findings.Add(new JObject { ["level"] = level, ["rule"] = rule, ["where"] = where, ["what"] = what, ["why"] = why, ["fix"] = fix });

            JObject scan = Scan.Dll(dll, searchDirs);
            var patches = (scan["patches"] as JArray ?? new JArray()).Cast<JObject>().ToList();
            var plugins = (scan["plugins"] as JArray ?? new JArray()).Cast<JObject>().ToList();
            bool usesJotunn = plugins.Any(p => (p["dependencies"] as JArray)?.Any(d => ((string)d["guid"] ?? "").IndexOf("jotunn", StringComparison.OrdinalIgnoreCase) >= 0) ?? false);

            // ---- from its patches ----
            foreach (JObject p in patches)
            {
                string target = (string)p["target"], method = (string)p["method"], kind = (string)p["kind"];
                List<string> others1 = others.SameMethod(target);
                string alsoHere = others1.Count > 0 ? " Popular mods that also change it: " + string.Join("; ", others1.Take(6).ToArray()) + (others1.Count > 6 ? $" (+{others1.Count - 6} more)." : ".") : "";
                if ((string)p["skips"] == "always")
                    Add(others1.Count > 0 ? "problem" : "warning", "takes-over-method", method,
                        $"Its prefix on {target} always returns false: the game's own {target} never runs, and other mods' changes inside it are lost." + alsoHere,
                        "Skipping the game's method is fine for your own objects only. Skipping it for everything takes the method away from every other mod.",
                        "Return false only when the call is about your mod's own thing (check the instance first), or use a postfix. If you must take it over, " +
                        "say so in the README and detect the mods that clash ([BepInIncompatibility] or a check of Chainloader.PluginInfos).");
                else if ((kind == "transpiler" || kind == "il-hook") && others1.Count > 0)
                    Add("warning", "rewrites-shared-method", method, $"It rewrites {target}'s code, which other mods also change." + alsoHere,
                        "Two mods rewriting one method each expect the code they saw; the second one's pattern may not match after the first.",
                        "Insert code rather than removing the game's, match patterns loosely, and log clearly when the pattern isn't found.");
                if (kind == "hook")
                    Add("note", "monomod-hook", method, $"A MonoMod hook (On.{target}): it can skip the game's method like a prefix.",
                        "Harmony's patch list and most compatibility tools don't see MonoMod hooks.", "Prefer Harmony patches, or make sure the hook always calls orig().");
            }

            // ---- from its code ----
            var resolver = new DefaultAssemblyResolver();
            foreach (string d in searchDirs.Concat(new[] { Path.GetDirectoryName(dll) }).Where(Directory.Exists).Distinct()) resolver.AddSearchDirectory(d);
            bool readsUpgrader = false, unpatches = false, addsPrefabs = false, freesKeys = false, disposesWatcher = false;
            bool usesPiece = false, syncs = false, networked = false, binds = false, addsToObjectDB = false;
            bool objectDbOutsideTemplate = false, repointsItems = false;   // (items added only through the ItemReload template, and its Repoint called)
            string declaredReload = null, managers = null;
            string watcherWhere = null;
            string resourcesWhere = null, prefabsWhere = null, heightWhere = null;
            var keyFindings = new List<JObject>();
            var customKeys = new Dictionary<string, string>();
            try
            {
                using (var stream = new MemoryStream(File.ReadAllBytes(dll)))
                using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    List<string> sameName = others.SameAssembly(asm.Name.Name);
                    if (sameName.Count > 0)
                        Add("warning", "assembly-name", Path.GetFileName(dll), $"Its assembly name \"{asm.Name.Name}\" is also the name of {string.Join(", ", sameName.Take(4).ToArray())}.",
                            "Mods are told apart by GUID, but .NET also knows a DLL by its assembly name: with both installed, the game can hand one mod's " +
                            "assembly to code looking for the other's (another mod's lookup, a reference by name), and a player copying DLLs into one folder overwrites one.",
                            "Give the assembly a name that is yours (<AssemblyName>YourName.ModName</AssemblyName>); for a mod already out, weigh it against " +
                            "the old file left behind by an update (a mod manager replaces files by name).");
                    // the starter project's names, never changed: its GUID (com.yourname.examplemod), assembly and namespace (ExampleMod), YourName.Shared
                    var placeholders = new List<string>();
                    foreach (JObject pl in plugins)
                        if (IsPlaceholder((string)pl["guid"])) placeholders.Add($"the GUID \"{pl["guid"]}\"");
                    if (IsPlaceholder(asm.Name.Name)) placeholders.Add($"the assembly name \"{asm.Name.Name}\"");
                    foreach (string ns in asm.MainModule.Types.Select(t => t.Namespace).Where(n => !string.IsNullOrEmpty(n)).Select(n => n.Split('.')[0]).Distinct().Where(IsPlaceholder).Take(3))
                        placeholders.Add($"the namespace \"{ns}\"");
                    if (placeholders.Count > 0)
                        Add("problem", "placeholder-name", Path.GetFileName(dll), "It still has the starter project's names: " + string.Join(", ", placeholders.ToArray()) + ".",
                            "The GUID tells mods apart for ever (saves, settings, the mod manager, Thunderstore): every mod made from the template that kept it clashes with the others, " +
                            "and a new GUID later makes players' installs see a different mod.",
                            "Rename before the first release: the GUID (com.<yourname>.<modname>), the Name, the folder and csproj, <AssemblyName>, and the namespace (also YourName.Shared).");
                    // how it may be reloaded: what the author says ([assembly: AssemblyMetadata("ClaudeTools.Reload", "world|menu|restart")]),
                    // and libraries merged in that patch with their own Harmony ids (blaxxun-boop's managers), which unloading can't undo
                    foreach (CustomAttribute a in asm.CustomAttributes.Where(a => a.AttributeType.Name == "AssemblyMetadataAttribute" && a.ConstructorArguments.Count == 2))
                        if ((string)a.ConstructorArguments[0].Value == "ClaudeTools.Reload") declaredReload = ((string)a.ConstructorArguments[1].Value ?? "").Trim().ToLowerInvariant();
                    managers = asm.MainModule.Types.Select(t => t.Namespace).FirstOrDefault(n => ManagerNamespaces.Contains(n));
                    // test commands that act as a cheat: a class that registers commands (Claude Tools' RegisterCommand by reflection, or the game's
                    // Terminal.ConsoleCommand) and whose code gives items, teleports, hurts or removes things, with no check of the game's cheat rule
                    foreach (TypeDefinition type in asm.MainModule.Types.Where(t => !t.Name.StartsWith("<")))
                    {
                        var methods = All(type).SelectMany(t => t.Methods).Where(x => x.HasBody).ToList();
                        bool registers = methods.Any(x => x.Body.Instructions.Any(i => (i.OpCode == OpCodes.Ldstr && (string)i.Operand == "RegisterCommand")
                            || (i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c && c.DeclaringType.Name == "ConsoleCommand")));
                        if (!registers) continue;
                        bool gated = methods.Any(x => x.Body.Instructions.Any(i => i.Operand is MethodReference c &&
                            (c.Name == "IsCheatsEnabled" || c.Name == "CheatsAllowed" || (c.Name == "IsServer" && c.DeclaringType.Name == "ZNet")
                             || (c.Name == "CheckAccess" && c.DeclaringType.Name == "PrivateArea"))));   // (checks wards: acts as a player may)
                        if (gated) continue;
                        string does = methods.SelectMany(x => x.Body.Instructions).Select(i => i.Operand as MethodReference)
                            .Where(c => c != null).Select(c => c.DeclaringType.Name + "." + c.Name).FirstOrDefault(CheatCalls.ContainsKey);
                        if (does != null)
                            Add("warning", "command-not-cheat-gated", Scan.Name(type), $"Its commands (in {Scan.Name(type)}) can {CheatCalls[does]} ({does}), with no check of the game's cheat rule.",
                                "On a server, any player who can type the command (the game's console, or Claude Tools' claude <command>) can use it: a cheat.",
                                "Allow such commands only in single player, for the host or with devcommands: ZNet.instance == null || ZNet.instance.IsServer() || " +
                                "Console.instance.IsCheatsEnabled(); or leave test commands out of the release build.");
                    }

                    Dictionary<MethodDefinition, string> perFrame = PerFrame(asm.MainModule, patches);
                    var searchSeen = new HashSet<string>();

                    foreach (TypeDefinition type in asm.MainModule.Types.SelectMany(All))
                    foreach (MethodDefinition m in type.Methods.Where(x => x.HasBody))
                    {
                        string where = Scan.Name(type) + "." + m.Name;
                        IList<Instruction> code = m.Body.Instructions;
                        // counting a recipe's cost here: reads m_resources and the amounts (GetAmount, m_amount, m_amountPerLevel)
                        bool costs = code.Any(x => x.Operand is FieldReference rf && rf.DeclaringType.Name == "Recipe" && rf.Name == "m_resources")
                                  && code.Any(x => (x.Operand is MethodReference mr && mr.Name == "GetAmount" && mr.DeclaringType.Name == "Requirement")
                                                || (x.OpCode == OpCodes.Ldfld && x.Operand is FieldReference af && af.DeclaringType.Name == "Requirement" && (af.Name == "m_amount" || af.Name == "m_amountPerLevel")));
                        if (costs) resourcesWhere = resourcesWhere ?? where;
                        for (int i = 0; i < code.Count; i++)
                        {
                            Instruction ins = code[i];
                            if (ins.Operand is FieldReference f)
                            {
                                string owner = f.DeclaringType.Name;
                                if (f.Name == "m_upgraderResource") readsUpgrader = true;
                                if (owner == "ZNetScene" && (f.Name == "m_prefabs" || f.Name == "m_namedPrefabs") && ins.OpCode == OpCodes.Ldfld && AddsTo(code, i))
                                    { addsPrefabs = true; prefabsWhere = prefabsWhere ?? where; }
                                if (owner == "ObjectDB" && (f.Name == "m_items" || f.Name == "m_recipes" || f.Name == "m_StatusEffects") && ins.OpCode == OpCodes.Ldfld && AddsTo(code, i))
                                {
                                    addsToObjectDB = true;
                                    if (f.Name != "m_items" || !InTemplate(type, "ItemReload")) objectDbOutsideTemplate = true;
                                }
                                if (f.Name == "m_customData" && ins.OpCode == OpCodes.Stfld && !NullGuarded(code, i))
                                    Add("warning", "replaces-custom-data", where, $"It replaces a {owner}.m_customData dictionary (not only when it is missing).",
                                        "m_customData is shared: every mod keeps its own keys in it. Replacing the dictionary of an item or player that has one wipes " +
                                        "theirs (backpack contents, enchantments, ...). Fine on an item you just made yourself.",
                                        "Only add, change and remove your own keys: m_customData[\"YourMod.key\"] = value (create the dictionary only if it is null).");
                                if (f.Name == "m_customData" && ins.OpCode == OpCodes.Ldfld && i + 1 < code.Count && code[i + 1].OpCode == OpCodes.Ldstr)
                                    if (!customKeys.ContainsKey((string)code[i + 1].Operand)) customKeys[(string)code[i + 1].Operand] = where;
                                if (owner == "Inventory" && f.Name == "m_height" && ins.OpCode == OpCodes.Stfld) heightWhere = heightWhere ?? where;
                                if (f.Name == "m_value" && f.DeclaringType.Name == "SharedData" && ins.OpCode == OpCodes.Stfld && !(i > 0 && Int(code[i - 1]) == 0))
                                    Add("warning", "sellable-item", where, "It gives an item a trader price (m_value).",
                                        "Any item with m_value above 0 sells for coins at Haldor (and any StoreGui): an item that is cheap to make becomes a coin machine.",
                                        "Set m_value = 0 unless the item should really be sold, and then price it above what it costs to make.");
                            }
                            else if (ins.Operand is MethodReference called)
                            {
                                string owner = called.DeclaringType.FullName, name = called.Name;
                                if (owner == "HarmonyLib.Harmony" && name.StartsWith("Unpatch")) unpatches = true;
                                if (name == "Repoint" && called.DeclaringType.Name == "ItemReload" && !InTemplate(type, "ItemReload")) repointsItems = true;
                                if ((name == "GetComponent" || name == "GetComponentInParent" || name == "TryGetComponent") && called is GenericInstanceMethod gc
                                    && gc.GenericArguments[0].Name == "Piece") usesPiece = true;
                                if (owner.Contains("ServerSync") || owner.EndsWith("ServerSettings") || owner.Contains("SynchronizationManager")) syncs = true;
                                if ((owner == "ZRoutedRpc" || owner == "ZNetView") && name == "Register") networked = true;
                                if (name == "Bind" && owner == "BepInEx.Configuration.ConfigFile") binds = true;
                                if (owner == "HarmonyLib.Harmony" && name == "UnpatchAll" && (called.Parameters.Count == 0 || (i > 0 && code[i - 1].OpCode == OpCodes.Ldnull)))
                                    Add("problem", "unpatch-all", where, "It calls UnpatchAll() without a Harmony id.",
                                        "With no id, UnpatchAll removes every mod's patches, not only this one's: when this mod unloads (a hot reload, the manager's " +
                                        "update), every other mod stops working until the game restarts.",
                                        "Use _harmony.UnpatchSelf() (or UnpatchAll(yourHarmonyId)).");
                                if (ins.OpCode == OpCodes.Newobj && owner == "System.IO.FileSystemWatcher") watcherWhere = watcherWhere ?? where;
                                if (name == "Dispose" && (owner == "System.IO.FileSystemWatcher" || owner == "System.ComponentModel.Component")) disposesWatcher = true;
                                if (name == "SetHeight" && called.DeclaringType.Name == "Inventory") heightWhere = heightWhere ?? where;
                                if (called.DeclaringType.Name == "ZDO" && name == "Set" && called.Parameters.Any(x => x.ParameterType.Name == "ZDOID")
                                    && !findings.Any(x => (string)x["rule"] == "saves-zdoid" && (string)x["where"] == where))
                                    Add("warning", "saves-zdoid", where, "It saves a ZDOID in an object's saved data (ZDO.Set).",
                                        "A ZDOID is renumbered every time the world loads: after a restart, a saved one points at some other object. Fine only if " +
                                        "it is cleared before the world is saved or never read after a restart.",
                                        "Save your own random id on the object instead, or use ZDO.SetConnection, which the game re-links when the world loads.");
                                if (called.DeclaringType.Name == "GameKeys" || (called.DeclaringType.Name == "ZInput" && name.StartsWith("Setbutton"))) freesKeys = true;
                                if (owner == "UnityEngine.Shader" && name == "Find")
                                    Add("note", "shader-find", where, "It looks a shader up by name (Shader.Find).",
                                        "Shader.Find returns null for shaders the game doesn't include (and on a dedicated server nothing is drawn): new Material(null) throws, " +
                                        "and if that happens while registering a piece, the piece isn't registered and the host deletes the placed ones.",
                                        "Take materials from the game's own prefabs, and fall back safely (try/catch around the visual part of registering).");
                                // scene searches and searches of the whole world, in code that runs every frame or on a timer from Update
                                // (followed through the mod's own methods: a helper called from Update every 2 s is a hitch every 2 s)
                                if (perFrame.TryGetValue(m, out string via) && owner.StartsWith("UnityEngine.") && SceneSearches.Contains(name)
                                    && !OnceGuarded(code, i) && searchSeen.Add("find|" + where))
                                    Add(Steps(via) <= 2 ? "warning" : "note", "find-every-frame", where,
                                        $"It calls {called.DeclaringType.Name}.{name} in {(via == null ? m.Name : "code reached from " + via)}.",
                                        "These walk every loaded object of the kind (about 10 ms a call in a big base). Every frame, or on a timer every few " +
                                        "seconds, it is a hitch players feel: a check every 2 s with two of them cost a mod 20-25 ms each time. (Fine once at load.)",
                                        "Keep your own list (BepInEx/claude/templates/Registry.cs: Live<T>, added in Awake, gone in OnDestroy, or a postfix on the game's " +
                                        "Awake), or use the game's own lists (Player.GetAllPlayers, Character.GetAllCharacters, Chainloader.PluginInfos). Measure with lag 20.");
                                if (perFrame.TryGetValue(m, out via) && called.DeclaringType.Name == "ZDOMan" && name.StartsWith("GetAllZDOsWithPrefab")
                                    && !OnceGuarded(code, i) && searchSeen.Add("zdo|" + where))
                                    Add((name.EndsWith("Iterative") && !LoopsOnResult(code, i)) || Steps(via) > 2 ? "note" : "warning", "world-zdo-scan", where,
                                        $"It searches every saved object in the world (ZDOMan.{name}{(LoopsOnResult(code, i) ? ", called again until it is done, in one frame" : "")}) in {(via == null ? m.Name : "code reached from " + via)}.",
                                        "That walks every sector of the world (hundreds of thousands of objects): in one frame on a timer it is a hitch (a ship search " +
                                        "once a second cost 40 ms), and a sliced one started again the moment it ends costs a millisecond every frame for ever.",
                                        "Keep the list up to date instead (your own objects' Awake and OnDestroy). If you must search, make one iterative call a frame, " +
                                        "rest a few seconds between passes, and check each ZDO again before using it (removed ones are reused): templates/Registry.cs, Sliced.");
                                if (name == "Bind" && owner == "BepInEx.Configuration.ConfigFile" && called is GenericInstanceMethod g && g.GenericArguments[0].Name == "KeyCode")
                                    KeyDefault(code, i, where, "a key setting", keyFindings);
                                if (ins.OpCode == OpCodes.Newobj && owner == "BepInEx.Configuration.KeyboardShortcut")
                                    KeyDefault(code, i, where, "a key shortcut", keyFindings);
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Add("note", "unreadable", Path.GetFileName(dll), "Part of the DLL could not be read: " + e.Message, "", ""); }
            finally { resolver.Dispose(); }

            foreach (JObject k in keyFindings)
            {
                if (freesKeys) { k["level"] = "note"; k["what"] += " (it seems to unbind the game's action: check it tells the player)"; }
                findings.Add(k);
            }
            if (resourcesWhere != null && !readsUpgrader)
                Add("warning", "recipe-cost", resourcesWhere, "It counts a recipe's cost from m_resources but never looks at m_upgraderResource.",
                    "Since the battle idols, m_resources also lists items only an upgrader station charges (the flint axe lists 1 Upgrader1Weapon). Counting all of " +
                    "them thinks every recipe needs an idol (nothing gets made), or refunds idols that were never paid (free idols from a recycler).",
                    "Skip requirements whose m_upgraderResource doesn't match the station's m_upgrader, as Player.HaveRequirementItems does.");
            if (patches.Any(p => (string)p["target"] == "Container.Awake") && !usesPiece)
                Add("warning", "container-not-chest", (string)patches.First(p => (string)p["target"] == "Container.Awake")["method"],
                    "It collects containers (Container.Awake) without checking they're built pieces.",
                    "Companions, carts, ships, tombstones and backpack proxies have a Container too: a mod using \"every container nearby\" spends a " +
                    "companion's arrows, a grave's contents or a backpack twice.",
                    "Keep to containers with a Piece in their parents (GetComponentInParent<Piece>() != null) and no Character.");
            foreach (JObject p in patches.Where(p => (string)p["kind"] == "prefix" && ((string)p["skips"] ?? "never") != "never" && (bool?)p["runOriginal"] != true
                                                     && Compat.DoesTheJob.Contains((string)p["target"])))
                Add("warning", "pays-twice", (string)p["method"], $"Its prefix on {p["target"]} can skip the game's method and does the job itself, but doesn't check whether another mod already did.",
                    "Every prefix runs even after another returned false: next to a mod doing the same (a backpack paying from itself, another auto-deposit), " +
                    "the job is done twice (paid twice, deposited twice).",
                    "Add a `bool __runOriginal` parameter and return false at once when it's already false; run at [HarmonyPriority(Priority.Last)].");
            if (binds && (networked || addsPrefabs) && !syncs)
                Add("note", "own-settings", plugins.FirstOrDefault()?["class"]?.ToString() ?? Path.GetFileName(dll),
                    "It has settings and works in multiplayer, but nothing makes the server decide them.",
                    "On a server, each player sets their own: rewards, ranges, timers and strengths become whatever a player wants.",
                    "Let the server decide the gameplay settings (BepInEx/claude/templates/ServerSettings.cs, ServerSync or Jotunn); keep look-and-feel settings local.");
            if (watcherWhere != null && !disposesWatcher)
                Add("warning", "watcher-not-disposed", watcherWhere, "It watches files (FileSystemWatcher) but never disposes the watcher.",
                    "After a hot reload the old copy's watcher keeps firing into the unloaded mod (errors, settings applied twice).",
                    "Keep the watcher in a field and call Dispose() on it in OnDestroy. Set SynchronizingObject = ThreadingHelper.SynchronizingObject " +
                    "so its events arrive on the game's main thread.");
            if (patches.Count > 0 && !unpatches)
                Add("warning", "no-unpatch", plugins.FirstOrDefault()?["class"]?.ToString() ?? Path.GetFileName(dll), "It never removes its Harmony patches.",
                    "With hot reload (ScriptEngine, the mod manager's updates), the old copy's patches keep running next to the new copy's: everything happens twice.",
                    "In OnDestroy: _harmony?.UnpatchSelf();");
            if (addsPrefabs && !usesJotunn && !patches.Any(p => (string)p["target"] == "ZNetScene.Awake"))
                Add("problem", "prefab-registration", prefabsWhere, "It adds prefabs to ZNetScene but has no ZNetScene.Awake patch.",
                    "Placed pieces are saved by prefab name. If the host loads a zone before your prefab is registered, the game logs \"Missing prefab hash\" and deletes " +
                    "those pieces for good. ObjectDB.Awake can run after ZNetScene.Awake on a fresh launch; hot reload hides this.",
                    "Register from a ZNetScene.Awake postfix and an ObjectDB.Awake postfix (and in Awake for hot reload). Test with a full restart.");
            if (heightWhere != null && !patches.Any(p => (string)p["target"] == "Player.SetInventorySize"))
                Add("warning", "inventory-height", heightWhere, "It changes an inventory's height but doesn't handle Player.SetInventorySize.",
                    "Player.OnSpawned resets the player's inventory height every spawn and drops what is below it on the ground.",
                    "If it's the player's inventory: keep your extra rows in a SetInventorySize postfix (don't take the method over: other inventory mods need it too).");
            foreach (var kv in customKeys.Where(k => k.Key.Length < 12 && k.Key.IndexOfAny(new[] { '.', '_', '#', ':', '-', '/' }) < 0))
                Add("warning", "custom-data-key", kv.Value, $"It uses the custom-data key \"{kv.Key}\".",
                    "m_customData is shared by every mod: a short, plain key can collide with another mod's.",
                    "Prefix your keys with your mod's GUID or name: \"YourMod.key\".");

            // the same job through different methods, by game system
            foreach (var sys in patches.Where(Risky).GroupBy(p => others.SystemOf((string)p["target"])).Where(g => g.Key != null && g.Key != "registering items and pieces"))
            {
                List<string> theirs = others.SameSystem(sys.Key).Where(o => !sys.Any(p => o.StartsWith((string)p["target"] + " "))).ToList();
                if (theirs.Count == 0) continue;
                Add("note", "same-job", string.Join(", ", sys.Select(p => (string)p["target"]).Distinct().ToArray()),
                    $"It changes {sys.Key}, which popular mods also change through other methods: " + string.Join("; ", theirs.Take(6).ToArray()) + (theirs.Count > 6 ? $" (+{theirs.Count - 6} more)" : ""),
                    "Mods doing one job in different methods can each do it, twice (two mods each adding chest items to what the player has: crafts pay half).",
                    "Read how they do it (library get <mod>, then decompile), and detect them: stand down or work with them when they're installed.");
            }

            // ---- whether it can be reloaded while playing ----
            var why = new List<string>();
            int level = 0; // 0 world, 1 main menu, 2 restart
            void Need(int l, string reason) { level = Math.Max(level, l); why.Add(reason); }
            if (usesJotunn) Need(2, "it uses Jotunn, which registers its things once per game");
            if (managers != null) Need(2, $"it includes {managers}, which patches with its own Harmony id: unloading can't undo it");
            // a mod whose pieces are made reloadable (they stay registered, and placed ones get the new copy's components: the template's
            // PieceReload) says so with AssemblyMetadata("ClaudeTools.Reload", "world"); that covers pieces and creatures;
            // items too, when every item it adds to ObjectDB goes through the template's ItemReload (kept registered over a reload, replaced by
            // name) and it calls ItemReload.Repoint (the items in bags, chests and on the ground given the new copy's prefabs); recipes and
            // status effects still need the main menu
            bool itemsReload = addsToObjectDB && !objectDbOutsideTemplate && repointsItems;
            bool piecesReload = declaredReload == "world" && (!addsToObjectDB || itemsReload) && heightWhere == null && !(patches.Count > 0 && !unpatches) && !usesJotunn && managers == null;
            if (addsPrefabs && piecesReload) why.Add("it registers pieces or creatures, and its author says they reload in a world (kept registered, the ones in the world given the new copy)");
            else if (addsPrefabs) Need(1, "it registers prefabs (pieces, items or creatures): for a moment they're missing, and placed ones and items in bags " +
                                     "point at the old copy");
            if (addsToObjectDB && piecesReload) why.Add("it adds items through the ItemReload template, and its author says they reload in a world (kept registered, the ones in bags, chests and on the ground given the new copy)");
            else if (addsToObjectDB) Need(1, "it adds items, recipes or status effects to ObjectDB" + (declaredReload == "world" && !itemsReload
                                          ? " (for world, add items only through templates/ItemReload.cs and call its Repoint)" : ""));
            if (heightWhere != null) Need(1, "it changes an inventory's size: unloading can drop items from the extra rows");
            if (patches.Count > 0 && !unpatches) Need(1, "it never removes its patches: a reload runs them twice");
            int declared = Array.IndexOf(ReloadLevels, declaredReload ?? "");
            if (declared > level) Need(declared, "its author says so (AssemblyMetadata \"ClaudeTools.Reload\")");
            var reload = new JObject
            {
                ["level"] = ReloadLevels[level],
                ["means"] = level == 0 ? "safe to reload in a world" : level == 1 ? "reload only at the main menu (or restart)" : "restart the game to load a new build",
                ["why"] = new JArray(why), ["declared"] = declaredReload,
            };

            string[] order = { "problem", "warning", "note" };
            var sorted = findings.OrderBy(f => Array.IndexOf(order, (string)f["level"])).ToList();
            return new JObject
            {
                ["dll"] = Path.GetFileName(dll), ["plugins"] = new JArray(plugins.Select(p => $"{p["name"]} {p["version"]} ({p["guid"]})")),
                ["patches"] = patches.Count,
                ["problems"] = sorted.Count(f => (string)f["level"] == "problem"), ["warnings"] = sorted.Count(f => (string)f["level"] == "warning"),
                ["notes"] = sorted.Count(f => (string)f["level"] == "note"),
                ["findings"] = new JArray(sorted), ["reload"] = reload,
            };
        }

        /// <summary>What a test command shouldn't do without the cheat rule, by the game method it calls.</summary>
        private static readonly Dictionary<string, string> CheatCalls = new Dictionary<string, string>
        {
            ["Inventory.AddItem"] = "give items", ["Player.TeleportTo"] = "teleport the player", ["Character.Damage"] = "hurt creatures",
            ["Character.SetHealth"] = "set health", ["ZNetScene.Destroy"] = "remove things from the world", ["ZDOMan.DestroyZDO"] = "remove things from the world",
            ["Player.AddKnownItem"] = "unlock recipes", ["Player.RaiseSkill"] = "raise skills", ["Skills.CheatRaiseSkill"] = "raise skills",
        };

        /// <summary>Reload levels, safest first: in a world, at the main menu only, only with a restart.</summary>
        internal static readonly string[] ReloadLevels = { "world", "menu", "restart" };

        /// <summary>blaxxun-boop's managers (merged into a mod as source or with ILRepack) patch the game with their own Harmony ids.</summary>
        private static readonly HashSet<string> ManagerNamespaces = new HashSet<string>
            { "ItemManager", "PieceManager", "CreatureManager", "LocationManager", "SkillManager", "StatusEffectManager" };

        // The template's placeholders (com.yourname.examplemod, ExampleMod, YourName.Shared). No real GUID or name of ours contains either word.
        private static bool IsPlaceholder(string s) =>
            !string.IsNullOrEmpty(s) && (s.IndexOf("yourname", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("examplemod", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool Risky(JObject p) =>
            (string)p["kind"] == "transpiler" || (string)p["kind"] == "hook" || ((string)p["skips"] ?? "never") != "never" || (bool?)p["changesResult"] == true || p["changesArgs"] != null;

        /// <summary>Whether the value read at <paramref name="at"/> goes into a collection: the next call is Add, Insert or a set (not a lookup).</summary>
        private static bool AddsTo(IList<Instruction> code, int at)
        {
            for (int k = at + 1; k < Math.Min(code.Count, at + 14); k++)
                if (code[k].Operand is MethodReference mr && (code[k].OpCode == OpCodes.Call || code[k].OpCode == OpCodes.Callvirt))
                {
                    if (mr.Name == "Add" || mr.Name == "Insert" || mr.Name == "set_Item" || mr.Name == "AddRange" || mr.Name == "TryAdd") return true;
                    if (mr.DeclaringType.Name.StartsWith("List") || mr.DeclaringType.Name.StartsWith("Dictionary")) return false; // a lookup
                }
            return false;
        }

        /// <summary>Whether a store to m_customData only happens when it was null (if (x.m_customData == null) x.m_customData = new ...).</summary>
        private static bool NullGuarded(IList<Instruction> code, int at)
        {
            for (int k = at - 1; k >= Math.Max(0, at - 10); k--)
                if (code[k].Operand is FieldReference f && f.Name == "m_customData" && code[k].OpCode == OpCodes.Ldfld && code[k].Next != null
                    && (code[k].Next.OpCode == OpCodes.Brtrue || code[k].Next.OpCode == OpCodes.Brtrue_S || code[k].Next.OpCode == OpCodes.Dup)) return true;
            return false;
        }

        /// <summary>The default key loaded just before a Bind&lt;KeyCode&gt; or new KeyboardShortcut(...), if it's one of the game's own.</summary>
        private static void KeyDefault(IList<Instruction> code, int at, string where, string what, List<JObject> into)
        {
            for (int k = at - 1; k >= Math.Max(0, at - 4); k--)
            {
                int? v = Int(code[k]);
                if (v == null || code[k].Next?.OpCode == OpCodes.Newarr) continue; // (an array's length, not the key)
                if (GameKeys.TryGetValue(v.Value, out string key))
                    into.Add(new JObject
                    {
                        ["level"] = "warning", ["rule"] = "game-key", ["where"] = where, ["what"] = $"It sets {what} to {key} by default.",
                        ["why"] = "The game reads its own keys whatever a mod does: pressing it does your action and the game's (a quick slot on V also toggled auto-pickup).",
                        ["fix"] = "Pick a key the game doesn't use, or unbind the game's action once and tell the player.",
                    });
                return;
            }
        }

        private static int? Int(Instruction i)
        {
            if (i.OpCode == OpCodes.Ldc_I4_S || i.OpCode == OpCodes.Ldc_I4) return Convert.ToInt32(i.Operand);
            if (i.OpCode.Code >= Code.Ldc_I4_0 && i.OpCode.Code <= Code.Ldc_I4_8) return i.OpCode.Code - Code.Ldc_I4_0;
            return null;
        }

        private static IEnumerable<TypeDefinition> All(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(All));

        /// <summary>The call's answer jumps back to before it (for (...; !Search(...); ) {}): the iterative search is run to the end in one frame.</summary>
        private static bool LoopsOnResult(IList<Instruction> code, int at)
        {
            for (int j = at + 1; j < Math.Min(code.Count, at + 3); j++)
                if (code[j].OpCode.FlowControl == FlowControl.Cond_Branch && code[j].Operand is Instruction to && to.Offset < code[at].Offset) return true;
            return false;
        }

        /// <summary>How many calls from the per-frame method (0: in it). Close by is the timer pattern; deep down it is more often a key or a command.</summary>
        private static int Steps(string via) => via == null ? 0 : (via.Length - via.Replace(" -> ", "").Length) / 4;

        /// <summary>
        /// The call sits behind a once-only guard: a field read and branched on before it, and set in the same method (if (_found) return
        /// _shader; _found = true; ... a search). Such a lookup runs once, not every frame.
        /// </summary>
        private static bool OnceGuarded(IList<Instruction> code, int at)
        {
            var stored = new HashSet<string>(code.Where(x => (x.OpCode == OpCodes.Stfld || x.OpCode == OpCodes.Stsfld) && x.Operand is FieldReference)
                .Select(x => ((FieldReference)x.Operand).FullName));
            for (int i = 0; i < at; i++)
            {
                if (!((code[i].OpCode == OpCodes.Ldfld || code[i].OpCode == OpCodes.Ldsfld) && code[i].Operand is FieldReference f && stored.Contains(f.FullName))) continue;
                // a flag or a kept object (null or not), never a number: "if (Time.time < _next) return; _next = ..." is a timer, not a once
                string t = f.FieldType.FullName;
                if (t == "System.Single" || t == "System.Double" || t == "System.Int32" || t == "System.Int64" || t == "System.UInt32") continue;
                for (int j = i + 1; j < Math.Min(at, i + 4); j++)
                    if (code[j].OpCode.FlowControl == FlowControl.Cond_Branch) return true;
            }
            return false;
        }

        private static readonly HashSet<string> SceneSearches = new HashSet<string>
        {
            "FindObjectsOfTypeAll", "FindObjectsOfType", "FindObjectsByType", "FindObjectOfType", "FindFirstObjectByType", "FindAnyObjectByType",
        };

        // the game's methods that run every frame (or physics step) for each object: a patch on one runs as often
        private static readonly string[] PerFrameTargets = { ".Update", ".LateUpdate", ".FixedUpdate", ".CustomUpdate", ".CustomFixedUpdate", ".CustomLateUpdate", ".UpdateAI", ".OnGUI" };

        /// <summary>
        /// The mod's methods that run every frame or from code that does (on a timer, say): its Update, LateUpdate, FixedUpdate and OnGUI, its
        /// patches on the game's per-frame methods, and everything of its own they call (lambdas and coroutines too), a few calls deep. The
        /// value is the way there ("Plugin.Update -> Landmarks.Tick"), null for the per-frame method itself.
        /// </summary>
        private static Dictionary<MethodDefinition, string> PerFrame(ModuleDefinition module, List<JObject> patches)
        {
            var found = new Dictionary<MethodDefinition, string>();
            var queue = new Queue<KeyValuePair<MethodDefinition, int>>();
            var hotPatches = new HashSet<string>(patches.Where(p => PerFrameTargets.Any(t => ((string)p["target"] ?? "").EndsWith(t))).Select(p => (string)p["method"]));
            foreach (TypeDefinition type in module.Types.SelectMany(All))
            foreach (MethodDefinition m in type.Methods.Where(x => x.HasBody))
                if (((m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate" || m.Name == "OnGUI") && m.Parameters.Count == 0)
                    || hotPatches.Contains(Scan.Name(type) + "." + m.Name))
                    if (!found.ContainsKey(m)) { found[m] = null; queue.Enqueue(new KeyValuePair<MethodDefinition, int>(m, 0)); }
            string Short(MethodDefinition m) => Scan.Name(m.DeclaringType) + "." + m.Name;
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                if (at.Value >= 6) continue;
                string path = found[at.Key] ?? Short(at.Key);
                foreach (Instruction ins in at.Key.Body.Instructions)
                {
                    if (!(ins.Operand is MethodReference r)) continue;
                    MethodDefinition d;
                    try { d = r.Resolve(); } catch (Exception) { continue; }
                    if (d == null || d.Module != module) continue;   // (only the mod's own code)
                    var next = new List<MethodDefinition>();
                    if (d.HasBody) next.Add(d);
                    // a coroutine's or lambda's state made here: its MoveNext runs from here too
                    if (ins.OpCode == OpCodes.Newobj) next.AddRange(d.DeclaringType.Methods.Where(x => x.HasBody && x.Name == "MoveNext"));
                    foreach (MethodDefinition n in next)
                    {
                        if (found.ContainsKey(n)) continue;
                        string step = n.Name == "MoveNext" || n.Name.StartsWith("<") ? Scan.Name(n.DeclaringType) : Short(n);
                        found[n] = path + " -> " + step;
                        queue.Enqueue(new KeyValuePair<MethodDefinition, int>(n, at.Value + 1));
                    }
                }
            }
            return found;
        }

        /// <summary>The type is a template's class (by its name), or nested in it (its lambdas).</summary>
        private static bool InTemplate(TypeDefinition t, string name)
        {
            for (; t != null; t = t.DeclaringType) if (t.Name == name) return true;
            return false;
        }
    }
}
