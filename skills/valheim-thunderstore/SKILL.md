---
name: valheim-thunderstore
description: Release a Valheim mod on Thunderstore (where r2modman and Thunderstore Mod Manager get mods) and Hexium (where Gale gets them), or publish an update to one - the package, its manifest, icon and README, Thunderstore's rules (AI disclosure included), testing the zip, uploading with the Thunderstore CLI (tcli) and Hexium's API. Use when someone wants to publish, release, upload or share a mod on Thunderstore or Hexium, or update one that is there.
---

# Releasing a mod on Thunderstore

> **Game closed?** The mod-maker commands (`modcheck`, `package`, `who`, `clashes`, `game`...) run as a program:
> `BepInEx/claude/modkit/modkit.exe <command>` on Windows, `dotnet BepInEx/claude/modkit/modkit.dll <command>` on Linux and Mac. Below,
> `modkit` means that.

Thunderstore (https://thunderstore.io/c/valheim/) is where most players get mods: r2modman and Thunderstore Mod Manager install from it
into `BepInEx/plugins`. A package is a zip with `manifest.json`, `icon.png`, `README.md` (and `CHANGELOG.md`) at its root and the DLL in
`plugins/`. **A published version can never be changed or deleted**, only followed by a higher one: every step before the upload is there
so the upload is right the first time.

Go through **valheim-prerelease** first. Then:

## 1. Once: the account, the team, the tools

Ask the player to do the account parts themselves (they're theirs, and the token is a password):

1. **Account**: sign in at https://thunderstore.io with Discord, GitHub or Overwolf.
2. **Team**: https://thunderstore.io/teams/ → create one. Every package belongs to a team, and the team name is part of every dependency
   string (`Team-Package-1.0.0`) and **can't be changed**: a solo modder usually takes their own name.
3. **Token**: the team's page → Service Accounts → Add service account → copy the token (`tss_...`). It is shown once. The player keeps it
   in an environment variable, typed in their own terminal, never in a file in the project and never pasted to you:
   Windows `setx TCLI_AUTH_TOKEN "tss_..."` (then a new terminal), Linux and Mac `export TCLI_AUTH_TOKEN=tss_...` in their shell profile.
   (In GitHub Actions, a repository secret.) If a token ever lands in a file or a chat, they delete that service account and make a new one.
4. **tcli**, the Thunderstore CLI, which builds the zip and uploads it. Check whether it's there: `tcli --version` (`modkit package` also
   says). If not: `dotnet tool install -g tcli` (the .NET SDK that builds mods brings `dotnet`), then a new terminal. If `tcli` still isn't
   found, the tools folder isn't on PATH: `~/.dotnet/tools/tcli` (Windows `%USERPROFILE%\.dotnet\tools\tcli.exe`) runs it directly.
   Update it now and then: `dotnet tool update -g tcli`.

## 2. Thunderstore's rules (they remove packages that break them)

- **Say AI was used.** If AI helped write the mod (Claude did): a line in the README ("Made with the help of Claude (Anthropic)."),
  the **ai-generated** category, and in the DLL. Put these two lines in any `.cs` file of the mod (the template's
  `Directory.Build.props` sets `GenerateAssemblyInfo` to false, which makes the csproj form `<AssemblyMetadata Include=...>` do nothing):
  ```csharp
  [assembly: System.Reflection.AssemblyMetadata("AI_Assisted_Creation", "This assembly was partially or fully created with the assistance of Generative AI.")]
  [assembly: System.Reflection.AssemblyMetadata("AI_Model_Vendor", "Anthropic")]
  ```
- **Tested and working.** "Untested or non-functional AI-generated mods" count as spam. Test the zip itself (step 4), on a clean profile.
- **The README says what the mod really does**, and its images are public `https://` links (not `images/x.png`: files in the package aren't
  shown). Push the images to GitHub and link `https://raw.githubusercontent.com/<you>/<repo>/main/<path>`.
- **No self-updating** (mod managers do updates), no obfuscated code, no downloading and running code, no collecting data about players.
  Moderators reject a package that *looks* like it could run outside code, even when it doesn't: one DLL that downloads other DLLs and
  also loads assemblies from bytes (`Assembly.Load(byte[])`) reads as a malware loader. Our own Claude Tools was rejected for that
  ("Invalid submission"). Keep such tools off Thunderstore, or leave those parts out of the Thunderstore build.
- **Other people's work**: don't include game files or other authors' mods or assets unless their licence allows it; depend on their
  package instead.
- One mod per job: not five near-identical packages.

## 3. The package: `modkit package`

```
modkit package <mod folder> --namespace <Team> --website https://github.com/<you>/<repo>     (first time)
modkit package <mod folder>                                                                (every time after)
```

It writes `thunderstore.toml` beside the csproj (the CLI's own project file: name, version, description, dependencies, icon, README,
which files go where, categories), keeps its version and dependencies matching the built DLL, and checks what an upload needs:

| It checks | So that |
|---|---|
| the DLL is newer than every `.cs` file | the package holds the build you mean |
| name, version (`Major.Minor.Patch`, the plugin's `Version`), description (250 characters at most) | Thunderstore accepts it |
| dependencies: BepInExPack, and each `[BepInDependency]` (hard) mapped to its Thunderstore package | players get what it needs |
| the version isn't published yet and is higher than every published one | the upload isn't refused |
| the icon is a 256 x 256 PNG | Thunderstore accepts it (`render <your prefab> size=256x256 bg=dark` in the game makes one) |
| the README is UTF-8, says what it does, mentions AI and other mods, its images are pushed | the page is right and follows the rules |
| the AI category and `AssemblyMetadata` | Thunderstore's rule |
| `modcheck` on the DLL (no problems) and nothing uncommitted in the folder | nothing broken or unfinished ships |

It also makes, in `thunderstore-build/` (add it to `.gitignore`):
- **the package's README** from the mod's `README.md`, with relative links made absolute so they work on Thunderstore: images point at
  `raw.githubusercontent.com`, other links at `github.com`, from the folder's git remote and branch. So the README can keep relative
  links for GitHub; **push** the images before publishing (it warns about any that aren't on the remote).
  Lines for one site only: `<!-- GITHUB-ONLY -->...<!-- END GITHUB-ONLY -->` is left out of the package's README (a link to the
  Thunderstore page itself, install steps for another manager), and `<!-- THUNDERSTORE-ONLY: ... -->` is hidden on GitHub and shown there.
- **CHANGELOG.md**: the mod's own `CHANGELOG.md` (a `## 1.0.1` heading per version, newest first, one line per change: not "various
  fixes"), or one made from `CHANGELOG.txt`, where a paragraph starting `1.0.1: ` gets that version's heading.

**The icon** is `thunderstore/icon.png` when there is one, or when the mod embeds an `icon.png` of its own (a build-menu icon): never
replace a mod's in-game icon with the package icon. Otherwise `icon.png` beside the csproj.

Read `thunderstore.toml` the first time: the description comes from `DESCRIPTION.txt` (or is marked TODO), categories start as
`mods` and `ai-generated` only, which `modkit package` refuses (next section). Edits to the toml are kept: modkit only updates the
version and dependencies. Fix every **problem** and run it again until there are none.

## 3b. Make it findable: the description and the categories (every first upload, and look again at each update)

A package nobody finds is wasted work, and the first upload is when it's newest and listed at the top. **Never upload with the
categories and description modkit made: write them.** What Thunderstore's search does (checked against the site, October 2026):

- It reads **only the package's name and its description**. Not the README, not the categories, not the changelog. A word that is only
  in the README ("saddlebags") doesn't find the mod.
- It matches **parts of words** ("ski" finds "Skis", "boat" finds "motorboat") and needs **every word typed** ("fast boat" finds a
  description with both). It does not join or split words: "jetski" finds nothing that only says "jet ski".
- The description is also the line under the name in r2modman's and Thunderstore Mod Manager's lists: it must still read as a sentence.

**The description** (250 characters at most: use most of them):
1. Start with what it is, in the words players type: the thing ("jet ski", "follower NPC", "quick slots"), not a pun or the mod's name.
2. Add the other spellings and names people use for it ("jet ski (jetski)", "motorboat", "speedboat"; "auto fuel"/"auto refuel";
   "salvage"/"recycle"/"deconstruct"): the words other mods for the same job use in their names and descriptions.
3. Then its main features as nouns players search for ("saddlebag storage", "paint colours", "map pins").
4. Leave out "for Valheim" and "mod" (every package there is a Valheim mod), and "Quad's" or your team's name (already shown).
5. Test the words: `https://thunderstore.io/api/cyberstorm/listing/valheim/?q=<word>` lists what each finds (its `count`, and each
   result's `name`, `description`, `categories`). A word with 0 results that players would type (a word for exactly your thing) is
   worth having; a word with hundreds says nothing.

**The categories** (the filters on the site; players narrow the list with them). The 100 most-downloaded Valheim mods have about 6.
Look up two or three popular mods that do something like yours (the listing above shows their `categories`) and tag every one that
truly fits, never one that doesn't (moderators and players notice):

| Kind | Categories |
|---|---|
| What it is (at least one) | `building`, `crafting`, `gear`, `enemies`, `npcs`, `world-generation`, `transportation`, `vehicles`, `pvp`, `audio`, `skins`, `tweaks`, `utility`, `tools`, `misc`, `libraries`, `language` (translations only) |
| Where it runs | `client-side` (each player installs it), `server-side` (the server needs it): both when everyone and the server need it, as for anything that adds pieces, items or creatures |
| The game update | `deep-north-update` (the newest update, a section of the site of its own) once tested on the current game version; add the next update's when it comes and the mod still works |
| Always | `mods`, and `ai-generated` when AI helped (Thunderstore's rule) |

For example, a jet ski: `mods`, `vehicles`, `transportation`, `server-side`, `client-side`, `deep-north-update`, `ai-generated`. The
popular boat mods (ValheimRAFT, OdinShip, BoatAdditions) all carry `transportation` and `vehicles`.

`modkit package` checks this: a **problem** when no category says what the mod is, **warnings** for neither client-side nor
server-side, no current-update category, or fewer than 5 categories, and **notes** for a short description or one that spends words on
"Valheim". The package's **name** is searched too: keep the thing in it (`Quads_Jet_Skis`, not `Quads_Splash`). It can't change after
the first upload.

**A repository with several mods**: each mod is its own package (one `thunderstore.toml` per mod folder, the same team). A small script
that, for each mod, builds it, runs `modkit package`, `tcli build` and `modkit package check`, and publishes only the mods named on its
command line keeps releases consistent. Other channels (a GitHub feed, a mod manager of your own) can ship the same builds alongside.

## 4. Build the zip, check it, test it

```
tcli build --config-path <mod folder>/thunderstore.toml        writes <mod folder>/thunderstore-build/<Team>-<Name>-<version>.zip
modkit package check <that zip>
```

Then test **that zip**, not the DLL you built: ask the player to open r2modman (or Thunderstore Mod Manager), make a **new, empty profile**,
Settings → **Import local mod** → the zip, start the game from that profile, and use the mod. With Claude Tools in that profile too,
`errors` and the mod's own checks work as usual. A clean profile catches what your own setup hides: a missing dependency, a file in the
wrong place, a config it expects to exist.

Add `thunderstore-build/` to `.gitignore`.

## 5. Publish (ask first)

Show the player what will go up (package name, version, the description, the categories, the changelog lines) and ask. Then, with `TCLI_AUTH_TOKEN` set:

```
tcli publish --config-path <mod folder>/thunderstore.toml --file <that zip>
```

It can take a few minutes to show on the site. Never pass the token on the command line in a shared log, and never write it into
`thunderstore.toml`.

## 6. Updates

Raise the plugin's `Version` (semantic: 1.0.10 is higher than 1.0.9), add the changelog lines, rebuild, then `modkit package` (it updates
the toml), read the description and categories again (3b: new features bring new search words; a new game update its category),
`tcli build`, check, test, ask, publish. Never change `name` (a new name is a new package) or the team. Players on
Thunderstore restart the game after updating, so the reload level doesn't matter there; it still matters for your own testing.

To retire a mod: deprecate it on its Thunderstore page (it stays installable for existing modpacks).

## 7. Hexium too (the same zip, a second site)

Hexium (https://hexium.gg/c/valheim/) is a newer mod site that Gale installs from (r2modman doesn't). Many of the big Valheim authors
are on it. It takes **the same zip**: publish the one you just tested and published on Thunderstore, with the same version, so both
sites have the same build. It doesn't require an AI tag (keep the README line anyway), lets you edit the README on the site without a
new version, and has comments.

- **Once**: the player signs in at hexium.gg with Discord, makes a team (the same name as on Thunderstore, so dependency strings match),
  and on the team's settings page creates an API token. It goes in `HEXIUM_AUTH_TOKEN` the same way as `TCLI_AUTH_TOKEN` (step 1).
- **Uploading** uses Thunderstore's upload API at `https://hexium.gg` with `Authorization: Bearer <token>`:
  `POST /api/experimental/usermedia/initiate-upload/` `{filename, file_size_bytes}`, `PUT` each part to its `upload_urls[].url` (keep
  the `ETag`), `POST .../usermedia/<uuid>/finish-upload/` `{parts: [{ETag, PartNumber}]}`, then `POST /api/experimental/submission/submit/`
  `{upload_uuid, author_name: <team>, communities: ["valheim"], has_nsfw_content: false, categories: [], community_categories: {valheim: [...]}}`.
- **Categories are Hexium's own** (`GET /api/experimental/community/valheim/category/`: "Quality of Life", "Storage", "Pieces", "User
  Interface", "Open Source"...), not Thunderstore's slugs. Map them, and add the ones Thunderstore has no word for.
- **Where it installs**: `POST /api/mod/<team>/<name>/set-metadata` `{install_location: "client" | "server" | "both", categories}`
  with the same token (client-side only: `client`; both client-side and server-side: `both`).
- A new upload is hidden for about a minute while it's virus-scanned, then shows at `https://valheim.hexium.gg/mods/<team>/<name>`.
  `GET /api/experimental/package/<team>/<name>/<version>/` answers 200 once it's there (also the check before uploading a version again).
  The team has a setting that makes API uploads start **private**: then the player makes each one public on the site, or turns it off.
- **10 uploads per 10 minutes** (measured October 2026). The 11th submit answers 400 "You are uploading too many mods. Please wait a
  few minutes." until the oldest of the 10 is 10 minutes old. Refused tries don't count. To publish many mods, send 10, then wait
  until 10 minutes after the first of them. A refused upload is used up: submitting its `upload_uuid` again gives "Upload is not
  complete", so a retry uploads the file again.
- Full API: https://hexium.gg/api/docs/ (spec at `/api/openapi.json`). Only on the website: editing the README without a new version,
  answering comments, deprecating. A working script for all of this (one mod or many, a dry run first) is `tools/hexium_publish.py` in
  https://github.com/HardHeadHackerHead/valheim-mods, which its `thunderstore.ps1 -Publish` runs after `tcli publish`.
