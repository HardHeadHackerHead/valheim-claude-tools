# valheim-claude-tools

The toolkit for making Valheim mods with an AI assistant: the Claude Tools mod (`mods/ClaudeTools`), the skills (`skills/`), the
templates (`templates/`, with `templates/project`, a whole mod project), modkit (`tools/ModKit`) and the pitfalls (`docs/`). The README
says what each part is.

- Before changing the mod or a template, read `docs/modding-pitfalls.md` and the **valheim-pitfalls** skill.
- The mod carries the skills, templates, modkit and the Python kits inside its DLL (see `mods/ClaudeTools/ClaudeTools.csproj`) and
  writes them to `BepInEx/claude`: a change to any of them reaches players only with a new release of the mod (raise `Version` in
  `mods/ClaudeTools/Plugin.cs`, add the change at the top of `mods/ClaudeTools/CHANGELOG.txt`, run `publish.ps1`, commit `dist/`).
- `mods/ClaudeTools/CLAUDE.md` is the assistant's in-game guide (every command): add each new command to it, and to `README.md` when
  players or mod makers would want to know.
- After changing `templates/project`, run `python tools/sync_project_template.py`: the mod manager repo
  (https://github.com/HardHeadHackerHead/valheim-mod-manager, `template/`) keeps the same files for people listing their mods in it.
- Build: `dotnet build mods/ClaudeTools -c Release` (needs `VALHEIM_DIR`; it copies the DLL into `BepInEx/scripts`, which the dev loader
  may reload at once while the game runs: `-p:DeployToGame=false` to only build).
- Our mods that use these tools live in https://github.com/HardHeadHackerHead/valheim-mods.
- The repo is also a Claude Code plugin (`.claude-plugin/`: the marketplace and the plugin `valheim-modding`, whose skills are
  `skills/`). Keep `version` in `.claude-plugin/plugin.json` the same as the mod's `Version`, and check with
  `claude plugin validate .` (the one warning, about this CLAUDE.md not being plugin context, is expected).
- After changing a skill, run `python tools/check_skills.py`: a description with an unquoted `: ` breaks the skill's header and it is
  never used by itself (two of ours were like that until 1.2.6, and `claude plugin validate` doesn't notice).
