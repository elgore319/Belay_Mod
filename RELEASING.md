# Releasing Belay

1. **Bump the version in a PR.**
   - Set the version in `src/Belay/Belay.csproj` (`<Version>`) and `src/Belay/Plugin.cs` (`Version`).
   - Build it, then copy `src/Belay/bin/Release/Belay.dll` to `release/Belay.dll`.
   - Write `last_release_notes.md`.
   - Merge the PR to main.
2. **Run the release workflow.** On GitHub, open Actions > **Release** > **Run workflow** on main, and enter `tag = vX.Y.Z`. You can also tick or untick pre-release. The workflow creates the tag and the release itself. Pushing a `vX.Y.Z` tag works too.
3. **Wait about a minute.** The workflow checks that the tag, `Belay.csproj`, `Plugin.cs` and `release/Belay.dll` all agree, and fails on purpose if they don't. It then builds `Belay-X.Y.Z-installer.zip` (with BepInEx) and `Belay-X.Y.Z-mod-only.zip` with `installer/make-package.sh`, and publishes them with `last_release_notes.md` as the release text.

**Where releases go.** Public releases live in the team repo, [BrassWhuckle/Belay](https://github.com/BrassWhuckle/Belay). The workflow copies the source there as one commit (no history; `.github/`, `release/` and `last_release_notes.md` stay here), publishes the release there, then publishes it here too. It signs in as the team's GitHub App using the `ORG_APP_ID` and `ORG_APP_PRIVATE_KEY` secrets of this repo. Before publishing anything it fails if a personal name shows up in the copied files, the release notes or `release/Belay.dll`, so keep names out of code comments and notes.

Players' games update themselves. Belay.dll carries a small BepInEx patcher (`src/BelayUpdater`, built automatically with Belay) and installs it to `BepInEx\patchers\Belay`. On every launch that patcher reads the releases of `Plugin.DefaultUpdateRepo` (BrassWhuckle/Belay; that repo must be public) and replaces `Belay.dll` with the newest release's `Belay.dll` asset before Belay loads, so one start is enough. While the game runs, Belay also rechecks every 10 minutes and offers a restart when a new release is downloaded. Keep the asset name `Belay.dll`, and always bump the version, because the updater only installs versions that are higher than the one running.

Rules:
- Never run the release before the version bump is merged, or the release will report the old version.
- A tag can't be reused. If a release goes wrong, move to the next version number.
- `release/Belay.dll` is committed because GitHub's runners can't compile Belay: it builds against the game's DLLs, which must never be committed.
