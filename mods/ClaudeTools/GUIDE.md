# Claude Tools: seeing and helping with the player's game

You are working with a player who has the **Claude Tools** mod. It gives you a **request mailbox**: you write commands into a text file,
the game carries them out, and writes back what happened. Pictures are saved as files you can read. Cameras are separate, and commands only
do what `help` lists. Those that act as the player (`screen click`, `type`, `use`, `key`) only work where the game allows cheats: ask first.

This is the general guide. A game with its own Claude Tools adapter (ClaudeTools.Valheim, for Valheim) writes its own guide here instead,
with its commands; `help` always lists every command there is, the game's own and other mods' too.

This folder is `BepInEx/claude` in the BepInEx folder the game uses: `<game>/BepInEx` when BepInEx was installed by hand, or the profile's
`BepInEx` folder with r2modman, Gale or Thunderstore Mod Manager.

| Path | What it is |
|---|---|
| `requests/` | Write `<name>.txt` here. The game renames it `<name>.taken`, runs it, and writes `<name>.done.json`. |
| `shots/` | Pictures: `shot`, `view`, `orbit`, `top`, `render`, and the player's F12 key. `latest.png` is the newest; screenshots have a `.json` beside them saying where the player stood and looked. |
| `library/` | The mod library: popular mods for this game and what they patch, when the game's adapter names its Thunderstore community. |

## Before you start

- **Ask the player** before looking at or doing things in their game, and tell them what you are about to do.
- Requests only run when `AllowRequests = true` in `BepInEx/config/com.quad.claudetools.cfg` (the player switches it on) and the game is
  running. Most commands need the player **in a world** (as the game's adapter tells; without one, always); `help`, `mods`, `config`,
  `log`, `errors`, `waitfor`, `wait`, `reload`, `devmods`, `screen` and the mod-maker commands also run from the main menu. If a request
  stays as `.txt`, one of those is not true: ask the player.
- Requests run one at a time, in name order, picked up within a moment of being written. Put a whole task in one request rather than one
  command per request. Wait for `<name>.done.json`, then read it (and any pictures it names).
- Run `help` first in a new session: it lists every command, with the mod that adds it.

## Writing a request

One command per line; `#` starts a comment. Example `requests/look_around.txt`:

```
mods
shot 1280
orbit here 20 30 4
```

The answer, `requests/look_around.done.json`:

```json
{ "request": "look_around.txt", "outputs": [ {"mods": [...]}, {"shot": ".../shots/....png"}, ... ], "errors": [], "ok": true }
```

Each command adds its result to `outputs`; anything that went wrong is in `errors` (the rest still runs).

## Places

Camera commands measure from a **place**: `world` (world coordinates), `here` (the player, facing where they face), `look` (the spot the
player is looking at), or a name another mod knows. Positions in a place are metres: x right, y up, z forward (Unity), turned with the place.

## Built-in commands

| Command | What it does |
|---|---|
| `help` | Every command, with the mod that adds it. |
| `shot [width]` | What the player sees, saved to `shots/`. |
| `view <place> <x> <y> <z> <yaw> <pitch> [fov] [w] [h]` | A separate camera at a spot measured from a place. |
| `orbit <place> [radius] [pitch] [count] [w] [h]` | Pictures from all round a place. |
| `top <place> [size] [width]` | Straight down over a place (an orthographic map of `size` metres). |
| `render <prefab> [yaw=25] [pitch=12] [views=1\|4] [focus=x,y,z] [dist=m] [fov=30] [size=WxH] [bg=sky\|dark\|clear]` | A picture of a prefab on its own, built out of sight. |
| `inspect <prefab> [depth=3]` | What an object is made of: parts, colliders, components, meshes, materials. |
| `mods` | The mods running, with versions. |
| `config <mod> [section] [key]` | A mod's settings. `config set <mod> <section> <key> <value>` changes one, only if the player allows it (`AllowConfigChanges`). |
| `log [lines] [text]` | The last lines of the BepInEx log, optionally only those containing some text. |
| `errors` | The new errors and exceptions in the log since you last asked, with their stack traces. Run it after every rebuild. |
| `logtrace <warning\|log\|error> <on\|off>` | Add stack traces to the game's warnings (or log lines, or errors), to find what keeps logging one. |
| `lag [seconds=20] [spike=50] [game=on\|off] [top=20]` | What makes the game stutter: times every mod's Update methods, coroutines and patches, then reports the slow frames. |
| `screen list [text]` / `texts` / `overlaps` / `click <words> [n]` / `type <text>` / `submit` / `slider <n> <value>` | The windows on screen: every button, text box and slider with where it is; which overlap; click, type, move a slider (acting only where cheats are allowed). `use`, `key` and `pause` need the game's adapter. |
| `wait <seconds>` | Pause, so things happen before the next picture. |
| `waitfor <mod> [version] [seconds=30]` | Wait until a mod (that version) is loaded: after a rebuild, before looking at it. |
| `reload <mod> [force]` | Unload a mod from `BepInEx/scripts` and load its new build. `reload ClaudeTools` reloads Claude Tools itself (and its game's adapter with it). |
| `devmods` | The mods in `BepInEx/scripts`: loaded or not, their reload level and why, and whether a newer build is waiting. |

## For mod makers

| Command | What it does |
|---|---|
| `modcheck <mod \| path.dll>` | The pre-release check (its rules come from Valheim mods; elsewhere it still finds patches that are never undone and scene searches every frame). |
| `game find <text>` / `game <Type>` / `game <Type.Member>` | The game's real code: signatures, who calls a method, who changes a field. Never guess names. |
| `who <Type.Method>` | Which mods (installed, and in the library) patch a game method, and how. |
| `clashes [mod] [all]` | Installed mods that change the same methods as each other or as library mods. |
| `patches [text]` | Every patch in the installed mods, by game method, with what each one can do. |
| `systems` | The game systems (groups of methods doing one job), when the game's adapter has them. |
| `gameupdate` | After a game update: methods gone, changed signature or changed code, and which mods patch them. |
| `library` / `library get <Namespace-Name>` / `library drop <Namespace-Name>` / `library update` | The mod library (Thunderstore). |

## Adding commands from a mod

A mod adds commands without referencing Claude Tools (so it works without it), by finding the Claude Tools plugin in BepInEx's plugin list
(`Chainloader.PluginInfos["com.quad.claudetools"].Instance`) and calling its public static methods by reflection:

```csharp
RegisterCommand(string owner, string name, string usage, Func<string[], Action<JObject>, Action<string>, IEnumerator> run)
RegisterFrame(string owner, Func<string, float[]> resolve)   // a named place: returns { x, y, z, yaw } or null
UnregisterAll(string owner)
```

`run` gets the words of the line (`args[0]` is the command), reports results with `output(JObject)` and problems with `error(string)`, and
returns `null` when done or an `IEnumerator` to take its time (it runs as a coroutine). Register again whenever a new Claude Tools appears
(it may be hot-reloaded); call `UnregisterAll` when your mod unloads.
