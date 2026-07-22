# ATLAS — Arma Tactical Launch and Administration System

A modern Windows desktop launcher and admin console for **Arma 3 dedicated servers**. ATLAS manages server
profiles, mods, missions, scheduling, BattlEye RCON, headless clients, and Discord integration from a single
self contained application — a spiritual successor to FASTER/TADST with a live admin console.

> **Status:** v0.4.6 · .NET 8 · WPF · single file, self contained, **portable** Windows x64 executable — no installer, no runtime install; just run it.

---

## Features

- **Server profiles** — full `server.cfg` / `basic.cfg` generation, live launch-command preview, import/export.
- **Mods** — a global **Mods hub** (library + presets): SteamCMD download/update, check + batch-update against
  the Workshop, junction-point deployment with BattlEye key handling and duplicate-key checks, Arma 3 Launcher
  `.html` preset import, and per mod headless client targeting.
- **Missions** — scan `MPMissions` (packed `.pbo` or unpacked mission folders), set the active mission or a
  multi-mission **rotation**, and parameters.
- **Process management** — launch/stop/restart with crash detection and optional auto restart; live RPT log tail
  and rolling CPU/RAM/player graphs on the Dashboard, switchable between whole-computer load and just the server
  process; and a **Down / Local / Public** reachability light that checks (via the Steam server list) whether your
  server is actually reachable from the internet. If ATLAS is closed while a server is running, it re-attaches to
  that server on the next launch.
- **Headless clients** — 1–10 instances, per instance profiles/logs, auto restart.
- **Console** — live ATLAS log and Server RPT viewer, plus an **Updates** tab to install/update the Arma 3 server
  (SteamCMD) and to update ATLAS itself.
- **RCON** — BattlEye BERCon client: live player list (kick/ban/unban, PM), bans manager, and a command console
  with history and quick actions.
- **Scheduler** — daily/weekly/interval restarts, RCON broadcasts, and a combined **Update & Restart**
  (server + mods), with pre-restart countdown warnings. An Advanced mode still accepts raw cron.
- **Discord bot** — embedded bot with 22 `/atlas …` slash commands, role gated, plus crash/join/leave notifications
  and a pinned live status embed.
- **Settings** — SteamCMD setup, Steam Web API key (encrypted), update checker, log level, **light/dark theme**.
- **Creator DLC, granular difficulty + AI, and more server settings** — per-profile DLC toggles, a custom
  difficulty grid with AI skill/precision, scripting callbacks, VoN codec, file patching, and network/mission options.
- **Self-updating** — ATLAS can download and apply its own updates; an **Uninstall** option (Settings) can remove it.

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

ATLAS uses SteamCMD's own cached session model (FASTER style): **log in once** with your Steam username and password
in a masked prompt. The password is used only to establish SteamCMD's cached session and is **never stored** by
ATLAS; afterwards downloads and updates reuse the cached token, and Steam Guard prompts are detected and surfaced for
input when needed. For Workshop metadata and preview images, add a free **Steam Web API key** (Settings → Steam Web
API Key); it is stored encrypted.

### BattlEye RCON

Enable BattlEye on the profile and set the **RCON password** and **RCON port** (in `BEServer*.cfg` /
`beserver.cfg`, `RConPassword` and `RConPort`). The **RCON** page connects to the active profile's
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
