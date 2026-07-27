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

## Follow-Bind Template Ownership

The master owns the fleet's follow bind: the friend-row fingerprint every VM matches in its friends drawer, and the ordered in-game nametag rolodex. Both are persisted in the master's SQLite `follow_template` row and replicated to each VM agent as `follow-template.txt` and `leader-template.txt` in its install directory.

`/d2r follow bind:true` and `bind-in-game` record the capture on the master first, then push it to every online account for an immediate, operator-visible result. Replication to everything else is reconciled rather than pushed once: each VM agent advertises a digest of what it currently holds in its status heartbeat, and the master sweeps the fleet every 60 seconds, at follow-auto start, and whenever a follow-auto check reports an unbound account. Any agent whose digest differs from the master's copy is repaired in place. This covers VMs that were offline during a bind, rebuilt from a clean image, or added to the fleet afterwards, including worker-owned VMs, whose agent connections are never visible to the master as events and only surface in worker inventory heartbeats.

The master tracks the friend-row bind and the nametag rolodex as separately "recorded" halves. A half the master has never recorded is never reconciled, so a host upgraded into this feature leaves existing agent-side templates untouched until the corresponding bind command runs once, and re-binding the friend row does not disturb in-game nametags the master has no copy of. A recorded but empty state is authoritative: an unbind reaches VMs that were offline when it happened.

Agents whose build predates the digest field cannot be diffed. The master falls back to what it last pushed each of them, so they converge once per bind change rather than on every sweep.

## Satellite Auto-Update

The master drives satellite auto-update for the whole fleet. Every five minutes it sweeps the
combined fleet view and offers `self_update` to each connected worker node and VM agent it has not
already offered one at that satellite's currently reported version, routing through the same
`agent_command` relay as any other command so worker-owned agents are reached identically to local
ones. Workers are offered first: a worker that is behind cannot relay commands it does not know
about.

This is snapshot-diffing rather than event-driven for the same reason follow-template sync is -
worker-owned VM agents never raise a connect event on the master, they only appear in a worker's
inventory heartbeat.

Each D2RHost still offers `self_update` to its own agents as they authenticate, which remains the
fast path. That local hook alone was not sufficient: it is gated on that host's own update check
having succeeded at process start, so a single failed check disables updates for every agent on
that node for the life of the process. The master's sweep is gated on the master's own check
instead, and a disabled sweep is now reported as a warning rather than a debug line.

A worker accepts `self_update` as a node command, so a worker no longer updates only when someone
restarts it by hand. The authentication hook covers worker nodes as well as VM agents, so a
reconnecting worker is offered an update immediately rather than waiting out the sweep's 30-second
startup delay. Previously only VM agents took that path, which is why `/d2r restart` appeared to
update every VM and do nothing to the node - the node's update was real but arrived quietly half a
minute later.

`/d2r restart` itself pushes nothing. It respawns the master, whose own startup self-update check
runs before Discord reconnects; every other fleet member updates through the two paths above.

Both offer paths share one `SatelliteUpdateGate`, which allows a single in-flight offer per satellite and remembers what it has already offered at each reported version. Separate per-path bookkeeping was not enough: after a master restart the two line up on the same satellite within seconds, because it reconnects and is offered an update immediately and the sweep's first pass then runs while it is still restarting and still reporting the old version. Two updaters unpacking the same release over the same directory is how a node reported two "update started" messages and stayed on the old build. `SelfUpdater` also refuses to launch a second updater in the same process, so a satellite does not depend on the host's bookkeeping being correct.

A worker whose build predates the `self_update` node command answers "unsupported worker command"
and cannot update itself out of that state. That reply is posted to Discord once per build rather
than only logged, because the failure is otherwise indistinguishable from a node that is already
current. Such a node needs one manual update.

`/d2r status` reports each node's version next to its agent counts, and marks a connected node
whose build differs from the master's. Versions are compared as the informational version string
agents advertise in their hello frame (`AgentVersion`), not the four-part assembly version.

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

- `GET /healthz`: mode, node ID, configured/connected agent counts, and the latest Windows Firewall reconciliation status. Its top-level `ok` follows firewall health. On a master it uses the combined fleet and includes node summaries; on a worker it uses local VM agents.
- `GET /agents`: combined fleet snapshots on a master; local VM-agent snapshots on a worker.
- `GET /nodes`: master/local-worker connectivity summaries on a master; the worker's own mode, node ID, and master URL on a worker.
- `GET /config/accounts`: currently known fleet account keys on a master; locally configured account keys on a worker.
- `WS /agent`: authenticated VM-agent connections.
- `WS /node`: authenticated worker-to-master connections. It uses the same base envelope as `/agent`, with `agentKind: "host"`.

The host app reads its config from the first CLI argument, then `CONFIG_PATH`, then `C:\D2ROps\d2r-host.config.json`. If that JSON is missing and the app has an interactive console, it launches first-run setup and writes the config before starting.

## Windows Firewall Lifecycle

Firewall migration is presence-sensitive. A config that omits the entire `windowsFirewall` object remains in legacy compatibility mode: management stays enabled and reconciliation continues, but the listener's local and remote address scopes remain unrestricted (`*`). A legacy worker's outbound rule also keeps an unrestricted local scope and an unrestricted remote scope for a hostname; a literal master IP remains exact. Saving such a config preserves the omission. Adding the object is an explicit opt-in to scoped behavior; fields omitted from a present object default to `manage: true`, `trustedNetworks: ["LocalSubnet"]`, and `reconcileSeconds: 30`.

In explicitly scoped mode:

- Every master and worker owns an inbound TCP allow rule scoped to the current D2RHost executable, `httpPort`, active usable IPv4 and IPv6 addresses on non-loopback interfaces, and `trustedNetworks`. IPv6 link-local addresses are excluded. If enumeration produces no usable address or raises a network-information error, the local-address filter uses Windows' dynamic `LocalSubnet` token.
- A worker also owns an outbound TCP allow rule for `masterUrl`. A literal IPv4 or IPv6 master address produces an exact remote-address rule; a hostname uses `trustedNetworks` rather than a captured DNS result. The remote port comes from the URL, including the normal 80/443 defaults for `ws`/`wss`.
- Rules apply to all Windows profiles but remain remote-address scoped. `trustedNetworks` accepts `LocalSubnet`, individual IPv4 or IPv6 addresses, and CIDRs, but rejects unrestricted networks.

For a same-LAN deployment with the master at `10.2.39.65` and worker at `10.2.39.66`, reserve or statically configure those addresses and use `["10.2.39.0/24"]` as `trustedNetworks` on both nodes. The worker can then use `ws://10.2.39.65:8080/node`; a stable LAN hostname is also valid. Add narrower VM-network CIDRs when VM agents originate outside that `/24`.

The firewall layer does not create routes or discover a replacement for an incorrect literal `masterUrl`; ordinary Windows routing between same-LAN nodes is assumed. Use a static address, DHCP reservation, or stable hostname for the master and diagnose host-to-host reachability separately from firewall-rule reconciliation.

Rule names and groups contain a stable owner identifier derived from the canonical config path. Consequently, multiple configs using the same executable do not retire one another's owner-specific rules. Legacy unsuffixed managed rules and rules whose names begin with `D2ROps Host inbound TCP` are eligible for migration cleanup only when their program path matches the running executable, their direction and protocol are inbound TCP, and their local port matches the current listener port.

The host reconciles at startup before serving traffic, on Windows network-address changes, and every configured interval (5-3600 seconds). Address changes are applied live; other configuration changes take effect on restart. A replacement is fully assembled as a detached COM rule before Windows is asked to install it, preserving the installed rule if replacement fails. When a desired rule is repaired, it must be read back successfully and remain stable through a follow-up reconciliation before stale owner-specific or qualifying legacy rules are removed.

Effective policy is checked before mutation and again before cleanup. A Group Policy override or inbound block, no recognized active profile, or Windows Firewall being disabled on any active profile makes firewall health false and suppresses stale-rule cleanup. `/healthz` exposes that state and its top-level `ok` becomes false, while D2RHost continues running. Local mutation requires elevation; the scheduled task runs as `SYSTEM`. With `manage: false`, D2RHost does not list, create, replace, or remove local firewall rules.

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

Discord `/d2r system sleep` defaults to every node that is online when the command is handled, matching the post-follow quick `Sleep` action: parking for the night should not leave a worker awake burning power and running VMs. `/d2r system shutdown|restart` still default to the master node, because those are normally aimed at one machine. `node:<node-id>` scopes the action to one known node, including the master; `all:false` narrows sleep back to the master alone; `all:true` widens shutdown/restart to the master and every online worker. Fleet-wide actions are queued on workers first and the master last so the master can forward every worker command before powering itself down. Offline workers are listed and skipped; if a selected worker fails to confirm the action, the master remains online for recovery. These actions target physical D2RHost machines, never VM guests.

Sleep is the only power action without a `shutdown.exe` equivalent, so it calls `SetSuspendState` directly. That API requires `SE_SHUTDOWN_NAME` to be *enabled* in the process token - running elevated is not enough, because an administrator's token holds the privilege in the disabled state and the call then fails with ERROR_PRIVILEGE_NOT_HELD. `WindowsShutdownPrivilege` enables it, and the attempt is made synchronously before the command answers, so a host that genuinely cannot sleep reports that in Discord instead of reporting the action as queued and staying awake.

`SetSuspendState` blocks until the machine resumes, so a call that returns immediately means the transition was refused rather than slept-and-woken - the failure mode on Modern Standby laptops and Hyper-V hosts, where the API can report success without suspending. A return faster than five seconds is therefore treated as a failure, logged with `powercfg /a` output naming which sleep states the machine actually supports, and posted to Discord by the master. This matters because the symptom is invisible: on a headless host with the screen already off, a sleep that never happened looks exactly like one that worked. The post-follow quick `Sleep` action captures one online-node target set, reconciles clients that appear on those nodes during its quit pass, then uses the same worker-first behavior.

## Control-Plane Failure

There is no automatic election or failover between D2RHost instances. If the master stops, worker processes keep their local VM-agent connections and retry the outbound connection, but Discord commands and global orchestration stop. When the master returns, workers reconnect and publish fresh inventory.

The master is therefore a control-plane single point of failure. Put it on an always-on management server if global control must remain available while a Hyper-V worker is powered down. Promotion of a worker requires deliberate reconfiguration; simultaneous masters are not supported.
