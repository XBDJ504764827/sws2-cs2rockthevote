<div align="center">
  <h2><strong>SWS2 CS2 Rock The Vote</strong></h2>
  <h3>Player-requested map voting for SwiftlyS2.</h3>
</div>

<p align="center">
  <img src="https://img.shields.io/github/downloads/XBDJ504764827/sws2-cs2rockthevote/total" alt="Downloads">
  <img src="https://img.shields.io/github/v/release/XBDJ504764827/sws2-cs2rockthevote" alt="Release">
  <img src="https://img.shields.io/github/license/XBDJ504764827/sws2-cs2rockthevote" alt="License">
</p>

`sws2-cs2rockthevote` adds a classic Rock the Vote flow to SwiftlyS2 servers.
Players request a vote with `!rtv`, vote through the shared
[`sws2-cs2menus`](https://github.com/XBDJ504764827/sws2-cs2menus) menu service,
and the winning map is changed through the shared
[`sws2-cs2mapmanager`](https://github.com/XBDJ504764827/sws2-cs2mapmanager)
catalog, including Workshop map downloads.

## Features

- Player-requested map votes with a configurable request ratio and minimum player count
- Random ballot of configured maps, excluding the current map
- Ballot rendered by the shared menu service (custom HUD or center HTML fallback)
- Per-player request cooldown and cooldown between finished votes
- Automatic vote timeout, disconnect cleanup and map-unload cleanup
- Map changes delegated to `sws2-cs2mapmanager`, including Workshop download progress
- Hot-reloadable `config.jsonc`
- Translations: English, 简体中文

## Requirements

- [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2) **1.5.0 or newer**
- [`sws2-cs2menus`](https://github.com/XBDJ504764827/sws2-cs2menus) loaded
- [`sws2-cs2mapmanager`](https://github.com/XBDJ504764827/sws2-cs2mapmanager) loaded
  and configured with at least two eligible maps
- .NET 10 SDK (only needed to build from source)

Plugins resolve each other through SwiftlyS2's shared interface manager during
`UseSharedInterface`, so plugin load order does not matter as long as both
dependency plugins loaded successfully.

## Installation

1. Download the latest release zip.
2. Extract the `sws2-cs2rockthevote` folder into
   `csgo/addons/swiftlys2/plugins/`:

```
csgo/addons/swiftlys2/plugins/
└── sws2-cs2rockthevote/
    ├── sws2-cs2rockthevote.dll
    ├── Sws2Cs2Menus.Contracts.dll
    ├── Sws2Cs2MapManager.Contracts.dll
    └── resources/
        ├── exports/
        ├── templates/config.template.jsonc
        └── translations/
```

The contracts DLLs are copies of the shared assemblies installed by
`sws2-cs2menus` and `sws2-cs2mapmanager`. Keep the versions from the same release
family to avoid contract mismatches. Workshop maps additionally require
MultiAddonManager; see the map manager documentation.

The first load creates
`csgo/addons/swiftlys2/configs/plugins/sws2-cs2rockthevote/config.jsonc` from the
bundled template.

## Commands

| Command | Description |
| :--- | :--- |
| `!rtv` | Request a map vote, or open the ballot while a vote is active |

## How a vote runs

1. A player types `!rtv`. The request is recorded if the player count,
   request cooldown and vote cooldown allow it.
2. When the number of distinct requests reaches
   `max(MinimumRequests, ceil(players × RequestRatio))`, a vote starts.
3. `OptionCount` maps are picked at random from the map manager catalog,
   skipping the current map.
4. Every eligible player receives a ballot. One vote per player is counted.
5. When the vote timer expires, the map with the most votes wins (ties are
   broken randomly). A vote with no ballots keeps the current map.
6. After `ChangeDelaySeconds`, the winner is requested from the map manager.
   Workshop maps download first and change automatically when ready.

## Configuration

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

| Key | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `true` | Enables RTV requests. |
| `MinimumPlayers` | `4` | Connected human players required to request and start a vote. |
| `MinimumRequests` | `3` | Minimum distinct requests required to start a vote. |
| `RequestRatio` | `0.6` | Fraction of connected human players that must request a vote. |
| `OptionCount` | `5` | Maps offered on the ballot, clamped to `2`-`5`. |
| `VoteDurationSeconds` | `20` | Ballot lifetime, clamped to `5`-`120` seconds. |
| `RequestCooldownSeconds` | `60` | Per-player cooldown between RTV requests. |
| `VoteCooldownSeconds` | `180` | Cooldown after a vote ends before another can start. |
| `ChangeDelaySeconds` | `3` | Delay before the winning map is requested, clamped to `0`-`30`. |

The file is hot-reloaded. Map candidates come from
`sws2-cs2mapmanager`; configure the catalog in
`csgo/addons/swiftlys2/configs/plugins/sws2-cs2mapmanager/config.jsonc`.

## Build

Requires .NET 10, the SwiftlyS2 NuGet package, and the two contract projects
from sibling repositories. Clone the repositories next to each other:

```sh
git clone git@github.com:XBDJ504764827/sws2-cs2menus.git
git clone git@github.com:XBDJ504764827/sws2-cs2mapmanager.git
git clone git@github.com:XBDJ504764827/sws2-cs2rockthevote.git
```

Then build from the `sws2-cs2rockthevote` folder:

```sh
dotnet restore sws2-cs2rockthevote.csproj
dotnet build sws2-cs2rockthevote.csproj -c Release -warnaserror
dotnet publish sws2-cs2rockthevote.csproj -c Release --no-restore
```

The publish step writes `build/publish/sws2-cs2rockthevote/` and packages the
deployment zip used by CI and releases.
