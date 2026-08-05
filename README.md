# SSHRtm

Realtime multi-server monitor for RHEL 9 / Linux hosts, driven over SSH from a
Windows terminal. No agent required on the target — the app polls `/proc`,
`df`, and `ps` over a persistent SSH session and renders a btop-style TUI.

---

## Configuration files

The app uses **two files**, always co-located in the same directory:

| File          | Purpose                                                   |
| ------------- | --------------------------------------------------------- |
| `servers.json` | Plain-text list of servers, poll/render intervals.       |
| `creds.dat`    | DPAPI-encrypted SSH passwords, keyed by server name.     |

### Lookup order

On startup (when `--config` is not passed) the app searches for `servers.json`
in this order and stops at the first match:

1. **`<exe-folder>\servers.json`** — the folder containing `SSHRtm.exe`.
   This is the *portable* location: drop the exe and its config into any
   folder and it just works.
2. **`%APPDATA%\SSHRtm\servers.json`** — the per-user install location
   (typically `C:\Users\<you>\AppData\Roaming\SSHRtm\servers.json`).

**`creds.dat` is always placed alongside whichever `servers.json` was loaded.**
If servers.json is next to the exe, so is creds.dat. If servers.json is in
%APPDATA%, so is creds.dat.

Override with:

```
SSHRtm.exe --config D:\path\to\servers.json
```

`creds.dat` will then live in `D:\path\to\`.

---

## servers.json format

```json
{
  "pollIntervalMs": 1000,
  "renderIntervalMs": 500,
  "servers": [
    { "name": "web01", "host": "10.0.0.10", "port": 22, "user": "monitor" },
    { "name": "db01",  "host": "10.0.0.11", "port": 22, "user": "monitor" }
  ]
}
```

| Field              | Type   | Default | Notes                                            |
| ------------------ | ------ | ------- | ------------------------------------------------ |
| `pollIntervalMs`   | int    | 1000    | How often each SSH poller pulls fresh metrics.  |
| `renderIntervalMs` | int    | 500     | How often the TUI redraws.                       |
| `servers[].name`   | string | —       | Unique label — appears as the tile header and is the key used in `creds.dat`. |
| `servers[].host`   | string | —       | Hostname or IP.                                  |
| `servers[].port`   | int    | 22      | SSH port.                                        |
| `servers[].user`   | string | —       | SSH username.                                    |

Parser accepts JSON comments (`// …`) and trailing commas; property names are
case-insensitive.

**Passwords are never stored in `servers.json`.** They live only in
`creds.dat`, encrypted.

---

## creds.dat lifecycle

`creds.dat` is a JSON dictionary of `{ "<server-name>": "<base64 DPAPI blob>" }`.
Each blob is the UTF-8 password encrypted via the Windows
Data Protection API with **`DataProtectionScope.CurrentUser`**.

### First run (no config anywhere)

1. App finds no `servers.json` in the exe folder or in `%APPDATA%\SSHRtm\`.
2. It writes the sample `servers.json` (shown above) — trying the exe folder
   first, then `%APPDATA%\SSHRtm\` if the exe folder isn't writable.
3. It prints where the sample was written and exits.
4. You edit that file with your real servers, then re-run.

### First run against a real servers.json

1. App loads `servers.json` and iterates the server list.
2. For each server **not present** in `creds.dat`, it prompts:

   ```
   Password for web01 (monitor):
   ```

3. Each entered password is DPAPI-encrypted and written to `creds.dat`
   immediately (so a crash mid-setup doesn't lose the ones already entered).
4. Once all passwords are collected, the TUI launches.

### Adding a new server later

1. Edit `servers.json` and append a new object to `servers`.
2. Re-launch `SSHRtm.exe`.
3. The app skips servers whose names already exist in `creds.dat` and prompts
   **only for the new one**. Existing passwords are left untouched.

### Re-entering / rotating a password

```
SSHRtm.exe --set-passwords
```

Forces a re-prompt for **every** server in `servers.json`, overwriting
whatever is in `creds.dat`. The app exits after storing them — re-run without
the flag to launch the TUI.

### Removing a server

Delete it from `servers.json`. Its entry in `creds.dat` becomes dead weight
but is harmless; the app just ignores it.

---

## DPAPI portability caveat

DPAPI `CurrentUser` scope binds the encryption to the **Windows user account
that wrote the file**. Consequences:

- `creds.dat` **cannot be shipped to other people** — they will not be able
  to decrypt it, even on the same PC.
- `creds.dat` **cannot be copied to another machine** for the same user
  (except when roaming profiles are configured to migrate DPAPI keys, which
  is rare).
- Multiple Windows users on the same PC each need their own `creds.dat`.

If you distribute a portable folder containing `SSHRtm.exe` + `servers.json`,
each recipient will be prompted once on their first run, and their local
`creds.dat` will be built at that point.

---

## Ad-hoc / one-shot single server

To monitor a single server without any config file or DPAPI store:

```
SSHRtm.exe --host db01.example.com --user monitor
```

Password is prompted (or pass `--password …`, at the cost of leaking it into
process listings and shell history).

Full ad-hoc flag set:

| Flag              | Default        | Description                                       |
| ----------------- | -------------- | ------------------------------------------------- |
| `--host <name>`   | —              | Target host (also accepts `--server`).           |
| `--user <name>`   | —              | SSH username (required with `--host`).           |
| `--password <pw>` | prompted       | SSH password.                                    |
| `--port <n>`      | 22             | SSH port.                                        |
| `--name <label>`  | value of host  | Tile label.                                      |
| `--poll-ms <n>`   | 1000           | Poll interval, milliseconds.                     |
| `--render-ms <n>` | 500            | Render interval, milliseconds.                   |

Ad-hoc mode does not read or write `servers.json` or `creds.dat`.

---

## Keys

**Grid view**

| Key             | Action                                    |
| --------------- | ----------------------------------------- |
| Arrows / Tab    | Move selection.                           |
| `1`–`9`         | Jump to Nth server.                       |
| `PgUp` / `PgDn` | Page through servers when they overflow.  |
| `Home` / `End`  | Jump to first / last server.              |
| `c`             | Toggle standard ↔ compact tiles.          |
| `Enter`         | Drill into the selected server.           |
| `q` / `Esc`     | Quit.                                     |

**Detail view**

| Key             | Action                                          |
| --------------- | ----------------------------------------------- |
| `Tab` / `Shift+Tab` | Cycle focus between CPU / NET / DISK / FS / PROCS panels. |
| `↑` / `↓`       | Scroll the focused panel by one row.            |
| `PgUp` / `PgDn` | Scroll by five rows.                            |
| `Home` / `End`  | Jump to top / bottom of the focused panel.      |
| `Esc`           | Back to grid.                                   |
| `q`             | Quit.                                           |

---

## Requirements

- Windows 10 / 11 (uses DPAPI and Windows Console VT sequences).
- Target hosts: any Linux with a readable `/proc`, plus `df`, `ps`, `nproc`,
  `hostname`. Tested against RHEL 9.
- Network: outbound SSH to each target's port.
- Auth: password only (public-key auth is not currently wired up).
