# Getting started: making Valheim mods with Claude Code

How we set up and work on our own mods, step by step. About half an hour the first time.

## 1. The game side

1. **Valheim with BepInEx.** By hand: [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) unzipped
   into the game folder. Or with r2modman / Thunderstore Mod Manager (then "the BepInEx folder" below is the profile's: Settings → Browse
   profile folder).
2. **Claude Tools**, the mod: with our [mod manager](https://github.com/HardHeadHackerHead/valheim-mod-manager) (F7 → Browse → Claude
   Tools → Install), or `ClaudeTools.dll` and `.pdb` from [dist/](../dist/) into `BepInEx/plugins`.
3. Start the game once, then in `BepInEx/config/com.quad.claudetools.cfg` set **`AllowRequests = true`** (F7 → Mod settings does it too).
   That lets Claude Code see and test the game while you work; switch it off when you don't.
4. **The game's console**: Settings → Gameplay → "Enable console". F5 opens it; `claude <command>` runs any Claude Tools command yourself.
5. A **test world** of its own (single player): commands that act for you (giving items, moving you, pressing keys) work only where the
   game allows cheats, and you'll want to break things without losing a real base.

## 2. The developer side

1. The [.NET SDK](https://dotnet.microsoft.com/download), 8 or later (it builds for the game's .NET Framework 4.8 by itself).
2. [Claude Code](https://claude.com/claude-code).
3. Optional, for reading the game's code yourself: `dotnet tool install -g ilspycmd`.
4. Set the environment variable **`VALHEIM_DIR`** to the game folder (and **`BEPINEX_DIR`** to the profile's `BepInEx` folder if a mod
   manager keeps it there). On Windows: `setx VALHEIM_DIR "D:\SteamLibrary\steamapps\common\Valheim"`, then open a new terminal.

## 3. Your mod project

1. Copy [templates/project](../templates/project/) to where you keep your code (not inside the game's folder) and make it a git repo.
   Once Claude Tools has run, the same folder is in `BepInEx/claude/templates/project`.
2. Fill in the two paths at the top of its `CLAUDE.md`.
3. Start Claude Code in it. Its `.claude/settings.json` switches on the **Valheim skills** (the `valheim-modding` plugin from this repo)
   **for this project only**: accept when Claude Code asks to install it. Your other projects don't get them.
4. Ask for what you want to make: *"make a mod that adds a lantern you can build with the hammer"*. The **valheim-mod-start** skill takes
   it from there: renaming the example mod, the plugin, building, loading it into the game.

Already have a mod repo? Switch the skills on for it alone, from inside it:

```
claude plugin marketplace add HardHeadHackerHead/valheim-claude-tools --scope project
claude plugin install valheim-modding@valheim-claude-tools --scope project
```

(`--scope local` instead keeps it to your machine, out of the repo's shared settings.)

Working on several repos at once (say your mods and a library of your own)? Let sessions in one reach the others without asking, in
that project's `.claude/settings.local.json`:

```json
{ "permissions": { "additionalDirectories": ["D:\\code\\my-other-repo"] } }
```

## 4. The loop

With the game running and you in your test world:

1. **Ask** Claude Code for the change.
2. **It looks up the game's real code** first (`game find`, `game <Type.Method>`) and which popular mods touch the same thing (`who`,
   `clashes`), instead of guessing.
3. **It builds**: `dotnet build mods/<Mod> -c Release` puts the DLL in `BepInEx/scripts`, and `reload <Mod>` swaps it in while you play
   (when that's safe for what the mod does; otherwise it says to go to the main menu or restart).
4. **It checks**: `waitfor <Mod> <version>`, `errors`, then tests it as you would: pictures (`shot`, `render`), windows (`screen list`,
   `click`, `overlaps`), `inventory` and `chests` before and after to measure what really changed, `language German` to see the words fit.
5. **You play it** and say what's wrong. Repeat.

Before a release: `modcheck <Mod>` (the mistakes that have lost players' things), a real game restart, multiplayer if it matters, the
**valheim-prerelease** skill's list, then `publish.ps1` for the mod manager and the **valheim-thunderstore** skill for Thunderstore.

## 5. Habits that save you

- **Raise `Version` with every build you test.** `waitfor` can only prove the new copy loaded if the version changed.
- **Read `errors` after every reload.** A mod that throws in `Awake` looks like it did nothing.
- **Keep translations from the start** (`templates/Translations.cs`; the project template has it): adding them later means finding every
  word again.
- **Things only a restart shows** (pieces registered too late, saved data read wrong): restart before calling it done. The
  **valheim-pitfalls** skill lists them.
- **Ask before acting in a real world.** Claude Tools refuses cheats outside single player or the host for a reason.
