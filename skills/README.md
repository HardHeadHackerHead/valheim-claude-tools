# Skills for making Valheim mods

Skills for [Claude Code](https://claude.com/claude-code): each folder is one skill (`SKILL.md`), and Claude Code uses the right one by
itself when the work calls for it. Copy the folders into your mod project's `.claude/skills` (or `~/.claude/skills` for every project).
The Claude Tools mod also writes them to `BepInEx/claude/.claude/skills`, so Claude Code started in `BepInEx/claude` has them already.

| Skill | Use it to |
|---|---|
| [valheim-mod-start](valheim-mod-start/SKILL.md) | Start a mod from nothing, build it, load it into the running game and see it work; build it the way the most-downloaded mods are built (settings, multiplayer, words in the player's language, shared code, names that are yours). |
| [valheim-game-code](valheim-game-code/SKILL.md) | Look up the game's real code (types, methods, fields, who calls what) instead of guessing names. |
| [valheim-compat](valheim-compat/SKILL.md) | Work alongside other mods: which popular mods change the same things, reading their code, integrating with them. |
| [valheim-look](valheim-look/SKILL.md) | Look and sound like part of the game: windows, buttons, fonts, HUD messages, 3D models, materials, effects, page pictures. |
| [valheim-pitfalls](valheim-pitfalls/SKILL.md) | Avoid the mistakes that cost players items and buildings: registering pieces and items, inventories, saved data, death, chests, multiplayer. |
| [valheim-prerelease](valheim-prerelease/SKILL.md) | Check a mod before a release: the automatic check, a real restart, multiplayer and a dedicated server, clashes, the README. |
| [valheim-fix-errors](valheim-fix-errors/SKILL.md) | Find out from the BepInEx log why the game or a mod is broken, which mod and patch caused it, and fix or report it. |
| [valheim-thunderstore](valheim-thunderstore/SKILL.md) | Release or update a mod on Thunderstore: the package, manifest, icon, README, the rules, being found. |

The skills use Claude Tools' commands (`modcheck`, `game`, `who`, `clashes`, pictures...) when the mod is installed, and modkit (the same
commands on the command line) when the game is closed. Every rule in them comes from a real bug in [our mods](https://github.com/HardHeadHackerHead/valheim-mods).
