# Changelog

All notable changes to ATLAS are documented here. This project adheres to [Semantic Versioning](https://semver.org).

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
