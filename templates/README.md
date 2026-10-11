# Templates

Code to copy into a mod (change the namespace). Each one is the version our own mods run, after the bugs in the skills were fixed.

| File | What it does |
|---|---|
| `ServerSettings.cs` | Settings the server decides in multiplayer: mark gameplay settings with `Add`, and a player joining a server with the mod plays by the server's values (their config file keeps their own). No library to install; uses the game's `ZRoutedRpc`. For more (locking settings for admins, a version check), use ServerSync instead: https://github.com/blaxxun-boop/ServerSync |
| `GameUi.cs` | Windows in the game's own look (on/off switches, number steppers, sliders, the game's text box, live bars and more; see its header), cloned from the inventory screen: wood panel, sunken boxes, buttons, the braided title line, the crafting screen's tabs, the inventory's own item cells (icons, durability, quality, tooltips, clicks), compact item chips with tooltips, progress bars, icons, a scrolling list with the game's scrollbar and scroll speed, a place on the HUD, the game's fonts and colours, a canvas that follows the player's interface scale, and the input patches every window needs (no walking or zooming underneath it, a free cursor, Escape closes only it). Its comment shows a whole window. The Recycler's and the Bounty Board's windows are built with it. |
| `PieceReload.cs` | Lets a mod with build pieces be reloaded while you play (`reload YourMod`) without losing pieces or leaving placed ones on the old code: the piece stays registered across the reload, and pieces standing in the world get the new copy's components. Pieces only: for items, add `ItemReload.cs`. |
| `ItemReload.cs` | The same for a mod's items: they stay registered and alive across the reload, the new copy replaces them by name in ZNetScene and ObjectDB, and the items in the player's bag, loaded chests and on the ground get the new copy's prefab. modcheck gives `world` only to a mod that adds items to ObjectDB through this class alone and calls its `Repoint`. |
| `Translations.cs` | Words in the player's language through the game's own translations: `translations/English.json` and one file per language built into the DLL, `T("key", args)` in code and `"$key"` in the game's own fields, switching with the game's Language setting, players' own corrections in `BepInEx/config/translations/<GUID>/`. No library, no patches; hot-reloads. The **valheim-mod-start** skill ("Words in the player's language") has the rules. |
| `ContainerAccess.cs` | Taking from or putting into chests the player didn't open, safely in multiplayer: only built chests (not graves, carts or creatures), not one someone has open, ward and lock respected, taken over and reloaded before touching it. |

**`project/`** is a whole mod project to start from: copy the folder to where you keep your mods (not inside the game's folder),
rename `mods/ExampleMod`, and build. It has the game references (`mods/Directory.Build.props`), shared code compiled into each mod
(`mods/Shared`: the AI disclosure Thunderstore asks for, and `Translations.cs` for any mod with a `translations/English.json`), an
example mod with its words in three languages, and `publish.ps1` for sharing through the mod manager (F7). Its `README.md` walks
through it. The same folder is `template/` in https://github.com/HardHeadHackerHead/valheim-mod-manager.
