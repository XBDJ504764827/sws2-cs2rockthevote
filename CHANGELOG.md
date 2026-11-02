# Changelog

## [Unreleased]

### Fixed

- Retry Steam Workshop metadata after transient network failures instead of caching the failure permanently.
- Do not block the reservation menu while another metadata refresh is still running.

## 1.0.0

- Add `!rtv` request threshold and cooldown.
- Add clickable HUD ballots backed by the shared map manager catalog.
- Add one-vote-per-player handling, disconnect cleanup, vote timer and tie resolution.
- Add Workshop-aware result feedback and configurable vote timings.
