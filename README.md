# SWS2 CS2 Rock The Vote

Player-requested map voting for SwiftlyS2. RTV reads its map choices from `sws2-cs2mapmanager`, displays ballots with `sws2-cs2menus`, and requests the winning map through the same shared manager.

## Requirements

- SwiftlyS2 1.5.0 or newer
- `sws2-cs2menus`
- `sws2-cs2mapmanager`
- MultiAddonManager for automatic server-side Workshop map downloads

Load the shared menu and map manager plugins before this plugin.

## How it works

1. A player uses `!rtv` to request a vote.
2. A vote starts after both the minimum request count and configured fraction of connected human players are reached.
3. The plugin randomly selects up to the configured number of maps, excluding the current map.
4. Eligible players receive a clickable HUD ballot. Each connected player can vote once; disconnecting removes that player's request and vote.
5. The map with the most votes wins. Ties are selected randomly. With no votes, the current map remains.
6. After the configured delay, the winner is passed to `sws2-cs2mapmanager`, which handles local and Workshop maps.

## Configuration

Edit `configs/plugins/sws2-cs2rockthevote/config.jsonc`:

```jsonc
{
  "sws2-cs2rockthevote": {
    "Enabled": true,
    "MinimumPlayers": 4,
    "MinimumRequests": 3,
    "RequestRatio": 0.6,
    "OptionCount": 5,
    "VoteDurationSeconds": 20,
    "RequestCooldownSeconds": 60,
    "VoteCooldownSeconds": 180,
    "ChangeDelaySeconds": 3
  }
}
```

Map names and Workshop IDs are configured only in `sws2-cs2mapmanager`. Changes are hot-reloaded.

## Build

Requires .NET 10 and access to the sibling `sws2-cs2menus` and `sws2-cs2mapmanager` contract projects.

```sh
dotnet restore sws2-cs2rockthevote.csproj
dotnet build sws2-cs2rockthevote.csproj -c Release -warnaserror
dotnet publish sws2-cs2rockthevote.csproj -c Release --no-restore
```

Pull requests and `develop` pushes build downloadable test packages. Merging to `main` builds a versioned package and publishes a GitHub Release; dispatching the release workflow allows choosing patch, minor, or major.
