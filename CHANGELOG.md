# Changelog

## [Unreleased]

### Reservation HUD

- Use the map picker HUD for !yd with configured map names, independent CKZ/VNL ratings and optional packaged screenshots; preserve direct-name reservations.
- Read ratings from mapmanager's shared catalog, show unknown values separately and open immediately without waiting for Steam.
- Recheck whether reservations are enabled when a player clicks an already open menu.

### Added

- Hot-reloadable Workshop metadata enable switch and per-request timeout (default 30 seconds).
- Deterministic HTTP regression checks for nested timeouts, cancellation, caching and offline mode.

### Fixed

- Recognize network failures across the entire exception chain, including timeouts ending in a disposed NetworkStream, and log a concise warning.
- Open reservation menus immediately from configured maps/cached details while refreshing Steam data in the background.
- Stop remaining detail batches after a Steam request fails instead of repeating the same timeout.
- Retry Steam Workshop metadata after transient network failures instead of caching the failure permanently.
- Do not block the reservation menu while another metadata refresh is still running.

## 1.0.0

- Add `!rtv` request threshold and cooldown.
- Add clickable HUD ballots backed by the shared map manager catalog.
- Add one-vote-per-player handling, disconnect cleanup, vote timer and tie resolution.
- Add Workshop-aware result feedback and configurable vote timings.
