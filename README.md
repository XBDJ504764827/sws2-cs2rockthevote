# SWS2 CS2 Rock The Vote

Player map reservations and consent-based map changes for SwiftlyS2. The map list and map changes come from `sws2-cs2mapmanager`; `sws2-cs2menus` displays reservation and voting menus.

## Requirements

- SwiftlyS2 1.5.0 or newer
- `sws2-cs2menus`
- `sws2-cs2mapmanager`
- MultiAddonManager for automatic server-side Workshop map downloads

Load the shared menu and map manager plugins before this plugin.

## How it works

1. Any player can use `!yd` to open the map reservation menu. The Custom HUD opens immediately with map names, separate CKZ NUB/PRO and VNL NUB/PRO tier badges, packaged map previews and cached Workshop title/creator details when available. Four maps fit on each page; clicking a map reserves it and closes the menu.
2. `!yd <map name>` reserves a map directly. The reservation is announced in chat and a later reservation replaces it.
3. Players use `!rtv` to agree to the reserved map. If nobody has reserved one, reaching the RTV threshold opens a map voting HUD for every connected human player, using the same pictures, ratings and pagination as `!yd`. The current map is excluded; map list order never chooses the winner.
4. One player is enough on a one-player server; two players are required when there are two; two are required when there are three; servers with four or more players require at least 80% of connected human players. Each player counts once.
5. With a reservation, reaching the threshold starts mapmanager's `ChangeDelaySeconds` countdown as before.
6. Without a reservation, each player present when the ballot starts can choose one map. When all have chosen, the highest vote count wins immediately; tied leaders are selected uniformly at random. One player chooses and changes immediately; two choosing different maps produce a random choice between those maps; with three players, three different choices are random and a 2–1 split picks the map with two votes. Ballot results use a zero-second mapmanager countdown; a missing Workshop map still needs to download first.
7. The ballot lasts at most `MapVoteDurationSeconds` (default 30, clamped to 5–120). At the deadline, only submitted votes count; no votes cancels the ballot. Closing the HUD abstains until the player reopens it with `!rtv`. Repeat selections are ignored. New arrivals wait for the next ballot. Disconnecting removes that player's agreement or ballot and can finish voting if everyone remaining has selected. Reservations cannot replace an active ballot.

## Configuration

Edit `configs/plugins/sws2-cs2rockthevote/config.jsonc`:

```jsonc
{
  "sws2-cs2rockthevote": {
    "Enabled": true,
    "ChangeDelaySeconds": 5,
    "MapVoteDurationSeconds": 30,
    "WorkshopMetadata": {
      "Enabled": true,
      "TimeoutSeconds": 30
    }
  }
}
```

`WorkshopMetadata.Enabled` controls optional Steam HTTP lookups. Set it to `false`
on servers without reliable access to `api.steampowered.com` / `steamcommunity.com`;
map reservations, RTV and mapmanager downloads do not depend on these lookups.
`TimeoutSeconds` applies to each request (1-120 seconds, default 30). Both settings
are hot-reloaded and take effect on subsequent requests; an existing request may
continue until its deadline.

Map names and Workshop IDs are configured only in `sws2-cs2mapmanager`. Steam details are fetched in the background on startup and when a player opens
`!yd`. The menu uses a cached snapshot immediately; reopen it after a successful
fetch to see updated details. A network failure emits one concise warning,
stops remaining batches and waits five minutes before allowing another refresh
on a later request. Maps already fetched remain cached. Changes are hot-reloaded.

All four ratings come from mapmanager's shared `MapInfo.CkzNubTier`, `CkzProTier`, `VnlNubTier` and `VnlProTier` fields. Configure `"CKZ": {"NUB": null, "PRO": null}` and `"VNL": {"NUB": null, "PRO": null}` inside each map object. Replace each `null` with the actual category rating. NUB means checkpoints (存点); PRO means no checkpoints (裸跳). Unknown categories are labeled separately and never borrow ratings from another category or from old scalar mode ratings. The admin map list uses the same four fields.

The HUD must be enabled in `sws2-cs2menus` (`Menu.Backend: "hudlayout"`, or `"auto"` with the addon mounted). The classic fallback still provides map reservations when the HUD resources are unavailable.

### Map pictures

Custom HUD layouts accept Panel/Label/Button, so map pictures use **local CSS backgrounds**, not remote Image panels or client JavaScript. The HUD source already contains real Workshop previews for the 15 requested maps. Missing pictures show a localized placeholder and never block reservations. Administrator Map management uses the same screenshot assets and four rating badges through the shared MapPicker renderer.

To regenerate pictures for your actual catalog, run from the sibling menus repository:

```sh
python3 tools/build_map_thumbnails.py --config /path/to/mapmanager/config.jsonc
```

Then compile the updated XML, CSS and images from `sws2-cs2menus/workshop/` in CS2 Workshop Tools and replace your locally mounted addon or update the Workshop addon. Both server and clients need the compiled resources. Ratings are server configuration and do not require republishing resources.

The tool supports `--offline` and `--images images.json` for local screenshots. See [the HUD addon instructions](../sws2-cs2menus/workshop/README.md#map-reservation-pictures). Runtime Steam metadata remains optional and is refreshed separately; it does not dynamically replace packaged screenshots.

## Build

Requires .NET 10 and access to the sibling `sws2-cs2menus` and `sws2-cs2mapmanager` contract projects.

```sh
dotnet restore sws2-cs2rockthevote.csproj
dotnet build sws2-cs2rockthevote.csproj -c Release -warnaserror
dotnet publish sws2-cs2rockthevote.csproj -c Release --no-restore
```

Pull requests and `develop` pushes build downloadable test packages. Merging to `main` builds a versioned package and publishes a GitHub Release; dispatching the release workflow allows choosing patch, minor, or major.

## Voting regression checks

```sh
dotnet run --project tests/VoteChecks/VoteChecks.csproj -c Release
```

Checks cover single-player completion, two/three-way ties, majority results,
duplicate/invalid selections, partial votes, disconnects and independent ballot sessions.

## Metadata regression checks

The checks simulate the nested timeout reported by HttpClient, a stalled request,
plugin shutdown, cached responses and disabled lookups without contacting Steam:

```sh
dotnet run --project tests/MetadataChecks/MetadataChecks.csproj -c Release
```

## Versioning and releases

This repository uses a `develop` -> `main` flow:

- Pull requests run CI: `dotnet format`, a Release build with warnings as errors, package validation and a test-package artifact.
- Each PR run gets a traceable preview version such as `1.0.1-pr.12.37`.
- For pull requests from this repository, CI also publishes or updates a pre-release named `vX.Y.Z-pr.<PR>.<run>`. It is deleted when the PR closes.
- When a PR is merged into `main`, the release workflow computes the next patch version from the latest stable `vX.Y.Z` tag, injects it into `src/VersionInfo.cs`, builds the package, creates the tag and publishes a GitHub Release.
- A manual release can be started with `workflow_dispatch` and a `patch`, `minor` or `major` bump.

No manual version editing is required. The value committed in `src/VersionInfo.cs` is only a fallback for local builds.

## Administrator map control

RTV countdowns belong to the shared map manager. Administrator cancel/pause actions also cancel any active map ballot and close its HUDs. Administrator **Cancel pending change now** clears existing votes and cancels the pending switch, including a switch waiting for a Workshop download. **Pause map changes now** also rejects new RTV requests. Resuming requires fresh votes; the reserved map is retained. Turning RTV off or unloading it cancels only its own pending request. Pause resets on map unload/restart.

Install matching mapmanager, menus, admin and rockthevote packages together and restart the server. `MapVoteDurationSeconds` is optional; existing configurations use the default 30-second ballot deadline.

RTV for a reserved map uses a **5-second** countdown by default. An unreserved ballot requests the winning map immediately without this countdown. `ChangeDelaySeconds` is clamped to 5–30 seconds, so an existing setting of 3 automatically runs as 5 without regenerating configuration. Administrator panel changes use a separate **3-second** countdown. At the deadline, an installed map changes immediately; a missing Workshop map begins downloading immediately and switches only after download completion. Both the countdown and automatic switch after download remain cancellable.
