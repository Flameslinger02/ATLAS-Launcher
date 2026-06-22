# Changelog

All notable changes to ATLAS are documented here. This project adheres to [Semantic Versioning](https://semver.org).

## [0.3.6] — 2026-06-22

### Changed
- The startup "update available" banner button is now **Update** (was "View Release") and opens the
  in-app updater on the Console page instead of the GitHub releases page — so you can download and apply
  the update without leaving ATLAS.

## [0.3.5] — 2026-06-21

### Added
- **Global Mods hub** — a single **Mods** page with two tabs: a **Library** of every mod ATLAS knows about
  (across all profiles and presets) and your **Presets**. From the Library you can add and download Workshop
  items, check the Workshop for updates and batch-update, deploy a chosen profile's mods, sync BattlEye keys,
  and check for duplicate-key conflicts — all with live output. The standalone Mod Presets page is folded in.
- **In-app Steam login** — log in to Steam from inside ATLAS (Mods or Settings). Your username and password are
  entered in a masked prompt; the password is used **once** to establish SteamCMD's cached session and is
  **never stored**. Steam Guard codes are prompted when required, and downloads/updates log you in automatically
  if the cached session is missing or expired.
- **Mod download directory** — choose where SteamCMD downloads Workshop mods. Point it at your existing Steam
  install and ATLAS reuses the mods already on disk instead of downloading duplicates.

### Changed
- **Clean Stale Links** now reports exactly what it scanned, removed, and left untouched (real copied folders are
  never deleted) instead of finishing silently.

### Fixed
- **Mod paths** — deploy and download now resolve Workshop folders correctly whether the mod directory points at
  your Steam root, `steamapps`, `…\workshop`, or `…\content\107410`; fixes a "source folder missing" error and a
  duplicated `steamapps\workshop` tree.
- **Logs** — the log file under `%AppData%\ATLAS\Logs` is now flushed to disk during the session and on exit, so
  it stays current for troubleshooting (previously it could appear frozen).

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
