## Belay 0.5.24

**The Belay menu (F7) now looks like White Knuckle's own menus.**

- The menu uses the game's pixel font. The game only ships that font baked at one size, so the first time you start the game Belay traces it into a font file in `BepInEx/cache/Belay/` and uses it from then on. Nothing of the game's is changed.
- New look: black panels with a light grey pixel frame, a black title bar with a red marker, white text, and red when you point at something. The selected tab and the switched-on buttons are shown inverted, and checkboxes, sliders, text boxes and scrollbars match.
- The window is a bit wider so the bigger text fits. Every tab and setting is still there, and there are no new keys.
- Only Belay's own windows use this look, so other mods' menus are unchanged. If the game font can't be found, the menu keeps the new colours with Unity's default font.
- The bot POV window now opens in the top-right corner, so it doesn't cover the Belay menu.
