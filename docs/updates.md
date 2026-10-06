# Releases and in-app updates

The mechanism follows Repo Watch's.

## For maintainers: publishing a release

1. Set `<Version>` in `Directory.Build.props` (e.g. `0.2.0`).
2. Add a `## [0.2.0] - YYYY-MM-DD` section to `CHANGELOG.md`, written for people using the app. It becomes the release notes and is what the update window shows.
3. Merge to `main`, then push a tag for that version on `main`:

   ```bash
   git tag v0.2.0
   ```

   ```bash
   git push origin v0.2.0
   ```

4. The **Release** workflow (`.github/workflows/release.yml`) checks that the tag matches the version and is on `main`, takes the notes from `CHANGELOG.md` (`scripts/release-notes.ps1`), runs `scripts/publish-windows.ps1` (tests included), and creates the GitHub release with `ArbetsWatch-<version>-win-x64.zip` and its `.sha256`.

Requirements: the repository must be **public**, so the app can read releases without signing in. Draft and pre-release releases are ignored by the app. Only `vX.Y.Z` tags count.

## For users: how updates work

- **Checking:** 30 seconds after startup and then once a day, ArbetsWatch asks GitHub anonymously for the releases of `PantaKoda/arbets-watch`. **Check for updates** in Settings asks right away.
- **Indicator:** when a newer release exists, an **UPDATE** pill appears in the header. It opens the update window with the notes of every release since your version, newest first (shown as plain text).
- **Install update** (only when you click it):
  1. Downloads the release's `.sha256` file and zip, only from this repository's release download URLs on github.com, over HTTPS (at most 500 MB; a download that receives nothing for 30 s gives up).
  2. Checks the zip's SHA-256 against the published checksum. On a mismatch nothing is installed.
  3. Unpacks it into the data folder (`%LOCALAPPDATA%\ArbetsWatch\updates`) and checks that it contains ArbetsWatch at that version (`release.json`).
  4. Starts the new copy as the updater and quits. The updater waits for ArbetsWatch to exit, moves the install folder aside as `<folder>.previous`, copies the new version in, and starts it. The new version says "Updated to X from Y" and removes the staging folder.
  5. If moving or copying fails, the previous folder is put back and started again with a message; the reason is in `logs\update.log` in the data folder.
- **Cancel:** the download can be cancelled from the update window; nothing is changed.
- **Kept:** the database (saved ads, read state, checkpoints), preferences and logs live in `%LOCALAPPDATA%\ArbetsWatch`, outside the app folder.
- **Layout requirement:** the data folder must not be inside the app folder. Extracting the zip directly into `%LOCALAPPDATA%` would make the app folder `%LOCALAPPDATA%\ArbetsWatch`, the same as the data folder; in that layout the app explains that it can't update itself. Use `%LOCALAPPDATA%\Programs\ArbetsWatch`.
- **Rolling back:** quit ArbetsWatch, delete the install folder and rename `<folder>.previous` back.
- **Builds from source** never replace themselves; only a folder extracted from a release zip has the `release.json` marker that allows it. Their update window offers the GitHub page instead.

## Testing an update locally

`ARBETSWATCH_UPDATE_REPOSITORY=<owner>/<repo>` points a copy at another repository's releases. `pwsh scripts/publish-windows.ps1 -Version 0.1.1` builds a zip with a different version from the same source.

## Security notes

- The checksum proves the download is the file published with the release, and HTTPS from github.com proves it came from GitHub. Neither is a **code signature**: someone able to publish a release on the repository could publish a malicious one. Authenticode signing and signed update metadata remain future work.
- Release notes are untrusted text: shown as plain text, never rendered as markup.
- Update checks send no credentials. One check a day stays far below GitHub's 60 anonymous requests per hour.
