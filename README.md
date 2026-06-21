# ATLAS — Arma Tactical Launch and Administration System

A modern Windows desktop launcher and admin console for **Arma 3 dedicated servers**. ATLAS manages server
profiles, mods, missions, scheduling, BattlEye RCON, headless clients, and Discord integration from a single
self contained application — a spiritual successor to FASTER/TADST with a live admin console.

> **Status:** v0.2.0 · .NET 8 · WPF · single file, self contained, **portable** Windows x64 executable — no installer, no runtime install; just run it.

---

## Features

- **Server profiles** — full `server.cfg` / `basic.cfg` generation, live launch-command preview, import/export.
- **Mods** — SteamCMD download/update, mod presets, Arma 3 Launcher `.html` preset import, junction point
  deployment with key handling, per mod headless client targeting.
- **Missions** — scan `MPMissions`, set the active mission and parameters.
- **Process management** — launch/stop/restart with crash detection and optional auto restart; live RPT log tail
  and CPU/RAM sampling on the Dashboard.
- **Headless clients** — 1–10 instances, per instance profiles/logs, auto restart.
- **Console / RCON** — BattlEye BERCon client: live player list (kick/ban/unban, PM), bans manager, command console
  with history, quick actions, and a Server RPT viewer.
- **Scheduler** — cron based restarts, RCON broadcasts, mod/server updates, with countdown warnings.
- **Discord bot** — embedded bot with 22 `/atlas …` slash commands, role gated, plus crash/join/leave notifications
  and a pinned live status embed.
- **Settings** — SteamCMD setup, Steam Web API key (encrypted), update checker, log level, **light/dark theme**.

---

## Getting started

1. **Download** `ATLAS.exe` (single file — nothing else to install) and run it. Data lives under
   `%AppData%\ATLAS` (database, logs, settings).
2. **Install SteamCMD** — **Settings → SteamCMD → Download SteamCMD** (or point ATLAS at an existing
   `steamcmd.exe`). Use **Test SteamCMD** to confirm it runs.
3. **Create a profile** — **Profiles → New**. Set the server executable path, ports, hostname, and passwords.
4. **Add mods** — **Mods** (paste Workshop IDs or import an Arma 3 Launcher `.html` preset), then **Update** to
   download and **deploy** them to the server.
5. **Pick a mission** — **Missions** scans the server's `MPMissions` folder.
6. **Launch** — from the **Dashboard**. Watch the live RPT log and resource usage.

### SteamCMD / Steam login

ATLAS uses SteamCMD's own cached session model (FASTER style): you enter a **username only** — your Steam password
is **never** stored or passed to ATLAS. Steam Guard prompts are detected and surfaced for input when needed. For
Workshop metadata and preview images, add a free **Steam Web API key** (Settings → Steam Web API Key); it is stored
encrypted.

### BattlEye RCON

Enable BattlEye on the profile and set the **RCON password** and **RCON port** (in `BEServer*.cfg` /
`beserver.cfg`, `RConPassword` and `RConPort`). The **Console / RCON** page connects to the active profile's
server and gives you the player list, bans, and a command console.

### Discord bot setup

1. Create an application + bot at the [Discord Developer Portal](https://discord.com/developers/applications),
   enable the **Server Members** intent, and copy the **bot token**.
2. Invite the bot to your server with the `applications.commands` and `bot` scopes.
3. In ATLAS: **Discord Bot** page → paste the token (stored encrypted), pick the primary guild, set the
   status/command channels and the admin/owner role IDs, then **Connect**. Slash commands register automatically
   (instantly for the primary guild).

---

## Configuration & data

| Path | Contents |
|------|----------|
| `%AppData%\ATLAS\atlas.db` | SQLite database (profiles, mods, schedules, bans, history) |
| `%AppData%\ATLAS\settings.json` | App settings + encrypted secrets — **never commit this** |
| `%AppData%\ATLAS\Logs\` | Rolling daily logs (`atlas-yyyyMMdd.log`) |
| `%AppData%\ATLAS\SteamCMD\` | SteamCMD install + cached login token |

Secrets (Steam API key, Discord token) are encrypted at rest, bound to your Windows account and machine, and are
never written to source control.

---

## Building from source

Requires the **.NET 8 SDK** on Windows.

```powershell
# Run / debug
dotnet build ATLAS\ATLAS.csproj -c Debug

# Produce the single file, self contained release exe (bin\publish\ATLAS.exe)
dotnet publish ATLAS\ATLAS.csproj -c Release -r win-x64 --self-contained true -o ATLAS\bin\publish
```

---

## License

[MIT](LICENSE). Not affiliated with Bohemia Interactive. "Arma" is a trademark of Bohemia Interactive a.s.
