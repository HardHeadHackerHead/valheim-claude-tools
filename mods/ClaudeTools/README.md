# Claude Tools (the mod)

The in-game half of [Claude Tools for Valheim](../../README.md): install, settings and everything it does are on the repo's front page.

This folder is its **core**, `ClaudeTools.dll` (`com.quad.claudetools`): the request mailbox, the commands any Unity game has (pictures,
`render`, `screen`, the log, `lag`, hot reload, the mod-maker commands), and none of a game's code. What it knows about the game comes
from that game's **adapter**, a small mod beside it: [ClaudeTools.Valheim](../ClaudeTools.Valheim/),
[ClaudeTools.Mycopunk](../ClaudeTools.Mycopunk/), [ClaudeTools.Emberville](../ClaudeTools.Emberville/). Each one makes a `GameAdapter`
(`GameAdapter.cs`: where the player is, the cheat rule, the camera, the game's code and Thunderstore community...) and adds the game's
own commands. Without one, the core runs on defaults for any Unity game.

- `GUIDE.md`: the general guide the mod writes to `BepInEx/claude/CLAUDE.md` when the game's adapter has none of its own.
- `CHANGELOG.txt`: every change; its top paragraph is what the mod manager shows when an update is waiting.
- `DESCRIPTION.txt`: the text on the mod's card in the mod manager.
- Built for a game other than Valheim (`-p:Game=Mycopunk`, as its adapter does), it goes to `bin/Release/<Game>/net48`.
