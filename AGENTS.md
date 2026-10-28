# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers configuration, the command-line flags,
the keys and the CSV schema in detail; this file covers how the code fits together and what to watch.

## What this is

A real-time terminal monitor for Linux servers (tested against RHEL 9), run from a Windows console. It polls
`/proc`, `df` and `ps` over persistent SSH sessions (SSH.NET) and draws a btop-style TUI with Spectre.Console.
Nothing is installed on the targets. It targets `net9.0-windows` and uses DPAPI, so it builds and runs on Windows
only.

## Commands

```bash
dotnet build SSHRtm.sln                           # .NET 9 SDK, on Windows
dotnet publish SSHRtm.csproj -c Release           # self-contained single-file win-x64 (set in the .csproj)
SSHRtm.exe --help
SSHRtm.exe --config D:\path\to\servers.json       # choose the config; creds.dat lives beside it
SSHRtm.exe --set-passwords                        # re-prompt and re-store every password, then exit
SSHRtm.exe --host db01 --user monitor [--port 22 --name x --poll-ms 1000 --render-ms 500]   # ad hoc, no files
```

There are no tests yet.

## Layout

- `Program.cs`: argument parsing, config lookup (exe folder, then `%APPDATA%\SSHRtm`), the sample config on first
  run, password prompts, then starts the pollers and the UI.
- `Config.cs`: `AppConfig` and `ServerConfig`, read from `servers.json` (comments and trailing commas allowed).
- `DpapiCredentialStore.cs`: `creds.dat`, passwords encrypted with DPAPI `CurrentUser` scope.
- `SshPoller.cs`: one SSH session per server; `PollCommand` runs every command in one round trip, separated by
  `==SEP:...==` markers.
- `PollerHost.cs`: runs a poller task per server and writes results into `StateStore`.
- `StateStore.cs`: a concurrent map of `ServerState` (status, last error, latest `Snapshot`).
- `Metrics/`: `ProcParser` splits and parses the output; `Deltas` turns counters into rates; `Snapshot.cs` holds
  the records.
- `UI/`: `App.cs` (render loop, keys, CSV logging), `GridView.cs`, `DetailView.cs`, `Theme.cs`.
- `CsvLogger.cs`: per-server CSV files written beside the exe.
- `config/servers.json`: the sample configuration.

## Rules that keep it working

- **Keep `PollCommand` and `ProcParser` in step.** Each section name after `==SEP:` is what the parser looks up.
  Add a section to both, or neither.
- **Rates need two samples.** `BuildSnapshot` works from the previous `RawCounters` and the elapsed time; the first
  sample of a session has no rates.
- **Polling and drawing are separate.** Pollers write to `StateStore` on their own interval; the UI reads it on
  the render interval. Don't call SSH from the UI.
- **Passwords never go in `servers.json`.** They are stored only in `creds.dat`, which only the same Windows user
  can decrypt. Don't add plain-text storage.
- **The CSV header is frozen** from the first snapshot of each file; later new NICs, disks or mounts are dropped
  for that file (see README).
- **Line endings.** `.gitattributes` sets `* text=auto`.
- The tracked `.claude/settings.local.json` is local tool configuration, not part of the program.

## Making a change

- Build on Windows and try it against a real Linux host; there are no tests.
- Update `README.md` (flags, keys, CSV schema) to match.

There is no CI workflow.
