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
