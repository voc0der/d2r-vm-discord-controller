# Architecture

```text
Discord slash commands
  -> Node.js controller
    -> WebSocket server at /agent
      <- outbound VM agents
      <- outbound Hyper-V host agent
```

The controller is the only process that talks to Discord. Agents call home over outbound WebSocket connections, authenticate with per-agent shared secrets, and wait for commands.

## Controller

The controller owns:

- Discord slash command registration and handling.
- Account-to-agent mapping.
- Agent authentication and connection state.
- Command request/response correlation.
- SQLite status and command history persistence.
- HTTP health endpoints.

The controller reads `CONFIG_PATH`, defaults to `/config/controller.config.json` in Docker, and stores SQLite state at `DB_PATH`.

## VM Agent

The VM agent runs inside each Windows VM as the logged-in user. It should be started by a scheduled task at logon because Battle.net, D2R, and screenshots all live in the interactive desktop session.

It supports:

- Battle.net process status.
- D2R process status.
- Launch Battle.net or configured D2R path.
- Kill/restart D2R.
- Primary-screen screenshot capture.

## Hyper-V Agent

The Hyper-V agent runs on the host machine with enough privilege to call Hyper-V PowerShell cmdlets.

It supports:

- `Get-VM` status.
- `Start-VM`.
- `Stop-VM -Force`.
- `Restart-VM -Force`.
- `Checkpoint-VM`.

Use `allowedVmNamePrefixes` to limit which VM names the agent is allowed to operate.
