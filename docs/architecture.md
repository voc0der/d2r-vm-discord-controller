# Architecture

```text
Discord slash commands
  -> D2RHost.exe on the Windows Hyper-V host
    -> HTTP health API
    -> WebSocket server at /agent
      <- outbound VM agents
    -> SQLite state
    -> local Hyper-V PowerShell cmdlets
```

`D2RHost` is the only process that talks to Discord. It also owns the VM WebSocket listener, account mapping, SQLite state, and Hyper-V operations.

## D2RHost

The host app runs on the Windows Hyper-V machine and owns:

- Discord slash command registration and handling.
- Account-to-VM-agent mapping.
- VM-agent authentication and connection state.
- Command request/response correlation.
- SQLite status, command history, and current-game persistence.
- HTTP health endpoints.
- Hyper-V VM status, start, stop, reboot, and checkpoint commands.

The host app reads its config from the first CLI argument, then `CONFIG_PATH`, then `C:\D2ROps\d2r-host.config.json`. If that JSON is missing and the app has an interactive console, it launches first-run setup and writes the config before starting.

Useful environment overrides:

- `DISCORD_TOKEN`
- `DISCORD_GUILD_ID`
- `DISABLE_DISCORD`
- `HTTP_PORT`
- `DB_PATH`
- `CLIENT_STAGGER_SECONDS`

## VM Agent

The VM agent runs inside each Windows VM as the logged-in user. It should be started by a scheduled task at logon because Battle.net, D2R, screenshots, and menu clicks all live in the interactive desktop session.

If `vm-agent.config.json` is missing, the VM agent prompts for its host URL, agent ID, shared secret, and Battle.net path, then writes the JSON. On later starts it probes the host; if the probe fails in an interactive console, it offers to update hostname/port and save the JSON.

It supports:

- Battle.net process status.
- D2R process status.
- Launch Battle.net or configured D2R path.
- Kill/restart D2R, or focus D2R and quit with Alt+F4.
- Primary-screen screenshot capture.
- Battle.net Play plus D2R intro click-through.
- Character select, Lobby, Play, Join Game, Create Game, Join Friend, and Save and Exit menu flows.

## Hyper-V Control

Hyper-V control now runs inside `D2RHost`; there is no separate host-side WebSocket agent.

`D2RHost` uses local PowerShell cmdlets for:

- `Get-VM` status.
- `Start-VM`.
- `Stop-VM -Force`.
- `Restart-VM -Force`.
- `Checkpoint-VM`.

Use `allowedVmNamePrefixes` to limit which VM names the host is allowed to operate.
