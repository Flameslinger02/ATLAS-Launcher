# ATLAS — Arma 3 Reference (BERCon protocol + launch/log notes)

Reference material that was only in the master build prompt, captured here so the prompt can be dropped.

---

## BattlEye RCon (BERCon) — UDP protocol

RCon for Arma 3 is the **BattlEye** protocol ("BERCon"), spoken over **UDP** to the BattlEye RCon port
(per-profile `RconPort`, default 2301 in ATLAS) using the per-profile `RconPassword`. It is enabled only
when BattlEye is on and `beserver_x64.cfg` (`RConPassword` / `RConPort`) is written — ATLAS generates that
in Phase 4 (`IConfigGeneratorService.GenerateBeServerCfg`).

### Packet layout

```
Offset  Size  Field
0       1     'B'  (0x42)
1       1     'E'  (0x45)
2       4     CRC32 checksum, LITTLE-ENDIAN, of every byte from offset 6 to the end of the packet
6       1     0xFF (payload start marker)
7       1     packet type  (0x00 login | 0x01 command | 0x02 server message)
8..     n     payload (type-dependent)
```

- **CRC32** is the standard CRC-32 (reflected polynomial `0xEDB88320`, init `0xFFFFFFFF`, final XOR
  `0xFFFFFFFF`) — identical to zlib/PKZIP CRC32. It is computed over the slice `[0xFF, type, ...payload]`
  (i.e. starting at offset 6), and written at offsets 2–5 **little-endian**.
- Text (passwords, commands, responses) is ASCII.

### Packet types

**Login — type 0x00**
- Client → server: `... 0xFF 0x00 <password ASCII>`
- Server → client: `... 0xFF 0x00 <result>` where `result` = `0x01` success, `0x00` failure.

**Command — type 0x01**
- Client → server: `... 0xFF 0x01 <seq:1 byte> <command ASCII>`
  - `seq` is a 0–255 wrapping sequence number the client assigns per command.
  - A **keepalive** is an *empty* command packet: `... 0xFF 0x01 <seq>` with no command bytes.
- Server → client, **single-part**: `... 0xFF 0x01 <seq> <response ASCII>`
- Server → client, **multi-part**: `... 0xFF 0x01 <seq> 0x00 <total:1> <index:1> <data ASCII>`
  - Detection: the byte immediately after `seq` is `0x00` and at least two header bytes follow
    (`total`, `index`). Reassemble by concatenating the `data` of indices `0..total-1` (same `seq`).
  - (Single-part responses put response text directly after `seq`; the `0x00` marker is the multi-part flag.)

**Server message — type 0x02** (server-initiated: chat, player connect/disconnect, kicks, etc.)
- Server → client: `... 0xFF 0x02 <seq:1 byte> <message ASCII>`
- Client **MUST acknowledge**: `... 0xFF 0x02 <seq>` (echo the seq, no data). Un-acked messages are resent.

### Connection lifecycle / keepalive

1. Open UDP socket to `host:RconPort`. Send the login packet; wait for the `0xFF 0x00` response.
2. On success, send commands (incrementing `seq`); handle responses (reassembling multi-part) and
   server messages (acking each).
3. **Timeout:** the server drops the connection if it receives **no packet from the client for ~45 s**.
   Send a keepalive (empty command packet) every ~30 s of idle to stay alive.
4. There is no explicit logout; just stop sending and close the socket.

### Useful RCon commands (sent as the command payload)

- `players` — list connected players (parse table, see below).
- `admins`, `missions`, `#mission <name>` — admin/mission control.
- `say -1 <message>` — global broadcast (`say <id> <msg>` to one player).
- `kick <id> [reason]`, `ban <id> [time] [reason]`, `addBan <guid> [time] [reason]`, `removeBan <id>`.
- `#lock`, `#unlock` — lock/unlock the server.
- `#shutdown` — clean server shutdown (ATLAS uses this for graceful stop in Phase 7's StopAsync once RCON
  is available).
- `#restartserver`, `loadEvents`, `loadScripts`.

### `players` response format (for the Console player list parse)

```
Players on server:
[#] [IP Address]:[Port] [Ping] [GUID] [Name]
--------------------------------------------------
0   127.0.0.1:2304        45   d41d8cd98f00b204e9800998ecf8427e(OK) Alice
1   10.0.0.5:2304         62   0cc175b9c0f1b6a831c399e269772661(OK) Bob (Lobby)
(2 players in total)
```

Parse rules: skip the 3 header lines and the `(N players in total)` footer; for each row split on
whitespace — col 0 = id, col 1 = `ip:port`, col 2 = ping, col 3 = `<guid>(<verify>)` (strip the trailing
`(OK)`/`(?)`), remainder = name (may contain spaces and a trailing ` (Lobby)`).

---

## Launch parameters / files (already implemented, for reference)

- Server exe: `arma3server_x64.exe` (or `arma3server_profiling_x64.exe` when `UseProfilingBranch`).
- Config files (ATLAS-generated, relative to the server dir): `-config=server.cfg`, `-cfg=basic.cfg`,
  BattlEye `BattlEye\beserver_x64.cfg`. Because `-config`/`-cfg` are relative, the process
  **WorkingDirectory must be the server directory** (Phase 7).
- `-profiles=<dir>` sets where the server writes its profile and the **`.rpt` log** ATLAS tails (Phase 7
  appends `-profiles=<ServerDir>\profiles`).
- `-port=<n>` game port (default 2302). The game reserves 2302–2306; RCON must be outside that range.
- Headless client: `-client -connect=127.0.0.1 -port=<n> -password=<srvpw> -profiles=<HCdir> -name=HC<i>`
  plus the client/headless mod list via `-mod=`.
- Steam app IDs: dedicated server `233780`, workshop content `107410` (profiling branch = `profiling`).
