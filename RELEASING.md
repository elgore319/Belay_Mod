# Releasing Belay

1. **Bump the version in a PR.**
   - Set the version in `src/Belay/Belay.csproj` (`<Version>`) and `src/Belay/Plugin.cs` (`Version`).
   - Build it, then copy `src/Belay/bin/Release/Belay.dll` to `release/Belay.dll`.
   - Write `last_release_notes.md`.
   - Merge the PR to main.
2. **Run the release workflow.** On GitHub, open Actions > **Release** > **Run workflow** on main, and enter `tag = vX.Y.Z`. You can also tick or untick pre-release. The workflow creates the tag and the release itself. Pushing a `vX.Y.Z` tag works too.
3. **Wait about a minute.** The workflow checks that the tag, `Belay.csproj`, `Plugin.cs` and `release/Belay.dll` all agree, and fails on purpose if they don't. It then builds `Belay-X.Y.Z-installer.zip` (with BepInEx) and `Belay-X.Y.Z-mod-only.zip` with `installer/make-package.sh`, and publishes them with `last_release_notes.md` as the release text.

Players' games update themselves. On launch, Belay reads the releases of the public releases repo (`elgore319/belay`, the `UpdateRepo` setting), falling back to this repo while that one has no releases. It downloads the newest release's `Belay.dll` asset and swaps it in for the next start. Keep the asset name `Belay.dll`, and always bump the version, because the updater only installs versions that are higher than the one running.

Rules:
- Never run the release before the version bump is merged, or the release will report the old version.
- A tag can't be reused. If a release goes wrong, move to the next version number.
- `release/Belay.dll` is committed because GitHub's runners can't compile Belay: it builds against the game's DLLs, which must never be committed.

## Public releases repo and download page

This source repo can be private. Players get Belay from a separate public repo, `elgore319/belay`, which holds only release files and the download page (no source code).

- The **Release** workflow publishes every release here and, when the `RELEASES_TOKEN` secret is set, to the public repo too.
- The **Site** workflow pushes `site/`, `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` to the public repo's `gh-pages` branch whenever they change on main. GitHub Pages serves it at https://elgore319.github.io/belay/.

One-time setup (owner only):
1. Create the public repo `elgore319/belay` on GitHub, ticking "Add a README file" so it has a default branch.
2. Create a fine-grained personal access token (Settings > Developer settings > Fine-grained tokens) for that repo only, with **Contents: Read and write**.
3. In this repo, add it as the Actions secret `RELEASES_TOKEN`. (A different public repo name goes in the Actions variable `PUBLIC_REPO`.)
4. Run Actions > **Site** > Run workflow, then in the public repo turn on Settings > Pages > Deploy from branch `gh-pages`, folder `/`.
5. Publish the next release. Once it's in the public repo, players on the new version update from there.

Only make this repo private after a release with the new updater (0.6.x with `DefaultUpdateRepo`) has gone out and players have updated. Older versions read this repo and stop finding updates once it's private; they can still install by hand from the download page.
