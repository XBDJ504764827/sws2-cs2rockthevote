# Changelog

## [Unreleased]

### Unreserved map voting

- Open the shared map picker HUD for all connected human players after RTV reaches quorum with no reserved map, instead of choosing by catalog order.
- Count one selection per player; choose the highest vote total and randomly resolve ties among leaders. Request the winning map immediately when all players finish.
- Add an optional 30-second ballot deadline, reopening with `!rtv`, disconnect handling, stale ballot rejection and cleanup on map unload, disable or administrator cancel/pause.

### Administrator map control

- Use a 5-second default/minimum RTV countdown, including existing configurations still set to 3. Start missing-map downloads only after the countdown.

- Schedule countdowns through mapmanager so admins can cancel or pause them immediately. Clear votes on control changes while retaining the reservation.
- Reject new votes while paused or another change is pending; cancel RTV-owned work on disable/unload.

### NUB / PRO ratings

- Read CKZ NUB, CKZ PRO, VNL NUB and VNL PRO separately from the shared map catalog for the reservation HUD.

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
