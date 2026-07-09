# Changelog

All notable changes to ATLAS are documented here. This project adheres to [Semantic Versioning](https://semver.org).

## [0.4.0] — 2026-07-08

### Added
- **Re-attach to a running server** — if you close ATLAS while a server is still running, ATLAS finds
  that server again when you reopen it (and its profile is active) and picks up where it left off:
  live status, uptime, Stop/Restart/Force-kill, the RPT log tail and crash detection all work again.
  Performance history starts fresh. This also works across a higher-privilege server started as
  administrator.

### Changed
- **Dashboard layout** — the uptime/players/CPU/memory/crashes tiles now sit in a compact column on the
  left with the performance graphs beside them, and the server log / headless-client panels get more
  room; the whole top area is height-capped so everything stays visible without maximising the window.
- **Performance graphs** — the rolling window is now 5 minutes with a fixed time axis (1m–5m) and
  round-value Y ticks, and the graphs are always shown (drawing an empty grid when the server is
  stopped) instead of appearing only while running.
- **Mods Library grid** — column widths and resizing now match the Presets grid (drag a divider and the
  neighbours give way; columns keep their width).

### Fixed
- **RPT analyzer no longer cries wolf on missing addons** — the "you cannot play/edit this mission…"
  / "requires addon" warning is a common, usually-harmless engine message (often from stale entries in
  a mission's `mission.sqm`, especially for vanilla `a3_*` content) and is now reported as a Warning
  with clearer guidance, not a Critical. A true "Cannot load mission" is still flagged Critical.
- **Unreadable text in some pop-up windows** — the Mission Dependencies, Add-from-Library, RPT Analysis
  and Headless-Client-Log windows now use the app theme's text colour, so their text is readable in the
  dark theme (previously some controls rendered black-on-dark).

## [0.3.15] — 2026-07-08

### Changed
- **Performance graphs now have axes** — the Dashboard's CPU, memory and player charts show value labels
  (with units) on the Y axis and the time span (oldest → now) on the X axis, with light gridlines.
- **Mission Dependencies hides vanilla by default** — the dependencies window now lists only mod-provided
  addons; a "Show vanilla (A3_)" toggle reveals the base-game/DLC entries, and the header shows the split.
- **Clearer Steam status wording** — the Dashboard's Steam check is relabeled to make explicit that it is a
  local query confirming the server's Steam layer is answering on this machine; it does not verify that
  your ports are forwarded or that the server is reachable from the internet.

## [0.3.14] — 2026-07-08

### Added
- **More server.cfg options** — an admin UID whitelist (`admins[]`, for password-less `#login`), an idle
  FPS limit (caps server FPS only when no players are connected), lobby and role-selection timeouts,
  a chat/command anti-flood section, and a mission whitelist that limits which missions an admin can
  switch to (your checked rotation missions are always included).
- **Missing-mod warning on start** — before launching, ATLAS checks the rotation mission(s)' required
  addons against your enabled mods and warns you (with a "Start Anyway" option) if something's missing.
- **Steam visibility check** — the Dashboard shows a live Steam query of the running server, confirming
  it is up and answering the server browser (independent of RCON).
- **RPT analyzer** — an **Analyze** button on the Console's Server RPT tab scans the log for known issues
  (missing addons, signature kicks, script errors, crashes, and harmless noise) and groups them with
  plain-language guidance.
- **Backup / restore** — snapshot a profile's configs, keys and mission folders to a timestamped zip and
  restore them later, from buttons in the profile header.
- **Performance graphs** — rolling 30-minute CPU, memory and player-count charts on the Dashboard.

### Fixed
- **autoInit now actually works.** `-autoInit` is a startup parameter, not a `server.cfg` setting; ATLAS
  was writing it into `server.cfg` where the game ignored it, so the mission never auto-initialized. It
  is now passed on the launch command line (and still forces `persistent = 1`, which the game requires).

## [0.3.12] — 2026-07-07

### Added
- **"Add from Library" for mod presets** — pick mods ATLAS already knows about from a filterable,
  multi-select list instead of re-pasting Workshop IDs. Presets → Add from Library.
- **Mission Dependencies** — a **Dependencies** button on the Missions tab reads a mission's `mission.sqm`
  (packed `.pbo` or unpacked folder, plain-text or binarized) and shows its required addons, with Copy All.

### Changed
- **Stop / Force Kill now work while the server is still starting up.** Previously both were disabled (or
  silently queued) for the whole ~20s startup window; a stop now aborts an in-progress launch immediately.
- **Log views auto-scroll properly.** Every log/output view (ATLAS Log, Server RPT, Updates output, RCON
  console, Mods output, headless-client logs, Dashboard log tail) now opens at the bottom, follows new
  output, and only stops following when you scroll up — scrolling back to the bottom resumes it.
- The **autoInit** checkbox now explains its requirements in a tooltip (forces `persistent=1`; the mission
  needs base/instant respawn to keep running with 0 players; disables mission Parameters).

## [0.3.10] — 2026-06-22

### Added
- **Unpacked missions** — the mission scanner now lists unpacked mission folders (a folder containing
  `mission.sqm`) under `MPMissions`/`Missions`, not just packed `.pbo` files.
- **Whole mod library in each profile** — a profile's **Mods** tab now lists every mod in your library;
  the **Server / Client / Headless** checkboxes decide which are active for that profile. Adding a mod
  (Workshop or local) adds it to the library and activates it for the current profile.
- **Server file patching** — a dedicated Config-tab toggle for the server's `-filePatching` switch
  (separate from the client-facing *allowed file patching* setting); required to load unpacked missions.

### Changed
- **In-place, app-aware server update** — ATLAS now updates the Steam app actually installed in the target
  folder: your **Arma 3** install (the dedicated server ships with the game) when that's what's there,
  otherwise the **standalone dedicated server** — instead of always installing the standalone server app.
  No separate folder required, and it never installs a different app on top of an existing one.
- The startup **update banner** now opens directly to the **Console → Updates** tab.
- **autoInit** now also sets `persistent = 1` (autoInit has no effect in Arma without it).
- **Headless clients** now get the same creator-DLC folders as the server in their `-mod=` line, so
  DLC-dependent missions load on the headless client.
- Network tuning keys (`MaxMsgSend`, max message sizes, bandwidth, error-to-send) are written to
  `basic.cfg` only (no longer duplicated into `server.cfg`).
- `voteMissionPlayers` is written as a whole number.

### Fixed
- **Fresh installs could fail to create their database** (`duplicate column name: MissionQueue`), leaving
  a brand-new user unable to start. New databases now initialize cleanly.
- The server updater no longer risks **overwriting/corrupting an existing Arma 3 game install**.

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
