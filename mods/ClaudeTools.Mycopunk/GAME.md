## Mycopunk

This game is **Mycopunk** (Claude Tools for Mycopunk, `ClaudeTools.Mycopunk.dll`). The player is "in a world" from spawning in a level,
the hub included; `ui`, `shot`, `log`, `errors` and the other main-menu commands above work before that too.

| Command | What it does |
|---|---|
| `status` | The level, the player, where they stand, health, whether this game hosts, who is in it. |
| `ui [list [text]]` | Every button on screen with its text and where it is: the game's own buttons too (the main menu's, the hub's windows). The same as `screen list`. |
| `ui click <words or name> [n]` | Press a button by its text (or its object's name, for one showing only an icon), as the game presses it: `ui click play` at the main menu. |
| `ui key <key> [frames=1]` | A key down for that many frames, then up, through the game's own input (the new Input System: what the game reads sees it): `ui key F8`, `ui key Escape`, `ui key Enter`, `ui key E`. |
| `ui texts [text]` / `ui type <text>` / `ui submit` | The texts on screen; type into the focused text box, and submit it. |

Clicking, typing and keys act as the player, so they only work for the host (or playing alone): ask the player first. Getting into the
hub from the main menu, one request at a time, looking after each step:

```
ui list
shot 1280
```

then `ui click play` (or what the list shows), `wait 3`, `ui list`, `shot 1280` again, until `status` answers (the player is in the hub).
