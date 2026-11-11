## Belay 0.6.0

Playtest fixes:
- **The run ends when everyone is dead.** Test bots no longer keep a run going once every real player is down (bots can't revive anyone). Added backup checks too: a client ends the run itself if the host's message never arrives, a leftover disk save can't turn the game over into a disk revive, and if the game-over screen doesn't start it is started directly.
- **Blast doors use the door close countdown.** Big doors whose close trigger doesn't go through a normal door now wait for the countdown from the settings (10 to 20 s) and then shut for everyone at once.
- **Terminals no longer pull teammates.** When a teammate's press or trigger is replayed on your PC, nothing in it can sit you down at a terminal.
