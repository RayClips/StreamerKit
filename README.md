# StreamerKit

<img src="./assests/icon.png" align="right" alt="StreamerKit icon" title="StreamerKit icon" width="120">

[![Build and release](https://github.com/RayClips/StreamerKit/actions/workflows/build.yml/badge.svg)](https://github.com/RayClips/StreamerKit/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/RayClips/StreamerKit?include_prereleases&label=download)](https://github.com/RayClips/StreamerKit/releases)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20%2F%2010%201809%2B-0078D4)
![Status](https://img.shields.io/badge/status-alpha-orange)

**StreamerKit** is a free Windows desktop app that starts, watches and restarts the local
servers behind your OBS overlays, reads Twitch/Kick/Discord chat, and re-broadcasts
everything over a WebSocket server that speaks the **Streamer.bot protocol** - so overlay
widgets built on `@streamerbot/client` connect to StreamerKit exactly as if it were
Streamer.bot.

It's built with **WinUI 3** - real Windows 11 Fluent controls, not a themed web view.

> **Alpha software.** Things will change and break. If something's off, that's worth
> reporting, not assuming is expected.

---

## What it actually does

Streamers running browser-source overlays end up with a pile of `.bat` files, one console
window per server, URLs copied by hand into OBS, and no idea a server died until a viewer
says the overlay's gone blank. StreamerKit replaces that with one window:

- **Runs your servers.** Start, stop, and watch the output of every local server from one
  place — the ones StreamerKit hosts itself (HTTP / WebSocket / UDP) and external programs
  it supervises as child processes.
- **Restarts what crashes.** A server that exits unexpectedly comes back on its own, with
  backoff (2s → 5s → 10s → 20s → 30s) so a genuinely broken server surfaces instead of
  looping forever. Every crash is logged with the last lines of output.
- **Reads chat.** Twitch and Kick, anonymously — no account or OAuth needed — plus Discord
  through a bot you provide the token for. All three normalize to the same message shape,
  so a chat trigger doesn't care which platform it came from.
- **Talks to OBS, Streamlabs and Discord.** Connects to OBS over obs-websocket, to
  Streamlabs Desktop over its remote-control API, and to Discord as a bot (gateway +
  REST) — switch scenes, show/hide sources, send messages and embeds, all from one app.
- **Runs actions.** A trigger-and-steps automation engine: chat commands, mouse regions,
  hotkeys, timers, server state changes, and "the stream just started" from OBS or
  Streamlabs — wired to steps like switching an OBS/Streamlabs scene, sending a Discord
  message, hitting an HTTP endpoint, or broadcasting a custom event to your overlays.
- **Speaks Streamer.bot's protocol**, so overlay widgets built for Streamer.bot
  (Nutty's, and anything on `@streamerbot/client`) connect to StreamerKit without
  modification.
- **Stays out of the way when closed.** Every server StreamerKit launches lives in a
  Windows job object, so closing — or even force-killing — StreamerKit takes every child
  process with it. Nothing is left holding a port.
- **Starts with Windows, if you want.** A per-user registry entry, no admin rights, no
  scheduled task — and each chat platform or integration can be set to connect
  automatically on launch, independently of the others.

## What it's honestly *not* yet

These are shown in the app with a warning banner, on purpose — nothing here is hidden:

- **The Plugins page is a mock.** A sample catalogue with a fake progress bar; nothing
  downloads or installs. It's there to preview the shape of the feature.
- **YouTube and TikTok chat aren't built.** Listed on the Platforms page as disabled,
  with the reason shown on screen.
- **Stream Deck isn't built.** Listed on the Integrations page the same way.
- **No follows, subs, bits or raid events.** Those need OAuth and EventSub, which don't
  exist yet — chat is read-only for now.

## Docs

**[Official Docs](https://github.com/RayClips/StreamerKit/wiki)**<br>
**[Website](https://streamerkit.rayclips.lol)**

## Download

Grab the latest build from **[Releases](https://github.com/RayClips/StreamerKit/releases)**
— every push to `main` publishes one automatically. Two options:

| | Size | Needs |
|---|---|---|
| **Self-contained** | ~86 MB download | Nothing — unzip and run |
| **Framework-dependent** | ~11 MB download | [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) + Windows App Runtime 1.8 |

If you're not sure which, take the self-contained one. Unzip anywhere writable and run
`StreamerKit.exe` — it's portable, no installer, no admin rights.

Requires Windows 11, or Windows 10 1809+, 64-bit.

## Building from source

```bash
dotnet publish LauncherWinUI/LauncherWinUI.csproj -c Release -o App
```

That's the only supported way to update `App/` — always publish, don't hand-copy files.

## Configuration

Everything lives in plain JSON next to the exe, so most changes don't need a rebuild:
`servers.json`, `settings.json`, `platforms.json`, `integrations.json`, `actions.json`,
`plugins.json`. Add a server by editing `servers.json` — any program that prints a
`http(s)://…` URL to its own output works, Node, Python, or anything else.

## Contributing

Issues and PRs are welcome. There's no test suite this app is verified by running it
and driving it through its actual UI, and the README explain
why.

## License
[**MIT License**](https://github.com/RayClips/StreamerKit/blob/main/LICENSE)
