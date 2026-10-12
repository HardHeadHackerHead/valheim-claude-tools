---
name: valheim-pitfalls
description: The Valheim modding mistakes that have cost players items and buildings or lagged multiplayer, and how to avoid each. Use when writing code that registers pieces or items, changes inventories, saves data, writes ZDO values or sends data between players, handles death, containers, keys, recipes or crafting costs.
---

# Valheim modding pitfalls

> **Game closed?** The mod-maker commands (`modcheck`, `who`, `clashes`, `patches`, `game`, `gameupdate`, `library`, `systems`) also run as a program: `BepInEx/claude/modkit/modkit.exe <command>` on Windows, `dotnet BepInEx/claude/modkit/modkit.dll <command>` on Linux and Mac (the command `help` lists them all). The others (`errors`, `log`, `waitfor`, pictures...) need the game running; its log is `BepInEx/LogOutput.log`.

Each of these lost players' things in a released mod. Most only show after a real restart or next to other mods, so hot reload hides
them. `modcheck <mod>` finds many of them automatically (see **valheim-prerelease**).

## Register pieces and items from the scene, early

A placed piece is saved by its prefab name. If the **host** loads a zone before the prefab is in `ZNetScene`, the game logs
`Missing prefab hash` then `Destroyed invalid prefab ZDO`, and the piece is gone for good. `ZNetScene.Awake` can run before
`ObjectDB.Awake`, so: get the hammer and cost items from `ZNetScene` (`scene.GetPrefab("Hammer")`), and register from all of:
- a `ZNetScene.Awake` postfix (pieces, creatures and items into `ZNetScene`),
- an `ObjectDB.Awake` postfix (items, recipes, status effects into `ObjectDB`),
- an `ObjectDB.CopyOtherDB` postfix (the main menu's copy of `ObjectDB`, which the character preview uses),
- your plugin's `Awake` when `ZNetScene.instance` / `ObjectDB.instance` already exist (after a reload).
Registering twice must be harmless (check by name first). On unload, remove only entries that still point at your prefab.
Take the piece off the build-menu list you put it on, keeping that `PieceTable` from registration: at the main menu (a reload there) the
hammer can't be looked up, but its list lives on. A destroyed piece left on it makes `Player.UpdateKnownRecipesList` throw on every
change to the bag, between `DoCrafting`'s `AddItem` and `ConsumeResources`: every craft is free. Sweep `p == null` entries when registering.

**A server without the mod deletes them too.** A player who has the mod, on a server that doesn't (a vanilla dedicated server, a host who
never installed it), can build a piece or drop an item that the server deletes as soon as its area loads, with whatever was in it. Keep
such pieces and recipes out of the menus there: `NeedsServer(prefab)` / `NeedsServer(recipe)` in the `ServerSettings.cs` template (it asks
the server over its own connection, and hides them while it waits for the answer); a creature a player summons (a companion) checks
`Synced.Server` before it spawns, and refuses with a message. Never decide "the server has the mod" from a routed
RPC: its sender id is whatever the packet claims. The other way round is harmless: a player *without* the mod on a server that has it
just doesn't see those objects (only the host deletes what it doesn't know).

## The player's inventory height is reset at every spawn

`Player.OnSpawned` calls `Player.SetInventorySize(rows)`, which sets the height and then `Humanoid.DropInvalidItems()` drops everything
below it. **A postfix is too late** (the items are already on the ground), and **taking the method over** (a prefix returning false)
breaks every other mod that changes the height (ValheimPlus' extra rows, quivers, slot mods) and drops their items instead. What works,
as the popular slot mods do it:
- a prefix on `SetInventorySize` that **doesn't skip** it, only notes where your rows are;
- a `[HarmonyPriority(Priority.First)]` prefix on `Humanoid.DropInvalidItems` (the game calls it only from there) that takes the height
  the game and other mods just set, moves your rows' items to just below it, and raises the height to fit them, so nothing is invalid;
- a `Priority.Last` postfix that raises the height again if another mod lowered it;
- your rows' position saved in `Player.m_customData` (e.g. `"YourMod.Base"`), so `Player.Load` can tell other mods' rows from yours, and
  `Math.Max(height, yourBase + yourRows)`, never an exact height.
And keep a list of the slot mods that use the same cells below the normal rows (ExtraSlots, EquipmentAndQuickSlots, AzuExtendedPlayerInventory,
ComfyQuickSlots, BetterArchery's quiver): when one is installed, stand down and move your rows' items into the bag.

## Never save a ZDOID

Every world load renumbers objects: a saved `ZDOID` points at an unrelated object after a restart. Store your own random id on the
object, or use `ZDO.SetConnection` (re-linked by the game). Not even in memory across worlds: a `ZDOID`'s hash comes from a table the game
empties on each join (`ZDOID.Reset`), so a dictionary keyed by ZDOIDs kept through a trip to the main menu can't find them (and can throw).
Key by `(id.UserID, id.ID)` taken when adding, or empty it when a new world starts.

## Fill a tombstone with the game's own move

A grave's container is small until filled: `Inventory.MoveInventoryToGrave(original)` resizes it and moves everything; `AddItem` one by
one loses what doesn't fit. Equipped items are skipped: unequip and clear `m_equipped` first.

## A container saves only when items come and go

Changing an item in place (quality, durability) doesn't save it: call `inventory.m_onChanged` on the game that owns it, or it is lost on
the next reload.

## A container reloads whenever anything in its ZDO changes

`Container.Load` rebuilds the inventory from the save whenever the ZDO's revision changes, and any `ZDO.Set` on that object changes it.
A container on an object that saves other things often (a creature) is rebuilt over and over: in-place changes lost, held items point at
old copies. Skip the reload on the owner after loading once.

## A container's saved contents are bytes, not a string

`ZDOVars.s_items` is a byte array (`Container.Save` writes bytes). `GetString` on it is always `""`, so "is this unloaded chest empty?"
always says yes: Quad's Dungeons destroyed unloaded chests with players' items in them this way. Use `GetByteArray` (or load an
`Inventory`), and test "empty" and "untouched" checks with a chest that holds something.

## Keep a moving object's ZDO small

The game sends a ZDO **whole** (every key) to each player near it whenever its revision changes. Any `ZDO.Set` that changes a value bumps
the revision, and so does every position change of a moving object (`ZDO.InternalSetPosition`): a walking creature, a cart or a ship is
resent many times a second. Each player gets at most about 10 KB of ZDOs per send (`ZDOMan.SendZDOs`, 20 times a second), nearest first,
so one big ZDO takes the whole budget and every other creature's updates arrive seconds late. AICompanion kept its companion's journal,
tally and missions on the companion's ZDO: 11-13 KB, resent with every step, and players saw lag in multiplayer. So:
- **Nothing big or growing on an object that moves** (logs, histories, lists that get longer). Keep it on the player's character
  (`Player.m_customData`) or another object that stays still, and send what changes in small routed RPCs, the whole of it only when a game
  asks.
- **Values that change all the time** (skill progress, stamina, a status that can flip back and forth) go into the ZDO every few seconds
  or on a real change, not on every frame or hit. Each write resends the whole ZDO even when the object stands still.
- **Measure it**: `var pkg = new ZPackage(); zdo.Serialize(pkg); pkg.Size()` is what each player is sent, each time. Check it with
  everything your mod writes on the object, after a long play session (strings that grow show only then); a Container's items count too.
- **News sent to everyone on a timer** (party panels, positions): once a second is plenty, and skip a send when nothing visible changed.
  The server relays each routed RPC to every player.

## A removed ZDO key stays on every other game

`ZDO.RemoveInt` (and the other `Remove...`) only removes the key on this game. Other games apply a ZDO with `ZDO.Deserialize`, which only
adds or overwrites keys: a removed key stays there, on the server, and for players who join later. (Parkour cleared its pose this way,
and other players saw the player frozen in their last move.) Never remove a key others read: write an explicit "none" value.

## An attached player lets go on any input

`Player.SetControls` calls `AttachStop()` whenever an attached player moves, attacks, blocks, jumps or crouches. Attaching the player
somewhere dangerous (a zipline over a gorge) needs a `SetControls` prefix that neutralises those inputs while attached, and a safe
set-down (ground under them, fall height reset) whenever it lets go for another reason: a reload, a logout (the logout point would be
the hanging spot).

## "True" from the game's UseItem doesn't mean it took the item

`Fireplace.UseItem` returns true when the fire is full too, after only showing "can't add more"; `UseItem`/`Interact` answer "handled",
not "taken". Check the station's state yourself (fuel against `m_maxFuel`, a free slot) before paying, or count before and after.

## Take items only once the owner confirms, or give them back

A station another player's game runs decides on its owner's game: your copy can be a moment old, and an RPC to an owner who just left or
unloaded it is dropped without an answer. Taking the items first and sending them (bait into a trap, leaves into a curer, filling a
fermenter run by another player) loses them whenever the owner refuses or never answers. Add one at a time and wait for the station to
change, or keep what you sent until the owner confirms and give it back if no answer comes.

## Count a recipe's cost as the game does

`recipe.m_resources` also lists battle idols, flagged `m_upgraderResource`: the game charges them only at an upgrader station
(`station.m_upgrader`). Ignore the flag and nothing can be crafted (every recipe "needs" an idol), or a recycler hands out free idols.
Filter as `Player.HaveRequirementItems` does.

## Pay in full or not at all

Mods that pay for crafting or building from chests must check they found everything. If a chest emptied in between (multiplayer) or
another mod already counted the chests, a silent shortfall gives the item for less: refuse the action instead. Three traps:
- **Refuse early.** The game hands over the crafted item (`InventoryGui.DoCrafting` calls `AddItem` before `ConsumeResources`) and places
  the piece (`TryPlacePiece` before `ConsumeResources`) **before** it charges. Check and reserve in a prefix on `DoCrafting` /
  `TryPlacePiece`; by `ConsumeResources` it's too late to say no.
- **Not every recipe pays through `ConsumeResources`.** "Any one of these" recipes (`m_requireOnlyOneIngredient`) pay with
  `DoCrafting`'s own `RemoveItem`.
- **Someone may already have paid.** Another mod's prefix (a backpack paying from itself) may have done it and returned false: a prefix that
  pays takes `bool __runOriginal`, returns at once when it's false, and runs at `Priority.Last`. Count the player's own items by walking
  `GetAllItems()`, not `CountItems` (other mods add their sources to it). And pay in the same context that counted: an affordability check
  inside one mod's craft context and a payment outside it charge less than was counted.

## Don't count the same thing twice

Another mod may already add chest items to what the player has (ValheimPlus, AzuCraftyBoxes): two such mods count the chests twice.
Detect them (`Chainloader.PluginInfos`) and stand down (see **valheim-compat**).

## Never put back an old snapshot of shared things

Writing a saved copy of shared world state back (terrain before levelling, a chest's old contents) undoes everything that happened since:
other players' buildings lose their ground and collapse, their items vanish. Only restore what is still exactly as you left it, skip
anything another piece now stands on or uses, and wait until the area is fully loaded (`ZNetScene.instance.IsAreaReady`: a zone's
terrain loads before its pieces). When in doubt, leave it.

## A cost setting with a mistake must keep the old cost

When server owners can change what something costs, a typo must never make it free or cheaper: refuse the whole setting and keep the
last good cost (PieceManager drops a misspelt item and builds the rest, so `Wod:10` makes a piece free). The `Costs.cs` template does
this. Read numbers with `CultureInfo.InvariantCulture` (on a German PC `0.5` reads as 5). Pieces already standing keep the cost they were
built with until their area reloads, so a raised cost refunds more when an old piece is taken down: say so, don't rewrite placed pieces.

## Rewards must not be gamed

- **Leaving must never pay better than losing**: logging out, quitting or a crash mid-contest gets at most what giving up gets.
- **What a mod hands out respects the world's progress**: items from lands whose boss isn't beaten (the world's global keys) don't leave a
  contest with the player, food, meads and arrows included.
- **An item's `m_value` is its price at the trader**: any item with `m_value > 0` sells for coins. A mod item that's cheap to make must
  have `m_value = 0`, or it's a coin machine.
- **Pay players for what they did**: identify a player from the RPC's sender (the peer), never from an id in the payload; rewards per
  character can be farmed with new characters.
- Work out the expected return of anything that pays coins (a slot machine, a contest) and keep it below 100% with the default settings,
  decided by the server.

## Item and player custom data is shared, and the game drops it in places

`m_customData` holds every mod's saved data (backpack contents, enchantments). Never replace the dictionary; only your own prefixed keys.
`Inventory.Save`/`Load` keep it, but:
- **An upgrade makes a new item**: `InventoryGui.DoCrafting` removes the old one and adds a fresh one by name, with no custom data. Copy
  your keys over: a `DoCrafting` prefix notes them from `m_craftUpgradeItem` (and its grid position), a postfix puts them on the new item
  there, a finalizer forgets them (QualityOfLife's `Plugin.UpgradeTags.cs` in https://github.com/HardHeadHackerHead/valheim-mods).
- **Stacking looks only at name, quality and world level** (`Inventory.FindFreeStackItem`, and `ItemDrop.AutoStackItems` on the ground):
  two items with different data merge and one's data is gone. Items that carry data get `m_maxStackSize = 1` and `m_autoStack = false`;
  for marks on the game's own items (a loan), keep marked and unmarked stacks apart: a `FindFreeStackItem` postfix while your item is
  being added, an `InventoryGrid.DropItem` prefix, and mark the item **before** it goes in (Arena's `KeepApart.cs`). Splitting a stack
  copies the data to both halves.
- An item lying on the ground keeps a change only through `ItemDrop.SaveToZDO`, on the game that owns it.
- Data set on a prefab's own item is copied into every new one made from it.

## Lists on the game's prefabs grow with every load

A smelter's, cooking station's or fermenter's `m_conversion`, a creature's `CharacterDrop.m_drops`, spawn lists and `Trader.m_items` live
on prefabs that last the whole session. Adding to them in an Awake postfix adds again at every world load and every hot reload (the
popular PieceManager and ItemManager libraries both do this: a smelter ends up taking your ore three times over). Remove your own
entries **by name** before adding (`c.m_to != null && c.m_to.name == "yourmod_ingot"`; not by reference, an old copy's prefab stays alive
after a hot reload). For a trader, prefer a `Trader.GetAvailableItems` postfix (gone with `UnpatchSelf`, no prefab changed; check
`m_requiredGlobalKey` yourself, and keep your `TradeItem` objects and their order fixed: the store window indexes the list). The
`Content.cs` template does all of these by name.

## A crafting station is known by its name

Recipes, repairs and extensions match a station by `CraftingStation.m_name`, nothing else: a cloned workbench that keeps
`$piece_workbench` *is* a workbench (and keeps the workbench's `PlayerBase` child: a base for raids and comfort). Give yours a
`$yourmod_` name and remove what it shouldn't bring. Repairs and upgrades use the **first** recipe for the item (`ObjectDB.GetRecipe`,
by `m_shared.m_name`), and a repair needs `min(station level, 4) >= m_minStationLevel`: a recipe that asks for level 5 can't be repaired.
`Recipe.m_noCraftOnlyUpgrade` gives "can be upgraded here, not made".

## A custom skill's levels go when the mod is missing once

`Skills.Load` keeps only skill types the game's enum knows (`IsSkillValid` is `Enum.IsDefined`); mods add theirs by patching it. One load
without the mod (a second mod-manager profile on the same character, the mod switched off, or failing after a game update) drops the
level, and the next save makes it final. Back it up in `Player.m_customData` (`yourmod.skill` = level and accumulator, written in a
`Player.Save` prefix) and put it back in a `Player.Load` postfix when the skill is missing. The skill's id is a hash of a fixed string:
never change it (a mod moving off SkillManager keeps `Math.Abs("English name".GetStableHashCode())`). Deaths lower it like the
game's skills, and with the game's skill cap on, it counts in the total. The `Skill.cs` template does the backup, the name and the
console commands.

## A status effect's copies share their lists

Each character gets `StatusEffect.Clone()`, a memberwise copy: a `List` or `Dictionary` field in your subclass is the same object for
everyone who has the effect. Make new ones in `Setup`. Effects aren't saved (only the guardian power is): one that must outlast a logout
keeps its remaining time in `m_customData`. The HUD shows only effects with an `m_icon` that aren't `m_hidden`. Name it after your mod
(its hash is `name.GetStableHashCode()`), and on unload take it off every character you own, then out of `ObjectDB`. The
`StatusEffects.cs` template does the registering, the unloading and the hand-over to a reloaded copy. `Setup` runs again when an effect
is put back after a reload: skip one-time things (a drink drunk, a "lit" message sent) while `StatusEffects.PuttingBack`, and carry state
that isn't time (how drunk, how far burned) with `StatusEffects.ICarry`.

## Scaling a hit leaves its untyped damage alone

`HitData.ApplyModifier(x)` scales every damage type but untyped (`m_damage.m_damage`): Quad's Dungeons' untyped shield-break blow took 48%
of a boss's health instead of 8%. Scale a whole hit with `hit.m_damage.Modify(x)`, or scale `m_damage.m_damage` too.

## Big jobs on a timer stutter

Work done every few seconds all in one frame is a hitch players feel (mods on the same timer land in the same frame). Real ones, measured
with `lag`: a plan re-read from its file with every piece searched for every 5 s (130-300 ms), a search of the world's saved objects for
ships every second (40 ms), a scene search for beacons every 2 s (20-25 ms), a colony scan filtering every piece for each question
(45-65 ms every 8 s). So:
- **Your own list, not a scene search**: `FindObjectsByType` / `FindObjectsOfType` / `Resources.FindObjectsOfTypeAll` walk every loaded
  object of the kind (~10 ms in a big base). Objects add themselves in `Awake`, leave in `OnDestroy`; the game's through a postfix on
  their `Awake` (`templates/Registry.cs`, `Live<T>`). Once at load is fine.
- **Read once per pass** (the pieces, the chests and what is in them, a file until it changes) and answer every question from that;
  **stop at the first answer** for "is there any".
- **A slice per frame** for what must be big, resting a few seconds between passes (`templates/Registry.cs`, `Sliced`): the world's
  iterative search is one call a frame, never a loop until done. Check a ZDO kept from an earlier frame again (`IsValid()`,
  `GetPrefab()`): removed ones are reused for new objects.
- **Measure** with `lag 20` (and `lag 30 mod=YourMod`); `modcheck` flags scene and world searches reachable from `Update`.

## Name everything after your mod

Prefab names, RPC names, ZDO keys, `m_customData` keys, `$` localization keys, console and Claude Tools commands, asset bundle names, the
Harmony id and the DLL's assembly name all share one space with every other mod. A plain name (`Lantern`, `check`, `"level"`) collides sooner or later: the later one
replaces the earlier, or the game throws. Start each with your mod's name (`yourmod_lantern`, `YourMod_RequestTake`, `"YourMod.level"`).

## Check default keys against the game's

The game reads its own keys whatever a mod does: V auto-pickup, X sit, C walk, Q auto-run, G radial menu, F forsaken power, R hide,
T emotes, E use, Tab, M, 1-8. A mod default on one of these does both actions. Pick a key the game doesn't use. Unbinding the game's action
(and telling the player) is only for a key your mod uses everywhere; for a key used in one situation (aiming at your object), pick a free one.

## Dedicated servers draw nothing

No player, no camera, nothing rendered: guard visual code, and wrap the visual part of registering a piece in try/catch so a material
problem never stops the piece being registered (the host would delete the placed ones).

## "Is this chest open?" only works on its owner's game

`Container.IsInUse()` reads a flag only the container's **owner** sets. On anyone else's game it says "free" while another player has
the chest open, and that game's copy of the contents can be a second old. Before taking from or putting into a container that isn't
yours: read the saved flag (`zdo.GetInt(ZDOVars.s_inUse) == 1`: an int, not a bool), skip it if set, then `ClaimOwnership()` and
**reload it** (`Container.Load`, private) before touching items. Without the reload, your change and the other player's overwrite each
other (whichever copy is newer wins): items duplicated or lost. `BepInEx/claude/templates/ContainerAccess.cs` does all of this (and keeps
to built chests: see "A Container isn't always a chest" in **valheim-compat**).

## Recovery data must live in the world

Data kept only on a player's character (`Player.m_customData`) is saved when they log out and every 30 minutes: while they're offline
it can't be updated, and a crash loses the last half hour. Something another player's game can need (a companion's gear when it falls at
a shared base) must be kept in the world: on the object itself if it survives, or on the tombstone.

## A piece that holds items needs what a chest has

A chest's `Container` brings a lot for free: ward checks (`PrivateArea.CheckAccess`), refusing to be torn down while it holds something,
dropping its contents when it's destroyed, and one player at a time. A piece that keeps items some other way (a drying rack, a display,
a machine) must do each of these itself, or players lose what's in it and anyone can take from a warded base. And the items must be
given out **by the piece's owner**, through an RPC (see **valheim-mod-start**), never by whoever clicked it.

## Undo temporary changes in a finalizer

A prefix that changes something for the game's method (a bigger inventory height, a cheaper stamina cost) and a postfix that puts it back
leaks when the method throws: the postfix never runs. Put it back in a `[HarmonyFinalizer]`, which always runs (as the popular inventory
mods do).

## Never UnpatchAll()

`harmony.UnpatchAll()` with no id removes **every mod's** patches, not only yours: when your mod unloads (a hot reload, an update), every
other mod stops working. Use `_harmony.UnpatchSelf()`. (One popular mod gets this wrong; `modcheck` finds it.)

## Keep what you can't load

The big inventory mods back up the player's items in `Player.m_customData` (with a version number), keep items whose prefab is missing as
saved data instead of deleting them (the mod that adds them may come back), and move them to the tombstone on death. Throw nothing
away you can't read.

## Clean up for hot reload

Everything a mod starts must stop in `OnDestroy`: patches (`UnpatchSelf`), file watchers (`Dispose`), coroutines and objects it made,
commands and RPCs it registered (registering an RPC name twice throws, so remove yours first). The game never unloads a mod's old copy, so
its **static fields live for the whole session**: set big static caches to null in `OnDestroy`, or every reload keeps another copy in
memory. Libraries that patch with their own Harmony id (blaxxun-boop's and Azumatt's managers: from a static constructor, under one id
shared by every mod that carries them) can't be undone: mark such a mod as needing a restart. Never patch .NET's own methods
(`Enum.GetValues`, `Type.GetCustomAttributes`): every mod and the game go through them.

A mod that registers pieces, items, creatures, recipes or status effects, or changes inventory sizes, can't be reloaded safely in a world:
its reload level is `menu` (`modcheck` shows it, and Claude Tools' `reload` refuses it in a world). Reload it at the main menu, or restart.
ScriptEngine's F6 reloads **every** mod in `BepInEx/scripts` at once with no such check, so one mod's problem hits a player who only
updated another.

## Test with a real restart

Before releasing a mod that registers prefabs or changes the player: quit fully, start again, check `log` for `Missing prefab hash` and
`invalid positioned items`, and look at the things themselves.
