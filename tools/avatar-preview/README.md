# Avatar preview

Renders the Belay climber (teammate model) without launching the game, so changes to
`src/Belay/Players/Avatar/AvatarDesign.cs` can be reviewed as a picture.

```
dotnet run -p:GameDir="<White Knuckle install>"   # writes preview.json (needs UnityEngine.CoreModule.dll)
npm i three@0.160.0                               # once
node render.js                                    # writes preview.png (Playwright + Chromium)
```

The preview uses the design's full 1.8 m proportions; in game the model is scaled to the
player's 1.5 m capsule (`ClimberAvatar.Scale`).

## Hand skins

`SKINS_DIR=<dir> dotnet run ...` adds the game's hand cosmetics on the 3D hands (render with
`VIEWS='[["skins",0,"..."]]'`). Build `<dir>` from your own install with `scripts/make_skins.py`.
Skin art comes from the local game files and is never committed.

## Held items

Teammates hold a hand-sized copy of each item's world model, posed by `HeldGrip.cs` (the rapier is
our own model, `ItemModels.cs`). To preview the grips with the real game models (never committed):
pull the item models out of the game's Addressables bundle
(`StreamingAssets/aa/StandaloneWindows64/defaultlocalgroup_assets_all_*.bundle`) with UnityPy
(`python3 scripts/export_items.py Item_Hammer,Item_Piton,...` then `python3 scripts/items_to_preview.py`),
then `ITEMS_JSON=items_preview.json dotnet run ...` adds a `hold:<item>` figure (left hand out in front,
right hand at the side) and a `grip:<item>` close-up of a right fist. `OLD=1` poses them the way
0.7.34 and earlier did, for comparisons.

## Totem

`TOTEM=1 dotnet run ...` adds the co-op disk totem (`src/Belay/Items/TotemLook.cs`): a `totem` figure at world
size and a `hold:Item_Floppy_T1` / `grip:Item_Floppy_T1` figure, and writes its first-person hand sprite to
`totem-sprite.raw` (128x128 RGBA, rows bottom-up).
