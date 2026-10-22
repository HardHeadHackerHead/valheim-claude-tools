# Valheim modding pitfalls

Things that cost us lost items or lost buildings. Check new mods against them.

## Register custom pieces from the scene, and from both Awake hooks

A placed piece is saved by its prefab name. When its zone loads on the **host** and no prefab with that name is registered in
`ZNetScene`, the game logs `Missing prefab hash: N` and then `Destroyed invalid prefab ZDO`, and the piece is gone for good.

At world load `ZNetScene.Awake` can run before `ObjectDB.Awake`, so a register step that starts with
`if (ObjectDB.instance == null) return;` does nothing on a fresh launch. It only works after a hot reload, which hides the bug while
you are developing. So:

- Get the hammer and the cost items with `scene.GetPrefab("Hammer")` (items are in `ZNetScene` too), not `ObjectDB.instance`.
- Register from a `ZNetScene.Awake` postfix **and** an `ObjectDB.Awake` postfix (when `ZNetScene.instance` exists), and in the plugin's
  `Awake` for a hot reload while in a world.
- On unload, remove the `m_namedPrefabs` entry only if it still points at your own prefab, so unloading never takes out another
  copy's registration. (ScriptEngine's reload destroys the old copies first and adds the new ones a frame later, so the old
  `OnDestroy` runs before the new `Awake`.)
- Take the piece off the build-menu list you put it on, and keep that `PieceTable` from when you registered: at the main menu (a reload
  there) `ZNetScene.instance` and `ObjectDB.instance` can't give you the hammer, but its list lives on (it belongs to the hammer prefab).
  A destroyed piece left on it makes `Player.UpdateKnownRecipesList` throw on every change to the player's bag, which falls between
  `DoCrafting`'s `AddItem` and `ConsumeResources`, so every craft is free (BirdTrap, SlotMachine, Ziplines, Cigars and Arena did this
  until 2026-10-11). Also sweep destroyed entries (`p == null`) off the list when you register.

See `mods/SlotMachine/Plugin.cs` (`Register`, `Unregister` and the two patches at the bottom).

## A server without the mod deletes what a player builds with it

The same deletion happens when the **server** lacks the mod: a player who has Jet Skis, on a vanilla dedicated server or with a host who
never installed it, could build a jet ski that the server deletes the moment its area loads, saddlebags and all (checked in
`ZNetScene.CreateObjects`: only the server destroys unknown prefabs, so a player *without* the mod on a server that has it loses nothing).
`Shared/ServerSettings.cs` asks the server over its own connection whether it has the mod; `Synced.NeedsServer(prefab)` keeps such pieces
(and `NeedsServer(recipe)` such items) out of the menus until it answers yes. Don't trust a routed RPC for "the server has the mod": its
sender id is whatever the packet says. Jet Skis does this (`Things.Register`, and its "isn't installed on this server" message).

## The player's inventory height is reset on every spawn

Since the trader started selling inventory rows, `Player.OnSpawned` calls `Player.SetInventorySize(rows)` with the row count saved in the
character's `invrows` key. That sets the inventory height and then calls `Humanoid.DropInvalidItems`, which drops everything below it. As
with the pieces, a hot reload never spawns you, so it looks fine while developing.

Don't take `SetInventorySize` over (a prefix returning false), and don't set an exact height in `Player.Load`: GearSlots did both, and with
ValheimPlus' extra rows, BetterArchery's quiver or the slot mods (ExtraSlots, EquipmentAndQuickSlots, AzuExtendedPlayerInventory) items were
dropped at every spawn (review 2026-10). A postfix is too late: the items are already on the ground. What works (EquipmentAndQuickSlots'
pattern): a non-skipping prefix on `SetInventorySize`; a `Priority.First` prefix on `DropInvalidItems` that moves your rows' items below the
height the game and other mods just set and raises the height to fit; a `Priority.Last` postfix that raises it again if another mod lowered
it; your rows' position saved in `Player.m_customData`; `Max(height, ...)`, never an exact height. And stand down when a slot mod that uses
the same cells is installed.

## Never save a ZDOID

Every time a world loads, the game gives each object a new `ZDOID` (`ZDO.Load` sets it from a counter that starts again at 0). A
`ZDOID` is only good for the current session: one saved in a ZDO or config points at some unrelated object after a restart. To refer to an
object across restarts, store your own random id on it (PortalHub's `dh_pid` in `mods/PortalHub/Plugin.Net.cs`), or use the game's
connections (`ZDO.SetConnection`), which it re-links by hash when the world loads.

Not even in memory across worlds: a `ZDOID` works out its hash (and its `UserID`) from a table the game empties each time you join a world
(`ZDOID.Reset`). A dictionary keyed by ZDOIDs that lives on through a trip to the main menu can't find its old keys, and looking them up can
throw: QualityOfLife's ship pins threw every second after a rejoin. Key such collections by `(zdoid.UserID, zdoid.ID)` taken when you add
them, or empty them when a new world starts.

## Fill a tombstone with the game's own move

A tombstone's (or cargo crate's) container is only a few slots big until the game fills it: `Inventory.MoveInventoryToGrave(original)`
resizes the grave to the bag it empties and moves everything at once. Adding items one by one with `AddItem` fits only the small default
size, and the rest is lost (AICompanion 0.3.0 lost a fallen companion's armour this way). The move also skips items flagged `m_equipped`:
unequip and clear the flag first.

## A container saves only when items come and go

A `Container` writes its inventory to its ZDO when an item is added or removed (`Inventory.m_onChanged`). Changing an item in place (its
quality after an upgrade, its durability after a repair or wear) does not save it, and the change is lost the next time the object is made
from its ZDO (its area reloading, a hot reload). After changing items in place, invoke `inventory.m_onChanged` on the game that owns it.

## Check every default key against the game's

Valheim reads its own keys whatever a mod does: GearSlots' quick slot 4 on V also flipped the game's auto-pickup toggle (V), and the
player only saw it as "auto-pickup stopped working". The game's defaults are in `ZInput` (in `assembly_utils.dll`, `AddButton(...)`):
V auto-pickup, X sit, C walk, Q auto-run and previous tab, G radial menu, F forsaken power, R hide, T emotes, E use, Tab, M, digits
1-8. Mods that use one of these call `GameKeys.Free` (in GearSlots, QualityOfLife, BuildOrders), which unbinds the game's action once
(saved in the game's own settings, never the essential ones) and tells the player, so they can give it another key in Settings, Controls.

## Test with a real restart

These bugs only show on a fresh launch. Before releasing a mod that registers prefabs or changes the player, quit the game fully,
start it again, and check the log for `Missing prefab hash` and `invalid positioned items`.

## Count a recipe's cost as the game does

Since the battle idols arrived, a recipe's `m_resources` also lists items marked `m_upgraderResource` (the flint axe lists 1
`Upgrader1Weapon`). The game counts a requirement only when that flag matches the station's `m_upgrader` (see
`Player.HaveRequirementItems`, `ConsumeResources`): at a workbench or forge the idols are left out, at an upgrader station only they count.
Code that reads `m_resources` directly thinks every recipe needs an idol, so nothing can be made (AICompanion 0.5.0's companion never
crafted or upgraded). Filter them out as AICompanion's `Upgrades.Needs` does.

## A container reloads whenever anything in its ZDO changes

`Container.Load` (called from `CheckForChanges` every update) rebuilds the inventory from the saved `s_items` whenever the ZDO's
`DataRevision` differs from the last one it saw, and any `ZDO.Set` on that object bumps the revision, not only the items. An object that
keeps a Container and also writes other values to its ZDO often (AICompanion's companion: status, stamina, skills several times a
second) gets its inventory replaced by fresh copies of the last saved items over and over: changes made in place since the last save
(wear, repairs) are lost, and anything holding a reference to an item (what a Humanoid has equipped) points at an old copy. AICompanion
skips the reload on the game that owns the companion, after loading once when it takes ownership (`Container_Load_Companion`).

## A container's saved contents are bytes, not a string

A `Container` saves its inventory into its ZDO as a **byte array** (`Container.Save` writes `ZDOVars.s_items` with `Set(..., byte[])`).
Read with `zdo.GetString(ZDOVars.s_items)` it is always `""`, so any check that asks "is this chest empty?" (or "is its loot still
untouched?") from the string always says yes. Quad's Dungeons did exactly that when cleaning up chests that weren't loaded: it destroyed
them with whatever players had left inside, and a hash of the string to spot untouched loot would have deleted touched loot on every
reset. Read the contents with `zdo.GetByteArray(ZDOVars.s_items)` (null or empty: nothing saved), or load them into an `Inventory`, and
test every "empty" or "untouched" check with a chest that holds something.

## Keep a moving object's ZDO small

The game sends a ZDO whole (every key) to each player near it whenever its revision changes, and any `Set` that changes a value bumps it,
as does every position change of a moving object (`ZDO.InternalSetPosition`). Each player gets at most about 10 KB of ZDOs per send
(`ZDOMan.SendZDOs`, 20 times a second), nearest first. AICompanion kept its journal, tally and missions on the companion's ZDO: 11-13 KB,
resent with every step, so other creatures' updates arrived seconds late in multiplayer (0.24.0 and older). Keep big or growing data off
anything that moves (AICompanion's `Book`: kept with its player's character, sent as small changes over routed RPCs and whole only when
asked), and save values that change all the time (skills, stamina) every few seconds rather than on every change.

## A removed ZDO key stays on every other game

`ZDO.RemoveInt` (and the other `Remove...`) only removes the key on this game. A ZDO arrives on other games through `ZDO.Deserialize`,
which only adds or overwrites keys (`ZDOExtraData.Add`); nothing is ever cleared there, nor on the server or for players who join later.
Parkour 0.1.0 ended a move by removing the pose keys from the player's ZDO, so other players saw them frozen in their last slide or climb.
Never remove a key others read: write an explicit "none" value (0, "", a version-tagged empty state) and treat it as nothing.

## What another player holds is in their ZDO, not their VisEquipment fields

`VisEquipment`'s `m_rightItem`, `m_leftItem` and the other `m_*Item` fields are only set on the game that owns the player (`SetRightItem`
and friends). On everyone else's game they stay 0: `UpdateEquipmentVisuals` reads the ZDO (`ZDOVars.s_rightItem`, `s_leftItem`...) into
locals. Quad's Guns 0.2.1 to 0.3.0 found other players' guns from `m_rightItem`, so no other player's shots, arm poses, dives or flame ever
showed, and it went unnoticed until a two-player test (fixed in 0.3.1). Read another player's equipment from their ZDO, and test anything
"other players see" with a second player.

## An attached player lets go on any input

`Player.SetControls` calls `AttachStop()` whenever an attached player (a chair, a bed, a zipline, a cart) moves, attacks, blocks, jumps
or crouches. Ziplines 0.3.0 let a rider tap W over a gorge and fall to their death. A mod that attaches the player somewhere dangerous
neutralises those inputs while attached (a `SetControls` prefix) and lets go on its own terms, and sets the player down safely (ground
under them, fall height reset) when it lets go for any other reason: a reload, a logout (the logout point is the hanging spot).

## "True" from the game's UseItem doesn't mean it took the item

`Fireplace.UseItem` returns true when the fire is full too (it only shows "can't add more"); other `UseItem`/`Interact` methods answer
"I handled it", not "I took it". FeedFromChests 1.8.0 counted that as fuel added and lost one wood per press on a full fire. Check the
station's state yourself (fuel against `m_maxFuel`, a free slot) before paying, or count the items before and after.

## Take items only once the owner confirms, or give them back

A station another player's game runs decides on its owner's game: your copy of it can be a moment old, and an RPC to an owner who has
just left or unloaded it is dropped without an answer. Taking the items first and sending them over (BirdTrap's bait, the cigar curer,
filling a fermenter run by another player) loses them whenever the owner refuses or never answers. Add one at a time and wait for the
station to change, or keep what you sent until the owner confirms, and give it back if no answer comes.

## Every prefix runs, and ScriptEngine ignores incompatibility

In the HarmonyX that BepInEx ships, a prefix returning false skips only the game's method: every other mod's prefixes still run. So a
prefix that pays, deposits or gives must take `bool __runOriginal`, return at once when it's false (another mod did the job), and usually
run at `Priority.Last`. Our chest crafting charged twice next to Adventure Backpacks, and FeedFromChests deposited smelter output twice
next to ValheimPlus, this way. ScriptEngine (how the mod manager loads our mods, from `BepInEx/scripts`) ignores `[BepInIncompatibility]`
and `[BepInDependency]`, and at `Awake` mods loaded later aren't in `Chainloader.PluginInfos` yet: detect other mods lazily, in code.

## Pay in full or not at all, and refuse early

The game gives the crafted item (`InventoryGui.DoCrafting` calls `AddItem` before `ConsumeResources`) and places the piece (`TryPlacePiece`
before `ConsumeResources`) before it charges, so a shortfall found in `ConsumeResources` is too late to refuse. Check and reserve in a
`DoCrafting` / `TryPlacePiece` prefix. "Any one of these" recipes (`m_requireOnlyOneIngredient`) pay through `DoCrafting`'s own
`RemoveItem`. Count the player's own items with `GetAllItems()` (other mods add their sources to `CountItems`). Never ignore what a
"take from chests" helper says it couldn't find.

## "Is this chest open?" only works on its owner's game

`Container.IsInUse()` reads a flag only the container's owner sets: on other games it says "free" while someone has it open, and their copy
of the contents can be a second old. Read the saved flag (`ZDOVars.s_inUse`) and skip it if set. AICompanion and QualityOfLife missed this.

## Don't take a chest from the player who owns it: ask the owner

Only the owner of a chest can change it. Claiming ownership of every chest in range and paying once the claim has settled (Craft From
Chests 1.5.1 to 1.5.3) fails when two players do it at the same time: each claim takes the chests from the other, so both players' totals
come and go every few seconds (Multi User Chest, which moves ownership to whoever opens a chest, made it worse). Chests the game owns are
changed directly; for any other chest send the owner an RPC to take the items out (`CFC_Take`), have it answer with what it really took,
hold that until the craft is paid and give back what is left (`CraftFromChests/ChestScanner.cs`). The owner serves two players one after the
other, so nothing is paid twice. Only a chest nobody owns (`ZDO.HasOwner()` false) is simply claimed.

## A Container isn't always a chest

Creatures (AICompanion's companions), carts, ships, tombstones and Adventure Backpacks' backpack proxy have a `Container` too. Mods that use
"every container nearby" must keep to built pieces (a `Piece` in the parent chain, no `Character`), or they spend a companion's arrows, a
grave's contents, or the backpack a second time.

A built piece with a Container isn't always storage either: the game's **Obliterator** burns what is in it, LedgerChest's Ledger Chest
sends on what goes in, and Arena's ring chests hold lent gear. AICompanion's companion took an empty Obliterator beside its bed as one of
its chests and filled it (0.22.2 and older). Mods that pick or fill chests by themselves skip them all through one rule,
`mods/Shared/SpecialContainers.cs` (by prefab name, the `Incinerator` component, or a ZDO key: `dh_arena_chest`, `dh_notstorage`). A mod
that clones `piece_chest_wood` for a piece that isn't storage must `DestroyImmediate` the Container on its prefab (Recycler, BountyBoard,
SlotMachine, BirdTrap do); one that keeps a Container sets `dh_notstorage` on its ZDO, or goes on the list.

## Recovery data must live in the world

`Player.m_customData` is saved on logout and every 30 minutes, and can't change while its player is offline. A companion's kept gear stored
only there and on the companion itself (destroyed when it dies) was lost when it fell on another player's game. Keep what other games may
need on an object that survives (the tombstone).

## Never put back an old snapshot of shared things

BuildOrders restored the ground under a removed plan from its saved copy, checking only the plan's own pieces: other buildings on the pad
lost their ground and fell. A zone's terrain also loads before its pieces, so a restore can see "nothing built" under a finished building.
Restore only what is still exactly as you left it, skip anything another piece stands on, and wait for `ZNetScene.IsAreaReady`.

## Rewards must not be gamed

Quitting mid-contest must not pay better than losing (Arena paid a full round). Items from lands whose boss isn't beaten must not leave a
contest. `m_value > 0` makes an item sellable at the trader (SkalTavern's mead was a coin machine). Identify players from the RPC sender,
not the payload, and remember new characters can claim per-character rewards (BountyBoard). Gameplay settings (payouts, rewards, ranges,
timers) are the server's in multiplayer (`mods/Shared/ServerSettings.cs`), or every player sets their own.

## The chest-crafting clash

CraftFromChests and BuildFromChests add chest items in `Inventory.CountItems`; ValheimPlus (craft from chest, off by default) and
AzuCraftyBoxes add them in `Player.HaveRequirementItems`/`HaveRequirements`. Installed together, chests count twice and crafts pay half. Stand
down when one of them is installed.

## Scaling a hit leaves its untyped damage alone

`HitData.ApplyModifier(x)` multiplies blunt, slash, pierce, chop, pickaxe, fire, frost, lightning, poison and spirit damage, but **not**
`m_damage.m_damage` (untyped damage). A mod that makes something tougher or weaker by scaling hits with `ApplyModifier` leaves any
untyped part at full strength. Quad's Dungeons made its bosses tougher this way, and its own blows are untyped: a shield break meant to
take 8% of a boss's health took 48%. Scale a whole hit with `hit.m_damage.Modify(x)` (or scale `m_damage.m_damage` as well), and test
with a hit that carries untyped damage.

## Big jobs on a timer stutter

A job that runs every few seconds and does all its work in one frame is a hitch players feel, however rare it looks in the code; mods
that start together check on the same second and land in the same frame. Measured with Claude Tools' `lag` in October 2026:
BuildOrders re-read a finished 566-piece plan from its file and searched for every piece every 5 s (130-300 ms each time);
QualityOfLife's ship pins searched every saved object in the world once per kind of ship every second (40 ms); a mod searched the scene
for its beacons every 2 s (20-25 ms); a colony mod filtered every piece of the base for each question its 8-second scan asked (45-65 ms).
So:
- **Keep your own list** instead of searching the scene (`FindObjectsByType`, `FindObjectsOfType`, `Resources.FindObjectsOfTypeAll` walk
  every loaded object of the kind, ~10 ms in a big base): your objects add themselves in `Awake` and leave in `OnDestroy`; the game's
  components through a postfix on their `Awake` (`templates/Registry.cs`, `Live<T>`). One search when the mod loads is fine.
- **Read once per pass**: a scan that asks many questions (is this in stock, which station makes it, what stands here) gathers the
  pieces and chest contents once and answers from that, and keeps what it read from a file until the file changes.
- **Stop at the first answer** when the question is "is there any" (`AnyBuilt`, not `BuiltPieces(...).Count > 0`).
- **A slice per frame** for what must be big: a millisecond of work each frame, then rest a few seconds before the next pass
  (`templates/Registry.cs`, `Sliced`). The world's iterative search (`ZDOMan.GetAllZDOsWithPrefabIterative`) is one call a frame, never
  a loop until it is done; and a sliced search started again the moment it ends still costs every frame for ever.
- **Check again what you kept from an earlier frame**: the game reuses a removed thing's ZDO for a new object, so test `IsValid()` and
  `GetPrefab()` before using one found a few frames ago.
- **Measure**: `lag 20` where the mod is busiest; `lag 30 mod=YourMod` shows which of its methods take the time. `modcheck` warns about
  scene and world searches it can reach from `Update` (`find-every-frame`, `world-zdo-scan`).

## Figures made from the Player prefab are dressed a frame later, and keep working every frame

A copy of the `Player` prefab used as scenery (a crowd, a shopkeeper, a statue) is dressed by its `VisEquipment` in the game's next
update (`MonoUpdaters` calls `CustomUpdate`), not by the `Set...Item` calls: the armour, the cape (with its MagicaCloth, built and running)
and what it holds don't exist yet when `Instantiate` returns. Arena switched the capes' cloth off right after making its crowd, found
nothing to switch off, and ran up to fifty cape simulations through every fight (October 2026; players saw their frame rate drop when a
fight began). After that, `VisEquipment` keeps re-colouring the body and hair every frame (a new material array each time). So: wait a
few frames after making the figure, then switch the cloth off and disable its `VisEquipment` (the clothes stay on), and turn off the
shadows of any lights it holds (a torch): set its `LightLod.m_shadowLod` false too, or the game turns them back on within 20 m.

## Game pieces used as scenery bring their fires' smoke and shadows

A fire piece (brazier, torch, fire pit) made as local scenery keeps its `SmokeSpawner` (a smoke cloud with physics every half second
within 64 m, the game capping all smoke at 100 and fading the oldest), its `CinderSpawner`, and lights whose `LightLod` gives them
shadows within 20 m: each shadowing point light draws everything round it six times. Arena's colosseum had some eighty fires and all
three running whenever a player was near (October 2026). Take the spawners off, set `LightLod.m_shadowLod` false and the light's shadows
to none (the flames and their light stay), and build a big set of pieces a few milliseconds a frame, not a fixed count a frame.

## Name everything after your mod

Prefabs, RPCs, ZDO and `m_customData` keys, `$` keys, commands and the Harmony id share one space with every other mod. ClaudeTools' `check`
command hid BuildOrders' `check` until it was renamed `modcheck`. Start every name with the mod's, and pick prefab names a player can find
(`prefabs <text>` in ClaudeTools lists them: a "war stone" was `BobWarstone`).

## Ship only what you mean to

`publish.ps1` builds every mod from the working folder, so uncommitted code ships (Arena 0.2.3 nearly went out with the screenshot tool).
Stash or commit first, and check the built DLL (strings are UTF-16). Debug commands that give, move or remove follow the game's cheat rule
(`CheatsAllowed()` in ClaudeTools) or stay out. F6 reloads every mod in `scripts`: test mods with `RESTART_REQUIRED.txt` with a full restart.

## A plugin that embeds a library must not name it in its own types

The dev loader (ScriptEngine, which is also how the F7 manager loads mods into `BepInEx/scripts`) calls `Assembly.GetTypes()` on a plugin
before any of its code runs. If the DLL carries a library as a resource and loads it from an `AssemblyResolve` handler set up in `Awake`
(Quad's Scripts embeds Jint), every type with a field, parameter or lambda that names the library fails to load at that moment, the runtime
**remembers the failure**, and the plugin's own start-up then dies with "type initializer threw" (found 2026-10-12 with Jint; a hot reload
cannot clear it, only a game restart). Make the plugin DLL a shell with no library types in any of its types: it hooks the resolver, loads a
second assembly (also an embedded resource, built from its own project with `DeployToGame=false` so it is never put in `scripts`) and calls
into it by reflection. See `mods/Scripts` (`Plugin.cs`, `Deps.cs`) and `mods/Scripts.Engine`. The same happens for a first run that lacked a
dependency: the failed lookup (`System.Buffers` for Jint) is cached for the session, so add every dependency before the first test.

## After a hot reload, GetComponent can't see your old components

A hot reload loads the mod as a new assembly, so `GetComponent<MyThing>()` asks for the new assembly's type and never finds the copies
the old one added to game objects. A mod that "finds its component or adds one" then adds a second copy on every reload, and the old
ones keep doing what they did. Ginnungagap (2026-10-15) stacked a fresh rock underside under every piece of land per reload, and the
stale ones showed as slivers the player took for a new bug. Find your own objects by name (or keep a list), destroy any whose component
isn't the current type, and remove everything you added in `OnDestroy` when the mod unloads.
