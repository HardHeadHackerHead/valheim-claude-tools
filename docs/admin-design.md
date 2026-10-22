# Claude as the server admin's helper: design

Status: agreed 2026-10-13 (the player's answers are at the end); being built from step 1.

The player wants to say "bring Bob to me", "give everyone 20 wood", "make it day", "kick the griefer", "why can't Bob load in" and have
Claude just do it. Today Claude Tools can only act on the player's own character and the world loaded around them. This proposes how
Claude Tools grows into an admin helper, and what each part may do.

## What Claude Tools can do today, and where it stops

About 70 commands, in four groups:

| Group | Commands | Reach |
|---|---|---|
| Look | status, nearby, looking, inventory, chests, effects, playerdata, objects, survey, shot/view/orbit/top/render, screen, log, errors, lag | own character; loaded area (objects: whole world) |
| Act on yourself | give, drop, pickup, use, craft, inv move, itemdata, shoot tp/look, console, daytime, say, pin, raven, grow, clear, terrain, tcdoctor | own character, own game; changing ones only where cheats are allowed |
| Mods | reload, devmods, waitfor, mods, config, modcheck, who, clashes, patches, game, library | own game |
| Get in and out | play, logout, quit | own game |

What tonight showed it can't do:

- **Other players are out of reach.** "Bring Bob to me" failed: there is no command that targets another player. Blockscript can teleport
  a player, but only one loaded near the host, and only when that player turned "Servers may teleport me" on.
- **It can't see the whole server.** `status` lists players from `Player.GetAllPlayers()` (only characters loaded near you), so it said
  trash wasn't online while he was connected. The server's own list (`ZNet.GetPlayerList()`) has everyone.
- **Fixes need throwaway code.** Getting the player out of a stuck respawn took a one-off plugin (SpawnDoctor): there is no command for
  "why is this load stuck" or for removing a record that blocks it.
- **"Only where cheats are allowed"** is the only rule, and it is about the player's own world. It says nothing about acting on others.

## What the game itself allows (no mod needed on the other player's game)

Read from the game's code (assembly_valheim, 2026-10). Every character answers these network calls; the owner (each player's own game)
carries them out, so they work on a player anywhere on the map, sent from the host or from any client:

| Call | Does | Use |
|---|---|---|
| `Character.RPC_TeleportTo(pos, rot, distant)` | moves the character (distant: loads the area first) | bring, goto, send home |
| `Character.RPC_Heal(hp, showText)` | heals | heal, revive-ish top-up |
| `Character.RPC_Damage(HitData)` | damages (can kill) | smite (asked for explicitly) |
| `SEMan.RPC_AddStatusEffect(hash, ...)` | gives a status effect (rested, wet, a mead) | buffs, cures by replacement |
| `Player "Message"(type, text, amount)` | text in the middle or top left of that player's screen | tell one player something |
| `Player "UseStamina"(n)` | drains stamina | (rarely useful) |

And the server's own admin tools (`ZNet`): the player list with every character's id and position, the admin list, `Kick`, `Ban`,
`Unban`, `Save`, the banned list, and `RPC_RemoteCommand`, which runs a console command on a **dedicated** server for a client on its
admin list (that is the "Remote admin executed command 'devcommands'" line in tonight's log). World-wide things live on the server:
global keys (boss progress), random events (raids), the world clock.

What the game does **not** allow from outside: putting items into another player's bag, reading their bag, or changing their skills.
Those live in the player's own game and save file. For those, something of ours must run in their game (see "Reach", level 3).

## Three levels of reach

1. **Own game** (today): the player's character, their screen, their mods.
2. **The server**: anything the host (or an admin on a dedicated server) may do: the game's calls above, the server's lists, world-wide
   state, and objects anywhere in the world. Needs no mod on anyone else's game. **This covers most of what the player asked for.**
3. **Another player's own game**: their bag and their skills. Only code in their game can change those, so this works only when that player
   runs Claude Tools too (the same mod: standalone, nothing goes through the mod manager or Blockscript). Without it, `give` drops the items
   at their feet.

## Who may use it

- The admin commands run only when this game is the **host**, or this player is on the server's **admin list** (`ZNet` syncs it to
  clients). Not in someone else's world where you are a guest.
- A new config switch, `AllowAdmin` (off by default, like `AllowRequests` and `AllowConfigChanges`): the player turns it on once.
- Level 3 also needs the **other player's** consent: a setting in their own Claude Tools ("Let the server's admins put items in my bag").
  Teleport, heal and messages need none (level 2: the game itself allows them).

## Safety: the rules, from what has gone wrong before

- **Landing**: never teleport to a raw height. Land beside a player who is standing (their position is on ground), or on the ground at a
  spot (the server's ground height, `distant` teleport so the area loads first). A landing 3 m off has killed the player twice.
- **Undo**: remember where a teleported player stood; `tp <who> back` returns them.
- **Danger tiers**: say the enemy tier before spawning creatures in a real world (Shifting Halls' spawners killed the player and Bob);
  damage/kill, ban, global keys and world-wide removals only when the player asked for exactly that, and Claude repeats what will happen.
- **Backups**: commands that change many things at once (remove objects across the world, reset global keys) save the world and copy
  the save first, and say where the copy is.
- **Visible**: every admin action is written to `BepInEx/claude/admin.log` (time, who asked, what, to whom, result), and the player it
  touched sees a short message ("Quad's admin teleported you to Quad"). Nothing is done to a friend silently.
- **Request names**: requests are read in name order from one folder, shared by every Claude session. Two sessions collided tonight
  (`tb3`): the guide asks for a session prefix.

## Proposed commands

Targets: a player's character name (`trash`), `me`, `all`, or `near <radius>`. Places: `me`, a player's name, `x z`, a map pin's text, a placed blueprint's name.

**Players**
| Command | Does |
|---|---|
| `players` | everyone on the server: name, admin, alive/dead, position, distance, health (when loaded) |
| `tp <who> to <place>` / `bring <who>` / `goto <who>` / `tp <who> back` | move players (safe landing, undo) |
| `heal <who> [hp]` / `rest <who>` / `effect <who> <effect> [seconds]` | care |
| `tell <who> <text>` / `announce <text>` | a message on one player's screen / everyone's |
| `give <who> <item> [n]` | dropped at their feet; into their bag when they run Claude Tools and allow it (level 3) |
| `kick`, `ban`, `unban <who>`, `banned` | server |
| `smite <who>` | damage, only when asked for that |

**World**
| Command | Does |
|---|---|
| `time <hh:mm> \| skip <hours>` | the world's clock, for everyone (not `daytime`, which only changes this screen) |
| `weather <clear\|rain\|storm\|mist\|snow> [minutes]` | for everyone |
| `event <name> [near <who>] \| event stop` | raids |
| `keys [set\|remove <key>]` | global keys (boss progress), with a backup |
| `spawn <creature> [n] [level] [near <who>]` | says the tier first |
| `save` | save the world now |
| `find <prefab\|player>` | where things are in the whole world (today's `objects`, plus players) |

**Fixing**
| Command | Does |
|---|---|
| `stuck [who]` | why a respawn or a teleport doesn't finish: the objects at the spawn point that never load (tonight's SpawnDoctor), and `stuck fix` removes leftover effect records (never anything built or carried) |
| `tcdoctor` (exists) | the ground's change records |
| `who-killed <who>` | the last deaths and what did them (from the log and the game's death events) |

`console` keeps running the game's own commands; on a dedicated server, admin commands go through `RPC_RemoteCommand`.

## The guide (BepInEx/claude/CLAUDE.md)

Today it says "Ask the player before looking at or doing things in their game". With `AllowAdmin` on, the player is the admin and has
asked: Claude does what was asked, says what it did, and asks first only for the danger tiers above. The guide gets an "Admin" section:
targets, the reach levels, the rules, and `players` first in a new session.

## Steps

1. **Level 2, read-only**: `players` from the server's list (and fix `status`'s "online"), `find` players. Small, safe, fixes tonight's
   confusion.
2. **Level 2, acting**: tp/bring/goto/back, heal, effect, tell/announce, time, weather, save, kick/ban; `admin.log`; `AllowAdmin`.
3. **Fixing**: `stuck` (+ `stuck fix`), `who-killed`.
4. **World**: event, keys, spawn (with tiers and backups).
5. **Level 3**: Claude Tools on the other player's game, with their consent, for give into the bag and skills. Needs a two-player test with Bob.

Each step: build, test in ClaudeTest with Bob, release Claude Tools.

## The player's answers (2026-10-13)

1. Everything is Claude Tools, standalone: no mod manager, no Blockscript.
2. Yes: a player always sees a message when an admin acts on them.
3. The player hosts the server themselves (their game is the server): no dedicated-server part needed.
4. No nicknames: character names (Bob plays as trash).
