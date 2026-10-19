<p align="center">
  <img src="icon.png" alt="jellyfin-plugin-torrent-throttle icon" width="180" />
</p>

# Jellyfin Torrent Throttle

<p align="center">
  <img src="https://img.shields.io/badge/since-10%2F13%2F2026-blue" alt="Since badge" />
  <a href="https://github.com/voc0der/jellyfin-plugin-torrent-throttle/releases/latest">
    <img src="https://img.shields.io/github/v/release/voc0der/jellyfin-plugin-torrent-throttle?label=stable%20release" alt="Stable release version" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-torrent-throttle/blob/main/manifest.json">
    <img src="https://img.shields.io/badge/dynamic/regex?url=https%3A%2F%2Fraw.githubusercontent.com%2Fvoc0der%2Fjellyfin-plugin-torrent-throttle%2Fmain%2Fmanifest.json&search=%22targetAbi%22%3A%5Cs*%22(%5Cd%2B%5C.%5Cd%2B)&replace=%241%2B&label=Jellyfin%20version&color=AA5CC3" alt="Minimum Jellyfin version" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-torrent-throttle/tree/main/tests">
    <img src="https://img.shields.io/badge/coverage-95%25-brightgreen" alt=".NET line coverage percentage" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-torrent-throttle/actions/workflows/codeql.yml">
    <img src="https://img.shields.io/github/actions/workflow/status/voc0der/jellyfin-plugin-torrent-throttle/codeql.yml?branch=main&label=codeql" alt="CodeQL status" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-torrent-throttle/issues">
    <img src="https://img.shields.io/github/issues/voc0der/jellyfin-plugin-torrent-throttle?color=DAA520" alt="Open issues" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/voc0der/jellyfin-plugin-torrent-throttle?color=97CA00" alt="License" />
  </a>
</p>

A Jellyfin plugin that keeps remote streams smooth by lowering qBittorrent's speed limits while people are watching away from home. It subtracts the combined bitrate of active remote streams from your torrent upload cap, and optionally your download cap, then returns to your configured caps when playback stops and restores the previous qBittorrent limits when disabled.

<p align="center">
  <img src="docs/images/torrent-throttle-settings.png" alt="Torrent Throttle plugin configuration screen in Jellyfin" width="880" />
</p>
<p align="center">
  <em>Configuration page inside the Jellyfin dashboard</em>
</p>

## Features

- Reacts to playback start, pause, resume and stop; no polling
- Reserves bandwidth only for remote streams; LAN playback is ignored using Jellyfin's network settings
- Concurrent streams share one budget, with configurable headroom
- Waits 5 seconds (configurable) before throttling, so brief starts and skips don't touch your limits
- Optional download throttling using the same reservation
- qBittorrent username/password or API key (qBittorrent 5.2+)
- Restores your original limits when disabled or when Jellyfin shuts down, and recovers them after a crash
- Test connection button and live status in the settings page

## Installation

### From Plugin Repository

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add: `https://raw.githubusercontent.com/voc0der/jellyfin-plugin-torrent-throttle/main/manifest.json`
3. Install **Torrent Throttle** from the catalog

> [!NOTE]
> Full repository of this author's plugins: [voc0der/jellyfin-plugins](https://github.com/voc0der/jellyfin-plugins).

### Manual

1. Download the latest release ZIP
2. Extract to your Jellyfin plugins directory
3. Restart Jellyfin

#### Building from source

```bash
dotnet build --configuration Release
```

## Configuration

Go to **Dashboard > Plugins > Torrent Throttle**:

- **Web UI URL** — qBittorrent's Web UI as reached from the Jellyfin server or container, e.g. `http://192.168.1.20:8080`. HTTPS certificates are verified, except on private and loopback addresses (e.g. `https://172.30.12.103`), where self-signed certificates are accepted
- **Username / Password** or **API key** — the API key takes precedence when both are set
- **Maximum torrent upload** — your normal upload cap in KiB/s, applied while nobody is streaming remotely (default: 1024)
- **Also throttle downloads** / **Maximum torrent download** — subtract the same reservation from the download cap (default: off)
- **Playback settling time** — seconds to wait before a new or resumed stream counts (default: 5)
- **Extra bandwidth reservation** — headroom added to stream bitrates (default: 10%)
- **Fallback stream bitrate** — used when a stream's bitrate is unknown (default: 20 Mbps)

While enabled, the plugin owns qBittorrent's normal and alternative speed limits. Disable it before changing those limits in qBittorrent, and before uninstalling, so your originals are restored.
