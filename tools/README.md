# Tools

The programs and scripts behind Claude Tools. Some of them go to players inside the mod. The others are for working on this repo.

| Folder or file | What it is | Players get it? |
|---|---|---|
| [ModKit/](ModKit/) | **modkit**: the mod-maker commands (`modcheck`, `who`, `clashes`, `game`, `library`, `package`...) on the command line, with the game closed (Valheim, Mycopunk or Emberville: `--game`). Windows, Linux and Mac. | Yes, in `BepInEx/claude/modkit` (Valheim's) |
| [modelkit/](modelkit/) | Python for designing a piece's or item's 3D model offline, seeing it from every side, and turning it into C# dressed in the game's materials. | Yes, in `BepInEx/claude/modelkit` |
| [blueprints/tools/](blueprints/tools/) | Python that reads the game's building pieces (sizes, snap points, real shapes), draws them, and designs blueprints for Quad's Build Orders. | Yes, in `BepInEx/claude/modelkit` |
| [PatchMap/](PatchMap/) | Makes `mods/ClaudeTools.Valheim/Shipped/patchmap.json.gz`: what the 100 most-downloaded Thunderstore mods for Valheim patch (facts only, none of their code). | The map it makes, inside ClaudeTools.Valheim |
| [Shared/](Shared/) | `Thunderstore.cs`: reading Thunderstore's package list, shared by ModKit and PatchMap. | |
| `check_skills.py` | Every skill's header is valid (an unquoted `: ` in a description stops the skill from being picked up by itself). Run after changing a skill. Needs PyYAML. | |
| `check_translations.py` | Every language has the same keys and placeholders as English. | |
| `sync_project_template.py` | Copies `templates/project` into the mod manager repo's `template/`, so the two stay the same. Run after changing the template. | |

## PatchMap

Run it when the popular mods have changed enough to matter (new mods in the top 100, big updates). Then release Claude Tools so players
get the new map:

```
dotnet run --project tools/PatchMap -c Release            (the top 100)
dotnet run --project tools/PatchMap -c Release -- 50      (another number of mods)
dotnet run --project tools/PatchMap -c Release -- --rescan    (scan the cached DLLs again, after the scanner gets better)
```

It downloads each mod's DLLs (code only, never run) into `tools/PatchMap/cache`, which is never committed. A later run downloads only
mods that have a new version. It scans them with Claude Tools' own scanner (`Scan.cs`), so the map is made exactly as the game makes
it. Needs `VALHEIM_DIR`, for the game's references.
