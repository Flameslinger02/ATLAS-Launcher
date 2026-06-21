# Changelog

All notable changes to ATLAS are documented here. This project adheres to [Semantic Versioning](https://semver.org).

## [0.3.1] — 2026-06-21

### Added
- **In-app updater** (Console → Updates) — install or update the **Arma 3 dedicated server** via SteamCMD with
  live output and a cancel button, alongside an **Update ATLAS** button.
- **ATLAS self-update** — ATLAS can download the latest release, replace itself, and relaunch, with no manual
  copying. Also available from **Settings → Check for updates → Update & Restart**.
- **Creator DLC toggles** — enable Contact, Global Mobilization, S.O.G. Prairie Fire, CSLA Iron Curtain,
  Western Sahara, Spearhead 1944, Reaction Forces, and Expeditionary Forces per profile; enabled DLC is added to
  the server launch command.
- **Granular difficulty editor** — a full custom-difficulty grid plus **AI skill/precision** and optional view
  distance / terrain grid, written to an `*.Arma3Profile`; load a built-in preset as a starting point.
- **More server.cfg settings** — `requiredBuild`, VoN codec type, three-state file patching
  (none / headless only / all), and scripting callbacks (`serverCommandPassword`, `onUserConnected` /
  `onUserDisconnected`, `onHackedData`, `onDifferentData`, `onUnsignedData`, `onUserKicked`).
- **More network + mission settings** — max packet size and max custom file size; auto-select mission, random
  mission order, and skip lobby.
- **Mission rotation** — check multiple missions in the Missions tab to build a server rotation
  (`server.cfg` `class Missions`), played in order and cycled; "Randomize mission order" shuffles it.
- **Uninstall** (Settings → danger zone) — removes ATLAS and its regenerable files, with an optional checkbox to
  also wipe your settings and profiles database; it reports anything it could not remove.

### Changed
- **Profiles are now the hub.** The sidebar lists your profiles — click one to make it active and edit it in a
  full-width workspace with **Profile / Mods / Missions / Config / Network** tabs and a single Save. Server
  Config, Mods, and Missions are no longer separate top-level pages, and a new overview page lists every profile
  with quick actions.
- **Console split in two** — **Console** (ATLAS log + Server RPT + Updates) and **RCON** (live player/ban
  management and command console) are now separate sidebar entries.
- **Data grids restyled** app-wide — sort arrows, clearer column edges and resize handles, and columns that keep
  their dragged width and push their neighbours.

### Fixed
- Drop-down menus render as a single bordered box around the selected text and arrow (previously the arrow could
  appear as a separate boxed button), and no longer collapse to just the selected value.
- Settings page content is centered in the window instead of pinned to the sidebar.
- The mod **Name** column is read-only (it was accidentally editable).
- Mods grid column widths and padding tightened for readability.
- Fixed a crash that could occur when setting the active mission.

## [0.2.0] — 2026-06-21

### Added
- **UPnP** — optional toggle (Server Config → Network) that writes `upnp = 1;` to `server.cfg`, letting the
  dedicated server map its own ports via a UPnP/IGD router. Off by default.
- **Arma install auto-detection** — Settings → Arma 3 Server locates the dedicated-server install from your
  Steam libraries; new profiles pre-fill their server path from it (still fully editable).
- **Custom mission folder** — an optional per-profile mission folder override (Server Config → Mission); when
  blank, ATLAS scans the server's `MPMissions` / `Missions` as before.

### Changed
- **Profiles** moved to the second sidebar position; the sidebar is narrower.
- **Mods grid** — per-column minimum widths, columns keep their dragged width (resizing pushes the neighbouring
  columns), and header divider lines mark the resize handles.
- Larger minimum window width.

### Fixed
- Drop-down (ComboBox) menus were hard to read (light-grey text on a white popup); they are now themed correctly
  in both the light and dark themes.

## [0.1.0] — 2026-06-16

First public release. ATLAS is a single-file, self-contained, **portable** Windows x64 application (.NET 8 / WPF) —
no installer and no .NET runtime required; just run `ATLAS.exe`.

### Added
- **Server profiles** — `server.cfg` / `basic.cfg` generation, live launch-command preview, JSON import/export.
- **Mods** — SteamCMD download/update, mod presets, Arma 3 Launcher `.html` preset import, junction-point
  deployment with BattlEye key handling, per-mod headless-client targeting.
- **Missions** — `MPMissions` scanning and active-mission/parameter selection.
- **Server process management** — launch/stop/restart, crash detection with optional auto-restart, live RPT log
  streaming, and CPU/memory sampling on the Dashboard.
- **Headless clients** — 1–10 instances with per-instance profiles, logs, and auto-restart.
- **Console / RCON** — BattlEye BERCon client with a live player list (kick/ban/unban/PM), bans manager, command
  console with history, quick actions, and a Server RPT viewer.
- **Scheduler** — cron-based restarts, RCON broadcasts, and mod/server updates, with pre-restart countdown warnings.
- **Discord bot** — embedded bot with 22 `/atlas …` slash commands (role-gated), crash/join/leave notifications,
  and a pinned live status embed.
- **Settings** — SteamCMD setup + tests, Steam Web API key (stored encrypted), GitHub update checker (Octokit),
  runtime light/dark theme switch, live log-level control, and a guarded "danger zone".
- **System tray** — minimize-to-tray, tooltip (server name / state / players), context menu, and balloon
  notifications on crash and restart.
- **Window state** — size and position are remembered between sessions.
- Secrets are stored encrypted and never written to source control; no Steam password is ever stored.
