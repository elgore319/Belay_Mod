A fix for the test bot. Your game updates to this automatically on the next launch, then asks you to restart.

## Fixed
- **Opening the bot's POV window launched you out of bounds.** The POV draws a stand-in for you in the bot's view, and since teammates became solid it had a solid body sitting inside you. The stand-in is never solid now.
- **Bots and the test client no longer step into you.** Teammates are solid, so a following bot (or the test client's autopilot) waits instead of moving onto your spot, and "Bring to me" places the bot in a clear spot beside you instead of inside you or a wall.

## Still on 0.5.0 or 0.5.1?
Download **Belay-0.5.12-installer.zip** below, extract it and double-click **Install Belay.bat**. Everyone in a lobby needs the same Belay version.
