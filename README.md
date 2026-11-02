# SWS2 CS2 Rock The Vote

Player map reservations and consent-based map changes for SwiftlyS2. The map list and map changes come from `sws2-cs2mapmanager`; `sws2-cs2menus` displays the reservation menu.

## Requirements

- SwiftlyS2 1.5.0 or newer
- `sws2-cs2menus`
- `sws2-cs2mapmanager`
- MultiAddonManager for automatic server-side Workshop map downloads

Load the shared menu and map manager plugins before this plugin.

## How it works

1. Any player can use `!yd` to open the map reservation menu. The menu shows the Workshop preview, title and creator when Steam metadata is available.
2. `!yd <map name>` reserves a map directly. The reservation is announced in chat and a later reservation replaces it.
3. Players use `!rtv` to agree to the reserved map. If nobody has reserved one, the next map in mapmanager's configured order is selected.
4. One player is enough on a one-player server; two players are required when there are two; two are required when there are three; servers with four or more players require at least 80% of connected human players. Each player counts once.
5. Once the threshold is reached, the map is passed to `sws2-cs2mapmanager` after `ChangeDelaySeconds`. Disconnecting removes that player's agreement.

## Configuration

Edit `configs/plugins/sws2-cs2rockthevote/config.jsonc`:

```jsonc
{
  "sws2-cs2rockthevote": {
    "Enabled": true,
    "ChangeDelaySeconds": 3
  }
}
```

Map names and Workshop IDs are configured only in `sws2-cs2mapmanager`. Workshop titles, creators and preview images are read from Steam when a player opens `!yd`; chat reservation and RTV commands work if that metadata request is unavailable. Changes are hot-reloaded.

Workshop thumbnails are rendered by the `hudlayout` menu backend. The classic menu fallback still shows the map title and author text.

## Build

Requires .NET 10 and access to the sibling `sws2-cs2menus` and `sws2-cs2mapmanager` contract projects.

```sh
dotnet restore sws2-cs2rockthevote.csproj
dotnet build sws2-cs2rockthevote.csproj -c Release -warnaserror
dotnet publish sws2-cs2rockthevote.csproj -c Release --no-restore
```

Pull requests and `develop` pushes build downloadable test packages. Merging to `main` builds a versioned package and publishes a GitHub Release; dispatching the release workflow allows choosing patch, minor, or major.

## Versioning and releases

This repository uses a `develop` -> `main` flow:

- Pull requests run CI: `dotnet format`, a Release build with warnings as errors, package validation and a test-package artifact.
- Each PR run gets a traceable preview version such as `1.0.1-pr.12.37`.
- For pull requests from this repository, CI also publishes or updates a pre-release named `vX.Y.Z-pr.<PR>.<run>`. It is deleted when the PR closes.
- When a PR is merged into `main`, the release workflow computes the next patch version from the latest stable `vX.Y.Z` tag, injects it into `src/VersionInfo.cs`, builds the package, creates the tag and publishes a GitHub Release.
- A manual release can be started with `workflow_dispatch` and a `patch`, `minor` or `major` bump.

No manual version editing is required. The value committed in `src/VersionInfo.cs` is only a fallback for local builds.
