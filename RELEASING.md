# Releasing Belay

1. **Bump the version in a PR.**
   - Set the version in `src/Belay/Belay.csproj` (`<Version>`) and `src/Belay/Plugin.cs` (`Version`).
   - Build it, then copy `src/Belay/bin/Release/Belay.dll` to `release/Belay.dll`.
   - Write `last_release_notes.md`.
   - Merge the PR to main.
2. **Run the release workflow.** On GitHub, open Actions > **Release** > **Run workflow** on main, and enter `tag = vX.Y.Z`. You can also tick or untick pre-release. The workflow creates the tag and the release itself. Pushing a `vX.Y.Z` tag works too.
3. **Wait about a minute.** The workflow checks that the tag, `Belay.csproj`, `Plugin.cs` and `release/Belay.dll` all agree, and fails on purpose if they don't. It then builds `Belay-X.Y.Z-installer.zip` (with BepInEx) and `Belay-X.Y.Z-mod-only.zip` with `installer/make-package.sh`, and publishes them with `last_release_notes.md` as the release text.

Players' games update themselves. On launch, Belay reads this repo's releases (the repo must be public), downloads the newest release's `Belay.dll` asset, and swaps it in for the next start. Keep the asset name `Belay.dll`, and always bump the version, because the updater only installs versions that are higher than the one running.

Rules:
- Never run the release before the version bump is merged, or the release will report the old version.
- A tag can't be reused. If a release goes wrong, move to the next version number.
- `release/Belay.dll` is committed because GitHub's runners can't compile Belay: it builds against the game's DLLs, which must never be committed.
