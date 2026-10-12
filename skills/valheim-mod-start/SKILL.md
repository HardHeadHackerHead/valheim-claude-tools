---
name: valheim-mod-start
description: Start a new Valheim mod (BepInEx plugin) from nothing, build it, load it into the running game and see it work. Use when someone wants to make a Valheim mod, add a feature to the game, or asks how modding Valheim works.
---

# Starting a Valheim mod

> **Game closed?** The mod-maker commands (`modcheck`, `who`, `clashes`, `patches`, `game`, `gameupdate`, `library`, `systems`) also run as a program: `BepInEx/claude/modkit/modkit.exe <command>` on Windows, `dotnet BepInEx/claude/modkit/modkit.dll <command>` on Linux and Mac (the command `help` lists them all). The others (`errors`, `log`, `waitfor`, pictures...) need the game running; its log is `BepInEx/LogOutput.log`.

A Valheim mod is a C# class library (.NET Framework 4.8) that BepInEx loads into the game. It changes the game by **patching** the game's
own methods with Harmony (run your code before or after them), and by adding things (items, pieces) to the game's lists. Work in this
order, and check each step in the game before the next.

## 1. What you need

- **Valheim with BepInEx** (the `denikson-BepInExPack_Valheim` package): installed by a mod manager (r2modman, Thunderstore Mod Manager)
  or by hand into the game folder.
- **Claude Tools**, which also brings the **dev loader**: the mod you are making goes in `BepInEx/scripts`, loads when the game starts,
  and reloads on its own with `reload YourMod`, without restarting the game (step 4). Nothing else to install for that. Set
  `AllowRequests = true` in `BepInEx/config/com.quad.claudetools.cfg` so Claude can send it commands.
- **Your BepInEx folder.** Installed by hand, it is `<Valheim>/BepInEx` (Steam → Valheim → Manage → Browse local files). With
  r2modman or Thunderstore Mod Manager each **profile** has its own: Settings → "Browse profile folder", then `BepInEx` inside it. Every
  `BepInEx/...` path in these skills means that folder (`BepInEx/claude` is where Claude Tools writes its guide, skills and tools).
- **The game's console**, for `claude <command>` and the game's own cheats: Settings → Gameplay → "Enable console" (or the `-console`
  launch option in Steam), then F5 opens it. Commands that cheat (`give`, `grow`, `objects ... remove`, `shoot tp`) work in single
  player and for the host; on someone else's server they need `devcommands`, which only the server's admins can use.
- The **.NET SDK** (8 or later: it builds .NET Framework 4.8 libraries with the reference assemblies package) and **ilspycmd** to read the
  game's code: `dotnet tool install -g ilspycmd`.
- Read `BepInEx/claude/CLAUDE.md`: every command Claude Tools has, and how requests work.

## 2. Make the project

Make the mod in its own folder (anywhere, not inside the game's), start Claude Code there, and copy the skills in: `BepInEx/claude/.claude/skills`
to the project's `.claude/skills`. Start from the project template, `BepInEx/claude/templates/project` (copy the whole folder):
`mods/Directory.Build.props` (game paths and references), `mods/Shared` (code compiled into each mod), `mods/ExampleMod/` (rename
folder, csproj, namespace, GUID: `publish.ps1` and `modcheck` refuse the template's names, `com.yourname.examplemod`, `ExampleMod`,
`YourName`) and `publish.ps1`. Write the game folder into `mods/Directory.Build.local.props` (`<ValheimDir>`, not committed; the path
Claude Tools reports), or set `VALHEIM_DIR`; when a mod manager keeps BepInEx in a profile, `BEPINEX_DIR` to that profile's `BepInEx` folder. The
plugin:

```csharp
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.yourname.yourmod";   // unique forever; never change it
    public const string Name = "YourMod";
    public const string Version = "1.0.0";
    private Harmony _harmony;

    private void Awake()
    {
        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Guid);
        Logger.LogInfo($"{Name} {Version} loaded");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();  // hot reload: never leave old patches behind
}

[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
internal static class Player_ConsumeResources
{
    private static void Postfix(Player __instance) { /* after the game's own code */ }
}
```

## 3. Before writing game code: look it up

Never guess the game's names. Use the **valheim-game-code** skill: `game Player.ConsumeResources` (in a request, or `claude game ...` in
the game's console) gives the real signature, who calls it and which popular mods patch it already. Read the game's code for anything you
change.

## 4. Build, load, check

```
dotnet build mods/YourMod -c Release        # copies YourMod.dll and .pdb into BepInEx/scripts
```

Building only copies the file: the game keeps running the old copy until it is reloaded. With the game running, send (a request file in
`BepInEx/claude/requests`, or `claude <command>` in the console):

```
reload YourMod
errors
modcheck YourMod
```

`reload` unloads your mod and loads the new build (its answer names the version it loaded: check it's the new one), `errors` shows
exceptions since the last check (with the mod they came from), `modcheck` runs the pre-release check (see **valheim-prerelease**). The
first time, put the DLL in `scripts` before starting the game (it loads at start), or `reload` loads it.

**When a mod may be reloaded.** A reload unloads the old copy (its `OnDestroy` runs) and starts the new one a frame later. Whether that is
safe depends on what the mod does: `modcheck` gives its **reload level**, `reload` keeps to it, and `devmods` lists every mod's.

| Level | The mod | Reload |
|---|---|---|
| `world` | only patches methods, adds UI, commands, settings; keeps its saved state in ZDOs or `m_customData` | anywhere, in a world too |
| `menu` | registers pieces, items, creatures, recipes or status effects, or changes an inventory's size | at the main menu only: for a moment its prefabs are missing (a host loading an area then deletes those objects), and items in bags point at the old copy |
| `restart` | uses Jotunn or blaxxun-boop's / Azumatt's managers (they patch with their own Harmony ids) | only by restarting the game |

A mod can declare a stricter level than the one found: `[assembly: AssemblyMetadata("ClaudeTools.Reload", "menu")]`. To try a `menu` mod,
quit to the main menu, `reload YourMod`, then load the world. `reload YourMod force` ignores the level: only in a test world. The old
copy's code and static fields stay in memory (.NET never unloads it), so a mod that keeps state in statics must reset it in `OnDestroy`.
If ScriptEngine is installed too, its F6 reloads **every** mod in `scripts` at once with no such check: use `reload` instead.

**Pieces that reload while you play.** A mod whose only `menu` reason is its build pieces can be made reloadable in a world with
`templates/PieceReload.cs` (about 60 lines; the Bounty Board uses it). It fixes the two things that go wrong:
1. The gap: the old copy no longer unregisters its piece (`UnloadPiece` only takes it out of the hammer menu), and the new copy's
   registration replaces it by name (`ReplacePrefab`). The game never stops knowing the piece, so no area loading meanwhile loses any.
2. The pieces standing: after registering, `Reattach` swaps the old copy's components on placed pieces for the new copy's, so E on them
   runs the new code at once. Their fields start at defaults: a component reads what it needs in `Awake`, not from fields set on the prefab.

Then declare it, `[assembly: AssemblyMetadata("ClaudeTools.Reload", "world")]`, and `modcheck` gives it `world`. Worth it when you change
a piece mod often (no trip to the main menu, nothing deleted); players never notice either way, since they start the game with the mod.

**Items that reload while you play.** `templates/ItemReload.cs` does the same for items (Quad's Dungeons uses it):
1. The gap: the old copy leaves its item prefabs registered and alive (on unload, do nothing to them while `KeepOnUnload` is true); the
   new copy's `Replace` puts its own in their place by name, in ZNetScene and ObjectDB. A dropped item in an area loading meanwhile, or a
   chest loading then, never meets a missing prefab.
2. The items already out there hold the old copy's prefab: after registering in `Awake`, `Repoint` gives the ones in the player's bag,
   loaded chests and on the ground the new copy's (anything missed still works: the old prefab is alive, and items save by name).
Add items only through `Replace`, and keep item prefabs plain (no component of yours on them). `modcheck` gives `world` to a mod that adds
items only through `ItemReload` and calls its `Repoint`; recipes and status effects still need the main menu.

**Test in a world you can lose.** Make a test world (and character) for your mod, and back up `BepInEx/scripts` before copying in a build
someone else made. Never try a build that saves things (pieces, chest contents, player data) on your main world first.

**Try it like a player would**, with commands instead of an afternoon of play (a request file, or `claude <command>` in the console):

```
prefabs mymod                 the exact names of your prefabs (as objects, give and render need them)
give MyItem 5                 put your item in the bag (cheat)
inventory                     what the player carries, before and after
chests 10                     what the chests around hold, before and after
nearby 20                     creatures, buildings and dropped items around
objects piece_mything         every one of your pieces in the world, loaded or not
render piece_mything views=4  your model on its own, from four sides
shoot find mything            then shoot tp / look / shot: set up a screenshot
```

Measure, don't eyeball: take `inventory` and `chests` before the thing you test (crafting, building, a reward) and after, and check the
difference is exactly what the recipe or the rule says.

## 5. Things that only show after a real restart

Hot reload hides several bugs (see **valheim-pitfalls**): pieces registered too late, inventory reset on spawn. Before calling anything
done, quit the game completely, start it again, load the world, and run `errors` and `log 200 Missing prefab`.

## 6. Play well with other mods from the start

Players run dozens of mods. Read **valheim-compat** before your first patch: postfix by default, skip the game's method only for your
own objects, never replace lists or results the game passes in, prefix your saved keys, and run `who <method>` before patching.

**Private game methods.** Many methods worth patching are private (`InventoryGui.DoCrafting`, `Container.Load`,
`Player.HaveRequirementItems`): `nameof` can't see them, and calling them doesn't compile. Patch them by name,
`[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]`, and call them through Harmony's `AccessTools` (`AccessTools.Method(typeof(Container),
"Load")`, kept in a static field). `game <Type.Method>` says whether a method is public.

## 7. Build it the way the best mods are built

From reading the code of the 100 most-downloaded Valheim mods:

**Settings**
- `Config.Bind` with `AcceptableValueRange` for numbers, sections numbered so they sort ("1 - General", "2 - Keys"), and
  `ConfigurationManagerAttributes` (`Order`, `IsAdvanced`) for players who use ConfigurationManager: copy
  `ConfigurationManagerAttributes.cs` from https://github.com/BepInEx/BepInEx.ConfigurationManager into your project (it's found by name).
- Bind many settings in one go: `Config.SaveOnConfigSet = false`, bind them all, then `Config.SaveOnConfigSet = true; Config.Save();`.
- Apply edits to the .cfg live (a server owner edits the file while it runs). `ServerSettings.cs` does it for you; without it: a
  `FileSystemWatcher` on the file, a one-second debounce, `Config.Reload()` in try/catch on the game's thread, and **dispose it in
  `OnDestroy`** (hot reload).
- **Multiplayer settings come from the server.** Copy `BepInEx/claude/templates/ServerSettings.cs` (no library, hot-reloads):
  `Synced.Bind("1 - Rewards", "Percent", 100, "...", range)` instead of `Config.Bind` for every gameplay value (rewards, costs, ranges,
  timers, strengths: anything a player could set to gain an edge); look-and-feel settings stay each player's own (`Config.Bind`). It
  gives what ServerSync does that matters, without its catches:
  - joining players get the server's values; leaving gives the player's own back, and their .cfg keeps their own even if it's saved meanwhile;
  - edits to the server's .cfg reach everyone without a restart;
  - the host and the server's admins (`adminlist.txt`) change the server's values from their own game in Configuration Manager (the
    server checks the admin list on the connection itself and saves its file); everyone else sees them read-only there;
  - `Synced.Server` says whether the server has the mod (and `ServerVersion` which), for the next point;
  - `Synced.AddText(key, own, use)` for data the server decides that isn't one setting (a pack, a list, a table): sent at join and on
    `TextChanged(key)`, compressed and in parts when big (Steam refuses a message over 512 KB and the connection stalls).

  Most popular mods use **ServerSync** (https://github.com/blaxxun-boop/ServerSync, merged in with ILRepack) or Jotunn's sync. Know
  their behaviour when you meet it: ServerSync with its lock **off** lets any player rewrite the server's .cfg; with `ModRequired` and no
  minimum version it demands the exact same version and kicks everyone without the mod, vanilla players too.

**Versions between players**: never kick by default (vanilla players and friends with an older copy should still get in). What matters is
the server having the mod when the mod adds pieces or items: a player who has it, on a server that doesn't, builds things the server
deletes. `Synced.NeedsServer(piecePrefab)` (or a `Recipe`) keeps them out of the menus there and back everywhere else; tell the player
why on `Synced.ServerChanged` when `Synced.Server` is `Missing` (Jet Skis says "isn't installed on this server" once they're in the
world). A different version is written to the log with both numbers. A player *without* the mod on a server that has it loses nothing.

**Words in the player's language.** Never hard-code English that players see: every word goes through the game's own translations
(`Localization`), so it follows the game's Language setting. Popular mods use **LocalizationManager** (blaxxun-boop: embedded
`translations/English.yml` plus other languages, players add their own files; it patches with its own Harmony id, so reload level
`restart`). With no library: copy `BepInEx/claude/templates/Translations.cs` (it hot-reloads):
- `translations/English.json` in the project (a flat `{"key": "text"}`: every key, the words the mod was written in) and one file per
  language, named as the game names them: `Russian`, `German`, `Chinese` (Simplified), `Portuguese_Brazilian`, `Spanish`, `French`,
  `Polish`, `Turkish` are every language with 2% or more of Valheim's players (Steam reviews, 2026: about 93% together). Built in with
  `<EmbeddedResource Include="translations/*.json" LogicalName="translations/%(Filename)%(Extension)" />`.
- `Translations.Load(this)` in `Awake`, `Translations.Unload()` in `OnDestroy`. In code `T("mymod_recycled", name, amount)`
  (`using static ...Translations;`): the text with `{0}` `{1}` filled in. In the game's own fields (a piece's `m_name` and
  `m_description`, an item's, a status effect's) the key itself, `"$mymod_lantern"`: the game translates it there.
- **Keys start with your mod's name** (`recycler_`), like everything else that's yours (below).
- **A setting's value in a text the game shows itself** (an item's, piece's, skill's or effect's description): write `{heal}` in every
  language's text and `Translations.Fill("mymod_potion_desc", "heal", HealSetting)`; it follows the setting, the server's values and
  the language. `check_translations.py` checks that every language keeps `{heal}`.
- **Words that come from data** (a pack file, a list the server sends): `Translations.AddWords(group, language, words)` (English as
  the fallback), `RemoveWords(group)` when the data goes. Players' own correction files still win over both.
- **The game's own words first**: item, piece, creature and station names come from their `m_name` through
  `Localization.instance.Localize`; common words have keys already (`$menu_cancel`, `$menu_close`, `$menu_yes`, `$menu_no`,
  `$hud_on`, `$hud_off`, `$menu_remove`, `$radial_all`...). `language dump` (Claude Tools) saves every word the game has, in the language
  it shows, to `BepInEx/claude/game-words/<Language>.json`: look a word up there before adding your own, and use the game's term in each
  language for its things (its Workbench is `Werkbank` in German: say the same).
- **Placeholders, not glued sentences**: one key per sentence with `{0}`, never `"Needs a " + name + " nearby"` (word order changes
  between languages). A number with a unit or `%` stays in the translation (`"{0}%"`; Turkish writes `%{0}`, French `{0} %`): colour
  only the number in code. Rich text (`<color>`, `<size>`) stays in code where it can; keys hold plain words.
- **What stays English**: settings (the .cfg, written before any language is known), log lines, commands and their answers, chat
  commands. Only what a player reads in the game.
- **Translate where it's shown, never where it's saved or sent.** Two players on one server can play in two languages: what goes in a
  ZDO, an RPC, a save file or a chat message to others is data (a key and its numbers, a prefab name), and each player's game makes the
  words. A bounty saved as "Hunt 2 × Stone Golem" shows English to a German player; saved as `kind=hunt, count=2, prefab=StoneGolem`
  it shows each player their own.
- **Words that code compares stop matching** once they're translated: `piece.m_name == "Recycler"`, a `Contains("Wood")` on a hover
  text, a window found by its title. Compare prefab names, components or keys instead; search the code for each English word you turn
  into a key.
- **Room for longer words**: German and Russian run about 30% longer than English, Chinese much shorter. Labels that take the height
  their text needs, and buttons wide enough; then look: `language German`, open the window, `screen overlaps`, a shot; again in
  Russian and Chinese (CJK letters come from the game's fallback fonts). `language English` after (or the player's own language: `language`
  says what it was).
- Translations made by AI are said to be so on the mod's page, with how to correct one: a player puts the same file name in
  `BepInEx/config/translations/<mod GUID>/` (only the lines they change), and it wins over the built-in one.

**Items, pieces, creatures, skills, status effects**: most of our mods clone the closest game prefab in code and change it (Jet Skis is the
raft underneath; see **valheim-look**). For your own Unity models, the game's own assets are in AssetBundles: embed yours as a resource and load it once,
reusing it if it's already loaded (a hot reload would otherwise fail with "already loaded"):
`Resources.FindObjectsOfTypeAll<AssetBundle>().FirstOrDefault(b => b.name == name) ?? AssetBundle.LoadFromStream(stream)`.
Then register as in **valheim-pitfalls** ("Register pieces and items from the scene, early"). Two ways most popular mods do the
registering:
- **blaxxun-boop's managers** (ItemManager, PieceManager, CreatureManager, SkillManager, StatusEffectManager, LocationManager, on
  https://github.com/blaxxun-boop, merged into your DLL with ILRepack as their READMEs show): `new Item(bundle, "MyAxe")`,
  `new BuildPiece(bundle, "MyWall")` with costs and crafting stations. Nothing for players to install. They patch with their own Harmony
  ids, which your `UnpatchSelf` doesn't undo: the mod's reload level is `restart`.
- **Jotunn** (a mod players must also install; 25 of the top 100 require it): `PrefabManager.OnVanillaPrefabsAvailable`,
  `PieceManager.Instance.AddPiece`, `ItemManager.Instance.AddItem`.
A piece with no cost is free to build: refuse to register it.

What to set on what you add (the things the managers above do, done our way: by name, again on every register, hot-reload safe).
`BepInEx/claude/templates/Content.cs` does the registering for items, recipes, smelting/cooking/fermenting, creature drops and trader
items (`Content.Items`, `Content.Recipe`, `Content.Conversion`, `Content.Drop`, `Content.Sell`, and `RemoveAll` on unload); with
`ItemReload.cs`, register items through `ItemReload.Replace` and call `Content.ItemExtras`:
- **Costs and stations as settings** a server owner can change: `BepInEx/claude/templates/Costs.cs` reads "FineWood:12, BronzeNails:30"
  (pieces: `Item:amount[:false]` for no refund; recipes: `Item:amount[:per upgrade level]`) and "forge:2" for a station and level. Make
  them server settings (`Synced.Bind`: the client pays when it builds, so it must charge the server's cost), apply when you build the
  prefab and again on `SettingChanged`. A setting with any mistake keeps the last good cost, whole.
- **Build menu**: vanilla categories (`Piece.m_category`) **and** the usage tags the menu now lists by (`Piece.m_usage`, flags such as
  `Furniture`, `Storage`, `Lighting`, `Transport`): a clone keeps its source's tags (a "Misc" slot machine cloned from a chest keeps
  the chest's). Don't add custom category tabs: the libraries that do patch `System.Enum` for the whole game and fight each other.
- **A crafting station of your own**: a unique `CraftingStation.m_name` (`$yourmod_press`), because the game matches stations by that
  name alone (**valheim-pitfalls**). An **extension** is easiest cloned from `piece_workbench_ext1` with its `StationExtension` pointed at
  your station (`m_craftingStation`, `m_maxStationDistance`).
- **Recipes**: one `Recipe` per way to make it, named `Recipe_<prefab>[_<way>]`, replaced by name in `ObjectDB.m_recipes` on every
  register. Repairs and upgrades use the first one. `m_noCraftOnlyUpgrade` for "upgrade only", `m_repairStation` for repairs elsewhere.
- **Items built in code**: set `itemDrop.m_itemData.m_dropPrefab = prefab` yourself (a prefab kept under an inactive holder never runs
  `ItemDrop.Awake`, and an item without it is saved without a name and lost on load). Register the status effects it uses
  (`m_consumeStatusEffect`, `m_equipStatusEffect`, `m_attackStatusEffect`, `m_setStatusEffect`) in `ObjectDB.m_StatusEffects` too.
- **Smelting, cooking, fermenting, creature drops, traders**: remove your own earlier entry by name before adding (**valheim-pitfalls**,
  "Lists on the game's prefabs grow with every load"); a trader through a `Trader.GetAvailableItems` postfix. A creature drop's
  `m_amountMax` is never reached (the game rolls `Random.Range(min, max)` on whole numbers): store one more than the most you mean.
- **Status effects of your own**: `BepInEx/claude/templates/StatusEffects.cs` (made in code, registered by name again on every
  register, taken off every character this game has on unload and put back with their time left after a reload). **Skills**:
  `BepInEx/claude/templates/Skill.cs` (a fixed id that never changes, its name in the skills window and messages, a backup in the
  character's data so a session without the mod doesn't wipe it, `raiseskill`/`resetskill` and tab completion, a live gain factor
  for a server setting). Read "A custom skill's
  levels go when the mod is missing once" and "A status effect's copies share their lists" in **valheim-pitfalls** first.
- **Build-menu picture**: `BepInEx/claude/templates/Icon.cs` draws one from the prefab (`Icon.Make(piece)`, `Icon.MakeItem(item)`),
  falling back to an embedded PNG; see **valheim-look**.
- **Snap points**: the game only reads direct children of the piece's root tagged `snappoint` (active or not), and only while
  placing: the ghost snaps when one of its points is within 0.5 m of a nearby piece's. `BepInEx/claude/templates/SnapPoints.cs`
  (`Clear`, `Add`, `Box(piece, min, max)`) makes them the way the game does. Chests, tables, stations, portals and the raft have none;
  walls, floors and poles do, so a clone of a pole keeps the pole's points 1 m above and below: clear them.

**Multiplayer actions**: register RPCs with names starting with your mod's (`YourMod_RequestTake`); send the request to the object's owner;
in the handler check `ZNetView.IsOwner()` again and send back a "no" (so nothing is lost) when it isn't; identify the player from the
RPC's sender, never from an id in the payload; take ownership only after the access
check (`ZNetView.ClaimOwnership` / `ZDO.SetOwner`). **Never claim ownership and then hand out items yourself**: two players doing it at once
both get them (duplication). The owner gives, as the game's own Fermenter does (`RPC_Tap`); wrap every handler in try/catch and log; compress big payloads. `ZNet.instance.IsServer()`,
`IsDedicated()`; before `ZNet` exists, `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null` means a dedicated server.

**Sharing code** (between your mods, or taken from elsewhere). In the 100 most-downloaded Valheim mods (October 2026), 45 have shared
libraries **compiled into their own DLL** (blaxxun-boop's ServerSync in 40, LocalizationManager, ItemManager, PieceManager...), 25 depend
on a **library mod** players must install too (Jotunn), and 56 need nothing but BepInEx. So:
1. **Compile it in, by default.** Copy the code into your mod (a template from `BepInEx/claude/templates`, or a library's source or DLL
   merged with ILRepack), make its classes `internal` and put them in your namespace. Players install one mod; two mods carrying the same
   code each have their own private copy and never clash.
2. **Several mods in one repo:** one `mods/Shared/` folder, linked into the mods that use it
   (`<Compile Include="../Shared/X.cs" Link="Shared/X.cs" />` in the csproj; files every mod needs, from `Directory.Build.props`). One
   copy to fix, but a fix reaches players only when each mod using it is rebuilt and released. The mod template has this set up
   (`Shared/All` goes into every mod).
3. **A separate library mod** only for something big many mods build on (as Jotunn is): players must install it, and every mod that
   uses it depends on its version (a library update can break them all at once).
4. **Shared code explains itself:** at the top of each file, why it exists, which mods use it and how it gets in. Keep it small, and
   tested in a real mod before a second one uses it.

**Names that are yours.** Everything a mod registers shares one space with every other mod, and two of the same name break one or both:
prefab names (`yourmod_lantern`, not `Lantern`), RPC names, ZDO and `m_customData` keys, console and Claude Tools command names, the
Harmony id (your GUID), `$` localization keys (`$yourmod_lantern`), asset bundle names, config file name (your GUID), and the DLL's
assembly name (`<AssemblyName>YourName.ModName</AssemblyName>`: `modcheck` warns when a popular mod has the same one). Choose the
GUID once (`com.yourname.modname`): BepInEx names the settings file after it, other mods find yours by it, and changing it later means
moving settings over and telling mod managers the old copy is the same mod. Start each with your
mod's name. (A real case: two of our mods both added a `check` command, and one hid the other.) Pick prefab names a player can find,
too: "war stone" turned out to be `BobWarstone`, so `prefabs` was needed to find it.

**Performance**: use the game's own lists (`Player.GetAllPlayers()`, `Character.GetAllCharacters()`), keep lookups in dictionaries by
prefab name, and never search the scene (`FindObjectsOfType`) every frame.

**Chests**: a mod that takes from or puts into chests the player didn't open (crafting from chests, feeding, sorting) copies
`BepInEx/claude/templates/ContainerAccess.cs`: only built chests (not graves, carts or a creature's bag), not one someone has open, ward
and lock respected, taken over and reloaded before touching it.

**Commands that cheat**: a debug command that gives items, moves the player or removes things must follow the game's own rule, or be
left out of the release build: allowed in single player and for the host (`ZNet.instance.IsServer()`), otherwise only with devcommands on
(`Console.instance.IsCheatsEnabled()`). Without that check, any player on a server can cheat with it. Leave read-only parts (status,
lists) open and check the rest; `modcheck` warns about commands that give, teleport, hurt or remove with no check.

## 8. Share it

Raise `Version` for every change, then go through **valheim-prerelease**. Most players install mods from **Thunderstore**
(https://thunderstore.io/c/valheim/) with r2modman or Thunderstore Mod Manager, which put them in `BepInEx/plugins`: loaded once at start
(players restart after an update), with `[BepInDependency]` and `[BepInIncompatibility]` honoured. **valheim-thunderstore** walks
through releasing there: the team and token, getting the Thunderstore CLI (`tcli`), `modkit package` (writes the package's
`thunderstore.toml` and checks everything an upload needs), testing the zip on a clean profile, and publishing. Thunderstore asks mods
made with AI to say so; doing it from the start is easiest: in any `.cs` file,
`[assembly: System.Reflection.AssemblyMetadata("AI_Assisted_Creation", "This assembly was partially or fully created with the assistance of Generative AI.")]`
and `[assembly: System.Reflection.AssemblyMetadata("AI_Model_Vendor", "Anthropic")]`, and a line in the README.

Builds come from your working folder, so unfinished code you haven't committed goes out too: commit or stash it first (see
**valheim-prerelease**). The template's `publish.ps1` also builds a feed for HardHeadHackerHead's mod manager (F7), which installs into
`BepInEx/scripts` and reloads mods in place; there, a mod whose reload level isn't `world` needs a `RESTART_REQUIRED.txt` beside its
csproj, holding the reason, and the manager asks players to restart instead of reloading it.
