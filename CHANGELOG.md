# Changelog

All notable changes to the DeckSurf OBS Connector are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `ToggleMute` command: mutes or unmutes an OBS audio input with a key press. The input picker in the profile editor is populated from the running OBS instance, and the key shows a red slashed microphone while the input is muted, tracking mute changes made in OBS itself as well.
- `PauseRecording` command: pauses or resumes the current recording with a key press. The key shows pause bars that turn amber while the recording is paused and stays dimmed when no recording is running.
- `ToggleVirtualCamera` command: starts or stops the OBS virtual camera with a key press. The key lights up blue while the virtual camera is running.

### Changed

- The recording key now turns amber while the recording is paused instead of continuing to pulse red.

### Fixed

- Keys recover within seconds after OBS is restarted. The reconnect backoff previously climbed to 30 seconds while OBS was down, so a relaunched OBS could sit unnoticed for up to half a minute before the keys reactivated; the backoff is now capped at 5 seconds.

## [0.3.0] - 2026-07-31

### Changed

- The recording key is now a dedicated REC circle: greyed out while idle, red with a slowly pulsating white REC label while a recording is in progress.

## [0.2.0] - 2026-07-30

### Added

- `ToggleRecording` command: starts or stops recording in OBS with a key press. The key turns red with a REC badge while a recording is in progress, tracking recordings started from OBS itself as well.

## [0.1.0] - 2026-07-30

### Added

- `SwitchScene` command: one key per scene, switching OBS to that scene on press.
- `CycleScenes` command: steps through the scene list with a key press or a Stream Deck+ knob rotation.
- Live scene previews on keys, refreshed on a configurable interval, with a red border and LIVE badge on the program scene.
- Scene picker in the DeckSurf profile editor, populated from the running OBS instance.
- Connection status reporting in the profile editor, including the failure reason when OBS is unreachable.
- Masked password input for the obs-websocket password.
- Automatic reconnection with backoff, and a shared connection for all keys pointing at the same OBS instance.
- Release workflow that builds, versions, and publishes the plugin from a version tag.

[Unreleased]: https://github.com/dend/decksurf-obs/compare/0.3.0...HEAD
[0.3.0]: https://github.com/dend/decksurf-obs/compare/0.2.0...0.3.0
[0.2.0]: https://github.com/dend/decksurf-obs/compare/0.1.0...0.2.0
[0.1.0]: https://github.com/dend/decksurf-obs/releases/tag/0.1.0
