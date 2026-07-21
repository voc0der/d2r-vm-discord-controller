# Architecture

```text
Discord slash commands
  -> D2RHost.exe, mode=master, Server A
      -> authoritative SQLite and global orchestration state
      -> HTTP health/inventory API
      -> local Hyper-V PowerShell cmdlets
      <- local VM agents over WebSocket /agent
      <- D2RHost.exe, mode=worker, Server B, over WebSocket /node
          -> local Hyper-V PowerShell cmdlets
          <- local VM agents over WebSocket /agent
```

`D2RHost.exe` is the host-side binary for both roles. A config with no `mode` remains backward compatible: it runs as `master`; an omitted `nodeId` defaults to `local`.

Release builds can rename the published host and VM-agent executables with `HOST_EXE_NAME` and `AGENT_EXE_NAME`. The project names and release zip names remain `D2RHost` and `D2RAgent`.

## Master D2RHost

The master is the control plane. It owns:

- Discord slash-command registration and handling.
- Authoritative SQLite game, automation, status, and command-history state.
- Global account selection, staggering, and fan-in results.
- Authentication and connection state for its local VM agents.
- Authentication and connection state for worker D2RHost instances.
- The combined fleet view used by Discord commands and master HTTP endpoints.
- Local Hyper-V operations for accounts owned by the master node.

The master's `agents` object contains its local VM agents as `kind: "vm"` and one entry for each allowed worker as `kind: "host"`. A host entry's key is the worker's `nodeId`, and its `sharedSecret` must match that worker's `masterSharedSecret`.

The master does not duplicate a worker's VM-agent or account configuration. It receives the worker's non-secret inventory through worker status heartbeats and merges that inventory with its local agents and accounts. Commands for a remote VM agent are wrapped in an `agent_command` and sent through the owning worker. Hyper-V commands for a remote account are sent to the worker and executed by PowerShell on that physical server.

Only master mode can start the Discord bot. `disableDiscord` can still be used to run a master without Discord for testing.

## Worker D2RHost

A worker is a data-plane host for one physical Hyper-V server. It owns:

- Its local VM-agent `agents` configuration and shared secrets.
- Its local `accounts` mapping and Hyper-V VM names.
- Its local `/agent` listener and VM-agent connection state.
- Local Hyper-V status, start, stop, reboot, and checkpoint execution.
- An outbound authenticated WebSocket connection to the master.

Worker mode requires a unique `nodeId`, an absolute `ws://` or `wss://` `masterUrl`, and a `masterSharedSecret` of at least 12 characters. D2RHost binds plain HTTP/WebSocket itself, so `wss://` requires a TLS-terminating reverse proxy in front of the master. If `masterUrl` has no path, `/node` is appended. Worker mode always disables Discord regardless of the configured `disableDiscord` value.

The worker identifies to the master as an agent with `agentKind: "host"` and `agentId` equal to its `nodeId`. Its heartbeat advertises host telemetry, configured local accounts, and local VM-agent snapshots. Shared secrets are deliberately excluded from that inventory.

VM agents always connect to the D2RHost on their own physical server. This keeps local VM connections available while the worker-to-master link reconnects and avoids exposing every VM directly to the master.

## Identity and Configuration Ownership

These identifiers must be unique, case-insensitively, across the entire fleet:

- Account keys used by Discord commands.
- VM-agent IDs.
- Node IDs, which also occupy the master agent-ID namespace as `kind: "host"` agents.

Each account exists only in the config of its owning node and references a VM agent in that same config. `account.nodeId`, when present, must equal that D2RHost's own `nodeId`; when omitted, it is filled from the local node ID during config loading. Duplicate IDs advertised by workers cannot be routed safely and are ignored by the master's fleet view.

For backward compatibility only, a pre-topology single-host config that omits `nodeId` may retain a VM agent literally named `local`. Explicitly node-aware configs reject all node/agent collisions.

## Availability and Routing

Both VM agents and workers send periodic status heartbeats. `nodeHeartbeatSeconds` controls the worker-to-master interval, while each VM agent has its own `heartbeatSeconds`. The effective interval is included in the authenticated hello. `agentOfflineAfterSeconds` is the receiver's minimum freshness threshold (45 seconds by default); for a slower advertised interval, the receiver automatically extends it by the bounded status-collection/jitter allowance.

The master treats a worker account as online only when both conditions are true:

- The worker's authenticated connection and heartbeat are fresh.
- That worker's advertised VM-agent snapshot is connected and fresh.

If a worker disconnects or goes stale, all accounts owned by that worker become unavailable. If the worker remains online but one VM agent disconnects or goes stale, only that VM agent's account becomes unavailable. Fleet-wide client commands select available accounts and skip unavailable ones, while an explicitly targeted unavailable account reports an offline failure. Other nodes continue to be orchestrated.

Hyper-V routing depends on worker availability, not the VM agent running inside the guest. Consequently, `/d2r vm start` can start a stopped worker-owned VM as long as its worker D2RHost is online. The worker applies its own `allowedVmNamePrefixes`, PowerShell path, and timeout to that command and advertises the non-secret timeout budget so the master waits for the worker's policy rather than its own local PowerShell setting.

Commands are not retried automatically. If a worker disconnects after accepting a destructive VM or system command but before its result reaches the master, the response reports the outcome as unknown because the operation may already have taken effect. Reconcile status before retrying.

## HTTP and WebSocket Surface

Every D2RHost listens on its configured `httpPort` and exposes:

- `GET /healthz`: mode, node ID, and configured/connected agent counts. On a master it uses the combined fleet and includes node summaries; on a worker it uses local VM agents.
- `GET /agents`: combined fleet snapshots on a master; local VM-agent snapshots on a worker.
- `GET /nodes`: master/local-worker connectivity summaries on a master; the worker's own mode, node ID, and master URL on a worker.
- `GET /config/accounts`: currently known fleet account keys on a master; locally configured account keys on a worker.
- `WS /agent`: authenticated VM-agent connections.
- `WS /node`: authenticated worker-to-master connections. It uses the same base envelope as `/agent`, with `agentKind: "host"`.

The host app reads its config from the first CLI argument, then `CONFIG_PATH`, then `C:\D2ROps\d2r-host.config.json`. If that JSON is missing and the app has an interactive console, it launches first-run setup and writes the config before starting.

Useful environment overrides are:

- `DISCORD_TOKEN`
- `DISCORD_GUILD_ID`
- `DISABLE_DISCORD`
- `HTTP_PORT`
- `DB_PATH`
- `CLIENT_STAGGER_SECONDS`

## VM Agent

The VM agent runs inside each Windows VM as the logged-in user. It should be started by a scheduled task at logon because Battle.net, D2R, screenshots, and menu clicks all live in the interactive desktop session.

If `vm-agent.config.json` is missing, the VM agent prompts for its local D2RHost URL, agent ID, shared secret, and Battle.net path, then writes the JSON. On later starts it probes that host; if the probe fails in an interactive console, it offers to update hostname/port and save the JSON.

It supports:

- Battle.net and D2R process status.
- Launch, kill, restart, focus, and Alt+F4 quit.
- Character-screen idle cleanup after the configured timeout.
- Primary-screen screenshot capture.
- Battle.net Play plus D2R intro click-through.
- Character select, Lobby, Play, Join Game, Create Game, Join Friend, and Save and Exit menu flows.

## Hyper-V and System Power Control

There is no separate host-side agent binary. Each D2RHost executes Hyper-V PowerShell commands only for VMs owned by its physical server:

- `Get-VM` status.
- `Start-VM`.
- `Stop-VM -Force`.
- `Restart-VM -Force`.
- `Checkpoint-VM`.

Use `allowedVmNamePrefixes` independently on every node to constrain which local VM names it may operate.

Discord `/d2r system sleep|shutdown|restart` defaults to the master node for backward compatibility. `node:<node-id>` targets one known node, including the master; `all:true` targets the master and every worker that is online when the command is handled. Fleet-wide actions are queued on workers first and the master last so the master can forward every worker command before powering itself down. Offline workers are listed and skipped; if a selected worker fails to confirm the action, the master remains online for recovery. These actions target physical D2RHost machines, never VM guests. The post-follow quick `Sleep` action captures one online-node target set, reconciles clients that appear on those nodes during its quit pass, then uses the same worker-first behavior.

## Control-Plane Failure

There is no automatic election or failover between D2RHost instances. If the master stops, worker processes keep their local VM-agent connections and retry the outbound connection, but Discord commands and global orchestration stop. When the master returns, workers reconnect and publish fresh inventory.

The master is therefore a control-plane single point of failure. Put it on an always-on management server if global control must remain available while a Hyper-V worker is powered down. Promotion of a worker requires deliberate reconfiguration; simultaneous masters are not supported.
