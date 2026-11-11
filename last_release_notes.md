## Belay 0.5.23

**The vote terminal now actually appears, and the game's own terminals no longer start votes.**

- In 0.5.22 the vote terminal never appeared, because its placement check was too strict. Now Belay tries several spots: beside the terminal on either side at a few distances, and back to back with it across the spot where you stand. It uses the first clear spot. If no spot is clear, it uses the least blocked one, so there is always a vote terminal.
- Sitting at a regular terminal never starts a shortcut vote now. Votes happen only at the "SHORTCUT VOTE" terminal, or when someone tries to open a shortcut nobody has voted on yet.
