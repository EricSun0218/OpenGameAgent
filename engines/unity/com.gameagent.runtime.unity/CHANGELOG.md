# Changelog

## [0.1.0-alpha.1] - 2026-07-28

- Added the first Unity UPM host package.
- Added bounded main-thread dispatch and Action handler marshalling.
- Added MonoBehaviour lifecycle, cancellation, and durable shutdown.
- Added injectable durable runtime backends with run, resume, and control-plane
  access.
- Bundled the durable composition builder and optional streaming provider
  adapter in assembled UPM artifacts.
- Added Unity-safe structured DTO bridges.
- Added sample, EditMode/PlayMode tests, stub conformance, and Player
  build-and-execution gates with durable pass markers for Mono and IL2CPP.
