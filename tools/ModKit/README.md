# modkit

Claude Tools' commands for mod makers, on the command line, **with the game closed**: the pre-release check, which mods patch what, the
game's real code, the mod library and getting a mod ready for Thunderstore. It is built from the same source files as the in-game
commands (`Scan.cs`, `Check.cs`, `Compat.cs`, `GameIndex.cs`), so it gives the same answers.

## Getting it

Claude Tools for Valheim carries modkit inside its DLL (ClaudeTools.Valheim.dll) and writes it to **`BepInEx/claude/modkit`** the first
time the game runs with the mod. There are two builds of it there:

```
BepInEx/claude/modkit/modkit.exe modcheck MyMod            (Windows: nothing to install)
dotnet BepInEx/claude/modkit/modkit.dll modcheck MyMod     (Linux, Mac: needs the .NET 8 runtime or later)
```

It uses the Mono.Cecil and Newtonsoft.Json that come with the game and BepInEx, so it carries no copies of its own.

## Commands

| Command | What it does |
|---|---|
| `modcheck <mod name \| GUID \| path\to\Mod.dll>` | The pre-release check (also `check`): the mistakes that lose players' things or break other mods, each with why and how to fix it. It reads the DLL, never runs it. |
| `who <Type.Method>` | Which mods, installed and popular, patch a game method. `who system <name>` does a whole system. |
| `clashes [mod] [all]` | Installed mods that clash with each other or with popular mods, most likely first. |
| `patches [text]` | Every patch in the installed mods, by game method. |
| `scan <path\to\Mod.dll>` | One DLL's plugins and patches. |
| `systems` | The game's systems (groups of methods that do one job). |
| `game find <text>`, `game <Type>`, `game <Type.Member>` | The game's real code: signatures, who calls a method, who changes a field. |
| `gameupdate` | What changed in the game since the last version seen, and which mods patch it. |
| `library` | The mod library: popular mods and what they patch. |
| `library get <Namespace-Name> [...]` | Download a Thunderstore mod's DLL (code only, never run) into the library, to read. |
| `library drop <Namespace-Name> [...]` | Remove one. |
| `library update [count]` | Fetch the most-downloaded mods (10 by default). |
| `package <mod folder> [--namespace Team]` | Get a mod ready for Thunderstore: writes its `thunderstore.toml` for the Thunderstore CLI (tcli) and checks everything an upload needs. The first time, `--namespace` is your Thunderstore team. |
| `package check <built .zip>` | Check the zip `tcli build` made, before testing and publishing it. |
| `help` | Every command. |

**Options:** `--json` gives the raw answer. `--game valheim|mycopunk|emberville` says which game (by default the one modkit is inside,
else Valheim); `--gamedir <folder>` (or `--valheim <folder>`) is the game folder and `--bepinex <folder>` is BepInEx, for when a mod
manager keeps it in a profile folder. For Mycopunk and Emberville, `package` isn't there (its checks are Thunderstore's Valheim rules),
`systems` is empty and the built-in patch map is left out (it is Valheim's); Emberville has no mod library yet.

What the top 100 Thunderstore mods for Valheim patch is built in (`mods/ClaudeTools.Valheim/Shipped/patchmap.json.gz`, made by [PatchMap](../PatchMap/)),
so `who` and `clashes` know about popular mods even with nothing downloaded. The library lives in `BepInEx/claude/library`, the same one
the game uses.

## How it finds the game

1. `--gamedir <folder>` (`--valheim`), or the game's environment variable: `VALHEIM_DIR`, `MYCOPUNK_DIR`, `EMBERVILLE_DIR`.
2. A folder above modkit itself (it usually lives inside the game, in `BepInEx/claude/modkit`).
3. Steam's usual places on Windows, Linux (Flatpak too) and Mac.

It finds BepInEx the same way: `--bepinex`, then `BEPINEX_DIR`, then the BepInEx folder modkit is in (a mod manager's profile), then
the game's own.

## Building it

`dotnet build mods/ClaudeTools.Valheim -c Release` builds modkit along with the mod and puts both builds inside it. To build only modkit:
`dotnet build tools/ModKit -c Release` (needs `VALHEIM_DIR` for the game's references). It is built twice from the same code: `net48` is
`modkit.exe` and `net8.0` is `modkit.dll`.

The code here is just the command line (`Program.cs`, `Cli.cs`), the games it knows (`Games.cs`) and `package` (`Package.cs`). The rest
is Claude Tools' own source (and Valheim's systems, `mods/ClaudeTools.Valheim/ValheimSystems.cs`), linked in by `ModKit.csproj`: a fix
there reaches both.
