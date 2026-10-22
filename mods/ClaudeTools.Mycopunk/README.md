# Claude Tools for Mycopunk

`ClaudeTools.Mycopunk.dll` (`com.quad.claudetools.mycopunk`): what [Claude Tools](../ClaudeTools/) knows about Mycopunk.

- In a world while `PlayerLook.Instance` is there; the player is `Pigeon.Movement.Player.LocalPlayer`, the camera `PlayerLook.LocalCamera`.
- Typing: a text box, the text chat, or the menu open. Cheats (screen click, type, use, key): only for the host, or playing alone.
- Its commands: `status` (level, player, position, health, host, who is in it) and `ui` (the same as `screen`, which also lists and
  presses the game's own buttons, `Pigeon.UI.Button`, by their text or name, and presses keys through the new Input System:
  `ui click play`, `ui key F8`; `Screen.cs`). `ui` and `shot` work at the main menu too. `GAME.md` is added to the end of the general
  guide in `BepInEx/claude/CLAUDE.md`. Everything else is Claude Tools' own: `shot`, `view`,
  `orbit`, `top`, `render`, `inspect`, `screen`, `log`, `errors`, `lag`, `reload`, `game`, `modcheck`, `who`, `clashes`, `library`
  (Thunderstore's `mycopunk` community)...
- Notices (F12's "Screenshot saved") go to the log only. Claude Tools reads keys through the new Input System by itself.

## Build and install

`dotnet build mods/ClaudeTools.Mycopunk -c Release -p:DeployToGame=false` (the game in `MYCOPUNK_DIR`, or `D:/SteamLibrary/...`). It
builds Claude Tools' core against Mycopunk's Unity too. Install both, in the same folder (`BepInEx/plugins/ClaudeTools`, or
`BepInEx/scripts` for hot reload):

- `mods/ClaudeTools/bin/Release/Mycopunk/net48/ClaudeTools.dll` (and `.pdb`)
- `mods/ClaudeTools.Mycopunk/bin/Release/net48/ClaudeTools.Mycopunk.dll` (and `.pdb`)

Without `-p:DeployToGame=false` the build copies both into the game's `BepInEx/scripts` itself. Then switch requests on in
`BepInEx/config/com.quad.claudetools.cfg` (`AllowRequests = true`) and start Claude Code in `BepInEx/claude`.
