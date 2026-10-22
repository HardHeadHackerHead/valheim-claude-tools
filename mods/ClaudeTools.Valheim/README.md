# Claude Tools for Valheim

`ClaudeTools.Valheim.dll` (`com.quad.claudetools.valheim`): what [Claude Tools](../ClaudeTools/) knows about Valheim, and Valheim's own
commands. It goes beside `ClaudeTools.dll`, in the same folder; the mod manager and `dist/` ship them together.

- `ValheimGame.cs`: the game for Claude Tools (the player, `GameCamera`, typing in chat or the console, the cheat rule, messages on
  screen, prefabs from `ZNetScene` and `ObjectDB`, the game's code, systems and Thunderstore community).
- The commands: `Info.cs` (status, inventory, nearby, chests, console, play, language...), `Survey.cs` (survey, Ctrl+F12), `Building.cs`
  (colliders, support, comfort), `Shoot.cs`, `Objects.cs`, `Terrain.cs`, `TcDoctor.cs`, `Testing.cs`, `Screen.cs` (screen's item cells,
  `use`, `pause`, `key`), `ConsoleCommand.cs` (`claude <command>` in the game's console).
- `CLAUDE.md`: the guide Claude Tools writes to `BepInEx/claude/CLAUDE.md` for the assistant (every command, how requests work).
- `Shipped/patchmap.json.gz`: what the top 100 Thunderstore mods patch (facts only), made by `tools/PatchMap`.
- It carries the skills, templates, modkit and modelkit too (see `ClaudeTools.Valheim.csproj`); they are at the repo's top:
  [skills/](../../skills/), [templates/](../../templates/), [tools/](../../tools/).

Its settings (`SurveyKey`, `SurveyRadius`, `AllowAdmin`, Clawd's `Summoned` and `Chatty`) stay in Claude Tools' own
`com.quad.claudetools.cfg`. `Admin.cs` is the server admin commands, `Clawd.cs` Clawd, whose hooks into requests and pictures go through
`ValheimGame` (`OnCommand`, `OnRequestDone`, `HideForPictures`); reloads are said in the game's chat (`ValheimGame.Announce`).
