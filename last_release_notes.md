## 0.6.22

Updates now take one restart, not several.

- **Updates install at launch.** Belay now puts a small launch updater in `BepInEx\patchers\Belay`. Each time the game starts it grabs the newest release before Belay loads, so you're on the latest version straight away. Restarting once after this update turns it on. The first time it updates you, you'll see "Updated to Belay X".
- **It keeps checking while you play.** With auto update on, Belay checks every 10 minutes. If a new release comes out mid-session it's downloaded, and the main menu and pause menu show a popup with **Restart now** (closes and relaunches the game through Steam) or **Later**.
- **Version mismatch notices.** If you join someone on a different Belay version you get an error notice saying who has which version. If the host is newer, your game starts updating right away. The host also gets a notice when someone older joins.
- The uninstaller also removes the launch updater.
