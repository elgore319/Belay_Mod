# Belay installer for White Knuckle.
# Started by "Install Belay.bat" / "Uninstall Belay.bat". Works with Windows PowerShell 5.1.
param(
    [switch]$Uninstall,
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"
$AppId   = "3195790"
$GameExe = "White Knuckle.exe"
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Payload = Join-Path $Here "payload"

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }

function Test-GameDir($dir) {
    return ($dir -and (Test-Path -LiteralPath (Join-Path $dir $GameExe)))
}

function Get-SteamRoots {
    $roots = @()
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @("SteamPath", "InstallPath")) {
                if ($p.$name) { $roots += ($p.$name -replace "/", "\") }
            }
        } catch { }
    }
    $roots += "${env:ProgramFiles(x86)}\Steam"
    return $roots | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
}

function Get-SteamLibraries {
    $libs = @()
    foreach ($root in Get-SteamRoots) {
        $libs += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            foreach ($line in Get-Content -LiteralPath $vdf) {
                if ($line -match '^\s*"path"\s*"(.+)"\s*$') {
                    $libs += ($Matches[1] -replace '\\\\', '\')
                }
            }
        }
    }
    # Common spots on other drives, in case Steam's registry entry is missing.
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if ($drive.DriveType -eq "Fixed" -and $drive.IsReady) {
            $libs += (Join-Path $drive.RootDirectory.FullName "SteamLibrary")
            $libs += (Join-Path $drive.RootDirectory.FullName "Steam")
        }
    }
    return $libs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
}

function Find-GameDir {
    foreach ($lib in Get-SteamLibraries) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $manifest) {
            $installdir = "White Knuckle"
            foreach ($line in Get-Content -LiteralPath $manifest) {
                if ($line -match '^\s*"installdir"\s*"(.+)"\s*$') { $installdir = $Matches[1] }
            }
            $dir = Join-Path $lib "steamapps\common\$installdir"
            if (Test-GameDir $dir) { return $dir }
        }
        $dir = Join-Path $lib "steamapps\common\White Knuckle"
        if (Test-GameDir $dir) { return $dir }
    }
    return $null
}

function Ask-GameDir {
    Say ""
    Say "I couldn't find White Knuckle automatically." "Yellow"
    Say "In Steam: right-click White Knuckle > Manage > Browse local files, then copy that folder's path."
    while ($true) {
        $dir = (Read-Host "Paste the game folder path here (or leave empty to cancel)").Trim('"', ' ')
        if (-not $dir) { return $null }
        if (Test-GameDir $dir) { return $dir }
        Say "That folder doesn't contain $GameExe. Try again." "Red"
    }
}

function Wait-GameClosed {
    while (Get-Process -Name "White Knuckle" -ErrorAction SilentlyContinue) {
        Say "White Knuckle is running. Close the game, then press Enter." "Yellow"
        [void](Read-Host)
    }
}

function Copy-Item-Safe($src, $dst) {
    $parent = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $src -Destination $dst -Force
}

try {
    Say "=== Belay for White Knuckle ===" "Cyan"

    if (-not (Test-GameDir $GameDir)) { $GameDir = Find-GameDir }
    if (-not $GameDir) { $GameDir = Ask-GameDir }
    if (-not $GameDir) { Say "Cancelled. Nothing was changed." "Yellow"; exit 1 }
    Say "Game folder: $GameDir" "Green"

    Wait-GameClosed

    $plugins  = Join-Path $GameDir "BepInEx\plugins"
    $belayDir = Join-Path $plugins "Belay"

    if ($Uninstall) {
        # Belay's launch updater (a BepInEx patcher it installs itself).
        $patcherDir = Join-Path $GameDir "BepInEx\patchers\Belay"
        if (Test-Path -LiteralPath $patcherDir) { Remove-Item -LiteralPath $patcherDir -Recurse -Force }
        if (Test-Path -LiteralPath $belayDir) {
            Remove-Item -LiteralPath $belayDir -Recurse -Force
            Say "Belay removed. BepInEx and your other mods were left alone." "Green"
        } else {
            Say "Belay isn't installed in this folder." "Yellow"
        }
        exit 0
    }

    if (-not (Test-Path -LiteralPath $Payload)) { throw "The 'payload' folder is missing. Extract the whole zip first, then run Install Belay.bat again." }
    Get-ChildItem -LiteralPath $Payload -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

    # 1. BepInEx (mod loader). Keep an existing install so other mods and settings are untouched.
    $hasBepInEx = (Test-Path -LiteralPath (Join-Path $GameDir "BepInEx\core\BepInEx.dll")) -and
                  (Test-Path -LiteralPath (Join-Path $GameDir "winhttp.dll"))
    if ($hasBepInEx) {
        Say "BepInEx is already installed. Keeping it."
    } else {
        Say "Installing BepInEx (mod loader)..."
        foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")) {
            $src = Join-Path $Payload $f
            if (Test-Path -LiteralPath $src) { Copy-Item-Safe $src (Join-Path $GameDir $f) }
        }
        $coreSrc = Join-Path $Payload "BepInEx\core"
        Get-ChildItem -LiteralPath $coreSrc -File | ForEach-Object {
            Copy-Item-Safe $_.FullName (Join-Path $GameDir "BepInEx\core\$($_.Name)")
        }
    }

    # 2. Belay itself.
    Say "Installing Belay..."
    Copy-Item-Safe (Join-Path $Payload "BepInEx\plugins\Belay\Belay.dll") (Join-Path $belayDir "Belay.dll")

    # 3. Other multiplayer mods conflict with Belay. Offer to switch them off (reversible).
    if (Test-Path -LiteralPath $plugins) {
        $others = Get-ChildItem -LiteralPath $plugins -Recurse -Filter "*.dll" -File |
                  Where-Object { $_.FullName -notlike "$belayDir*" -and $_.Name -match "multi ?player|WKMP|WKMultiMod" }
        foreach ($dll in $others) {
            Say ""
            Say "Found another multiplayer mod: $($dll.Name)" "Yellow"
            $answer = Read-Host "It conflicts with Belay. Turn it off? (Y/n)"
            if ($answer -notmatch '^[nN]') {
                Move-Item -LiteralPath $dll.FullName -Destination ($dll.FullName + ".disabled") -Force
                Say "Turned off. You can turn it back on from the Belay menu's Mods tab." "Green"
            }
        }
    }

    Say ""
    Say "Done! Belay is installed." "Green"
    Say "Start White Knuckle and click 'Belay (F7)' in the bottom-right of the main menu."
    Say "Host a lobby and invite friends through Steam. Everyone needs the same Belay version."
    exit 0
}
catch {
    Say ""
    Say "Install failed: $($_.Exception.Message)" "Red"
    Say "Nothing else was changed. You can also install by hand: see 'Belay README.txt'."
    exit 1
}
