# valheim-claude-tools

The toolkit for making Valheim mods with an AI assistant: the Claude Tools mod (`mods/ClaudeTools`, its core, and
`mods/ClaudeTools.Valheim`, what it knows about Valheim; `mods/ClaudeTools.Mycopunk` and `.Emberville` do the same for those games), the skills (`skills/`), the
templates (`templates/`, with `templates/project`, a whole mod project), modkit (`tools/ModKit`) and the pitfalls (`docs/`). The README
says what each part is.

- Before changing the mod or a template, read `docs/modding-pitfalls.md` and the **valheim-pitfalls** skill.
- The core (`ClaudeTools.dll`) uses no game's code: anything about a game goes in that game's adapter (a `GameAdapter`, see
  `mods/ClaudeTools/GameAdapter.cs`), and its commands there. Valheim's adapter carries the skills, templates, modkit and the Python kits
  inside its DLL (see `mods/ClaudeTools.Valheim/ClaudeTools.Valheim.csproj`) and
  writes them to `BepInEx/claude`: a change to any of them reaches players only with a new release of the mod (raise `Version` in
  `mods/ClaudeTools/Plugin.cs`, add the change at the top of `mods/ClaudeTools/CHANGELOG.txt`, run `publish.ps1`, commit `dist/`).
- `mods/ClaudeTools.Valheim/CLAUDE.md` is the assistant's in-game guide for Valheim (every command; `mods/ClaudeTools/GUIDE.md` is the
  general one, for games without their own): add each new command to it, and a line to
  `README.md`'s "Every command" list (and to its features above when players or mod makers would want to know).
- After changing `templates/project`, run `python tools/sync_project_template.py`: the mod manager repo
  (https://github.com/HardHeadHackerHead/valheim-mod-manager, `template/`) keeps the same files for people listing their mods in it.
- Build: `dotnet build mods/ClaudeTools.Valheim -c Release` (needs `VALHEIM_DIR`; it copies both DLLs into `BepInEx/scripts`, which the
  dev loader may reload at once while the game runs: `-p:DeployToGame=false` to only build). Mycopunk: `mods/ClaudeTools.Mycopunk` (needs
  `MYCOPUNK_DIR`), Emberville: `mods/ClaudeTools.Emberville`.
- Our mods that use these tools live in https://github.com/HardHeadHackerHead/valheim-mods.
- The repo is also a Claude Code plugin (`.claude-plugin/`: the marketplace and the plugin `valheim-modding`, whose skills are
  `skills/`). **Raise `version` in `.claude-plugin/plugin.json` with every change to a skill**: an installed
  plugin only updates when that number changes (it is the skills' own version, separate from the mod's). Check with
  `claude plugin validate .` (the one warning, about this CLAUDE.md not being plugin context, is expected).
- After changing a skill, run `python tools/check_skills.py`: a description with an unquoted `: ` breaks the skill's header and it is
  never used by itself (two of ours were like that until 1.2.6, and `claude plugin validate` doesn't notice).
