Fixes the auto-updater. Some players on 0.5.0 were told "up to date" even though 0.5.1 was out.

## Fixed
- **Updater missed new releases.** The update check now reads each release's download link directly instead of parsing GitHub's full release list, skips any cached copy of that list, and writes what it found to LogOutput.log ("Update check: ... newest X, you have Y").

## If you're on 0.5.0
The 0.5.0 updater can't see this release, so update once by hand:
1. Close the game.
2. Download **Belay-0.5.2-installer.zip** below, extract it, and double-click **Install Belay.bat**. It replaces your old Belay.

From 0.5.1 on, updates install themselves on launch and apply after a restart.

Everything from 0.5.1 is included (playtest fixes, shared controls, tileset checker, climber avatars, test bots). Everyone in a lobby needs the same Belay version.
