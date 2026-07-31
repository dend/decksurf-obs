<h1 align="center">DeckSurf OBS Connector</h1>

<p align="center">
  <a href="https://github.com/dend/decksurf"><img src="https://img.shields.io/badge/DeckSurf-plugin-1D9BF0" alt="DeckSurf plugin" /></a>
  <a href="https://obsproject.com/"><img src="https://img.shields.io/badge/OBS%20Studio-28%2B-302E31?logo=obsstudio&logoColor=white" alt="OBS Studio 28+" /></a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 10.0" />
  <a href="https://github.com/dend/decksurf-obs/commits/main"><img src="https://img.shields.io/github/last-commit/dend/decksurf-obs" alt="Last commit" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-Keep%20a%20Changelog-E05735" alt="Keep a Changelog" /></a>
</p>

Control [OBS Studio](https://obsproject.com/) from your Stream Deck with [DeckSurf](https://github.com/dend/decksurf). Scene buttons show a live preview of their scene, and the scene that is on program is marked with a red border and a LIVE badge.

## What you get

- **Switch scene**: one button per scene. The key shows a live snapshot of the scene, and pressing it puts that scene on program.
- **Cycle scenes**: step through your scenes with a button press, or by rotating a knob on the Stream Deck+. The key always shows what is currently live.
- **Toggle recording**: start or stop recording with a button press. The key shows a REC circle that slowly pulses red while a recording is in progress — even when the recording was started from OBS itself — and is greyed out when idle.

When you set up a button in the DeckSurf editor, the scene picker is filled with the scenes from your running OBS, the password field is masked, and a status bar tells you whether DeckSurf can reach OBS before you ever press a key.

## Requirements

- OBS Studio 28 or later. The WebSocket server the plugin uses is built in; there is nothing to install on the OBS side.
- DeckSurf with a supported Stream Deck.

## Setup

1. In OBS, open **Tools → WebSocket Server Settings** and check **Enable WebSocket server**. If authentication is on, click **Show Connect Info** and copy the password.
2. Put `DeckSurf.Plugin.OBS.dll` into the `plugins` folder next to your DeckSurf install.
3. In the DeckSurf editor, assign **Switch scene** or **Cycle scenes** to a key, pick a scene, and enter the password. The status bar below the settings turns green when the connection works.

## Button settings

| Setting | Default | Notes |
|---|---|---|
| Scene name | | Which scene the button switches to (Switch scene only). Picked from a list when OBS is running. |
| OBS host | `127.0.0.1` | Set to the machine's IP when OBS runs on another computer. |
| OBS port | `4455` | Matches the OBS WebSocket server default. |
| OBS password | empty | Leave empty when authentication is disabled in OBS. |
| Scene preview on key | on | Shows a live snapshot of the scene on the key (scene commands only). Turn off for a flat key with the scene name. |
| Preview refresh (seconds) | `3` | How often the snapshot updates (scene commands only). |

Scene keys are always drawn by the plugin, so custom button images do not apply to these commands.

## Tips

- A small red dot on a key means OBS is not reachable. The plugin reconnects on its own once OBS is back.
- Previews of scenes that are not on program can look frozen or black. That happens when OBS pauses their sources while hidden. For cameras, uncheck *Deactivate when not showing*; for media sources, check *Always play even when not visible*; for display captures, keep a **View → Multiview (Windowed)** window open so all scenes stay rendering.
- Scenes renamed, added, or removed in OBS show up on the deck right away; no profile changes needed.

## Building from source

```bash
cd src
dotnet build DeckSurf.Plugin.OBS.slnx
```

The [DeckSurf SDK](https://www.nuget.org/packages/DeckSurf.SDK) is pulled from NuGet during the build; no other checkouts are needed.
