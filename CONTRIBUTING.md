# Contributing

Use .NET 10. The plugin targets Jellyfin 12; keep the Jellyfin.Controller and Jellyfin.Model package versions together. Keep the GUID consistent across Plugin.cs, the settings page and manifest.json.

Run the release build, tests, and formatting checks before submitting changes. Lint runs the unit tests with coverage; the Settings page workflow runs the browser tests. Tests cover concurrent playback, settling and cancellation, bitrate selection, recovery after failures, cookie and API-key authentication, the hosted event pipeline, and the status and connection-test API.

```sh
dotnet test tests/Jellyfin.Plugin.TorrentThrottle.Tests --configuration Release
npm --prefix tests/config-page ci
(cd tests/config-page && npx playwright install chromium && npm test)
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

Screenshots in `docs/images` come from the shared harness in the catalogue repository, using synthetic settings: `../jellyfin-plugins/scripts/capture-screenshots.sh --only=torrent-throttle`.

## Coverage

Use the existing Coverlet collector to measure the plugin's .NET code in Release configuration, matching CI:

```sh
dotnet test tests/Jellyfin.Plugin.TorrentThrottle.Tests --configuration Release \
  --collect:"XPlat Code Coverage" --results-directory TestResults
```

The command prints the path to `TestResults/<run-id>/coverage.cobertura.xml`. Its root `line-rate` multiplied by 100 is the line coverage percentage; `lines-covered` / `lines-valid` and `branches-covered` / `branches-valid` give the underlying counts. Use the report from the latest run, since older reports remain in their own run directories.

The README badge is maintained by hand from that line coverage, rounded to a whole percent, following `jellyfin-plugin-imdb-ratings`. Update it when changing tests or plugin code. Aim for 90–95% line coverage with tests that exercise observable behavior. The badge measures .NET code only; the settings page's JavaScript is tested separately by the browser harness.

The Lint workflow publishes line and branch coverage in its job summary and uploads the Cobertura report as the `coverage` artifact. Generated reports are ignored by Git.

## Dependency updates

`.github/renovate.json5` and `.github/workflows/renovate.yml` match Transcode Guard exactly. Renovate uses a GitHub App token so its pull requests trigger CI. Minor and patch updates auto-merge after checks pass and a three-day release-age delay. Major updates require Dependency Dashboard approval. Jellyfin package updates are grouped, require approval, and never auto-merge. Security fixes are sourced from Dependabot alerts and osv.dev.

Repository setup requires:

- `RENOVATE_APP_CLIENT_ID` Actions variable: the existing Renovate GitHub App client ID.
- `RENOVATE_APP_PRIVATE_KEY` Actions secret: that app's private key.
- Installation access for the app to this repository, with the same permissions as the other plugins.
- Dependabot vulnerability alerts enabled and Dependabot's separate automated security-update PRs disabled, so Renovate handles those fixes once.
- `PLUGIN_REPO_TOKEN` Actions secret for release notifications to `voc0der/jellyfin-plugins`.

Secrets cannot be copied out of another repository through the GitHub API. Set them from the existing credential source; do not put them in files committed to this repository.

## Releases

Pull requests and pushes to main build and test the plugin. A release is created only when a pull request carrying the `release` label is merged. A direct push or unlabelled merge does not release. The workflow increments the four-part version, packages the plugin DLL, updates the manifest, creates the release, and dispatches a catalogue rebuild.

The initial manifest intentionally has an empty versions list until a release exists. Do not invent release checksums or URLs. Keep the manifest name, assembly name and GUID stable after the first release because Jellyfin uses them for installation and configuration paths.

The catalogue repository's generated manifest and README table should be rebuilt by its CI. Add input entries and screenshot fixtures there; keep screenshot PNGs in this repository.

## Contribution scope

Use focused branches and commits. Changes to another torrent client should implement the client abstraction and add authentication and restoration tests.

This project uses LLM-assisted development. Contributions generated with AI assistance are welcome; review and test all code before submitting.
