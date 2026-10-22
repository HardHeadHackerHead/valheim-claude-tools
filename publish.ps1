# Builds Claude Tools for Valheim (its core and ClaudeTools.Valheim, which carries modkit) and writes .\dist: the DLLs, their .pdbs, the
# cover and manifest.json, the feed the Valheim mod manager (F7) reads. Then commit and push dist/. Set VALHEIM_DIR to the game folder first.
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

$manifest = @()   # becomes dist\manifest.json, which the in-game mod manager reads
$GuidSwitchRelease = $false   # (the com.dhack -> com.quad release, 1.2.2, is out: "was" alone tells the manager the old GUID)
# Claude Tools' game adapters are not mods of their own in the feed: Valheim's ships in Claude Tools' entry (both DLLs, one install), and
# the other games' (ClaudeTools.Mycopunk...) are not Valheim's.
$shipsWith = @{ "ClaudeTools" = @("ClaudeTools.Valheim") }

foreach ($proj in Get-ChildItem (Join-Path $root "mods") -Directory) {
    $csproj = Join-Path $proj.FullName "$($proj.Name).csproj"
    if (-not (Test-Path $csproj)) { continue }
    if ($proj.Name -like "ClaudeTools.*") { continue }   # (an adapter: see $shipsWith)

    $files = @()
    foreach ($part in @($proj.Name) + @($shipsWith[$proj.Name] | Where-Object { $_ })) {
        $partDir = Join-Path (Join-Path $root "mods") $part
        Write-Host "Building $part..."
        dotnet build (Join-Path $partDir "$part.csproj") -c Release --nologo -v q -p:DeployToGame=false
        if ($LASTEXITCODE -ne 0) { Write-Error "Build failed for $part"; exit 1 }
        $out = Join-Path $partDir "bin/Release/net48"
        Copy-Item (Join-Path $out "$part.dll") $dist -Force
        Copy-Item (Join-Path $out "$part.pdb") $dist -Force   # ScriptEngine needs the .pdb beside the DLL
        $files += @("$part.dll", "$part.pdb")
    }

    # Read guid/name/version from the mod's [BepInPlugin] constants, description from DESCRIPTION.txt (optional).
    $src = (Get-ChildItem $proj.FullName -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
            ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $guid = [regex]::Match($src, 'const string Guid\s*=\s*"([^"]+)"').Groups[1].Value
    # the plugin's own (public) Name: a mod may have other private Name constants (Parkour's bar)
    $name = [regex]::Match($src, 'public const string Name\s*=\s*"([^"]+)"').Groups[1].Value
    if (-not $name) { $name = [regex]::Match($src, 'const string Name\s*=\s*"([^"]+)"').Groups[1].Value }
    $ver  = [regex]::Match($src, 'const string Version\s*=\s*"([^"]+)"').Groups[1].Value
    # The mod's GUID before it changed (com.dhack.* until 2026-10): the manager treats the old one as an older copy of this mod.
    $oldGuid = [regex]::Match($src, 'const string OldGuid\s*=\s*"([^"]+)"').Groups[1].Value
    if (-not $guid -or -not $ver) { Write-Error "Couldn't find Guid/Version constants in $($proj.Name)"; exit 1 }
    $descFile = Join-Path $proj.FullName "DESCRIPTION.txt"
    $desc = if (Test-Path $descFile) { (Get-Content $descFile -Raw -Encoding UTF8).Trim() } else { "" }

    # "What's new" shown in the manager when an update is waiting: the top section of CHANGELOG.txt (up to the first blank line).
    $notes = ""
    $logFile = Join-Path $proj.FullName "CHANGELOG.txt"
    if (Test-Path $logFile) {
        $lines = @()
        foreach ($line in (Get-Content $logFile -Encoding UTF8)) {
            if ($line.Trim() -eq "") { if ($lines.Count -gt 0) { break } else { continue } }
            $lines += $line.Trim()
        }
        $notes = ($lines -join "  ")
    }

    # Optional RESTART_REQUIRED.txt: the mod can't be hot-reloaded safely, so the manager asks for a game restart. The file's text is the reason.
    $restartFile = Join-Path $proj.FullName "RESTART_REQUIRED.txt"
    $restart = if (Test-Path $restartFile) { (Get-Content $restartFile -Raw -Encoding UTF8).Trim() } else { "" }
    # The release that moved every mod to its com.quad.* GUID: a manager that doesn't know "was" would hot-load the new copy next to the
    # old one (two GUIDs, so both run), so this one release asks for a restart. Set $GuidSwitchRelease to $false after it.
    if ($GuidSwitchRelease -and $oldGuid -and -not $restart) { $restart = "This update gives the mod a new id ($guid; it was $oldGuid). Restart the game to finish: your settings move over by themselves." }

    # Optional cover image (cover.png or cover.jpg in the mod folder), shown on the mod's card in the manager. Keep it small: about 640x360, under 1 MB.
    $cover = ""
    foreach ($ext in @("png", "jpg", "jpeg")) {
        $coverSrc = Join-Path $proj.FullName "cover.$ext"
        if (Test-Path $coverSrc) {
            $cover = "$($proj.Name).cover.$ext"
            Copy-Item $coverSrc (Join-Path $dist $cover) -Force
            if ((Get-Item $coverSrc).Length -gt 1MB) { Write-Warning "$($proj.Name): the cover image is over 1 MB, so the manager will skip it" }
            break
        }
    }

    $manifest += [ordered]@{
        guid = $guid; name = $name; version = $ver; description = $desc; notes = $notes; restart = $restart; cover = $cover
        files = $files
        was = @($oldGuid | Where-Object { $_ })
    }
}

$json = ConvertTo-Json -InputObject ([ordered]@{ mods = @($manifest) }) -Depth 5
[IO.File]::WriteAllText((Join-Path $dist "manifest.json"), $json, (New-Object Text.UTF8Encoding($false)))

Write-Host "`nReady in $dist :"
Get-ChildItem $dist | Select-Object Name, Length, LastWriteTime
Write-Host "`nNext: git add dist; git commit; git push"
