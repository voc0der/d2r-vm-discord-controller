# Agent Protocol

`D2RHost` exposes two WebSocket paths using the same authenticated JSON envelope:

- `/agent`: a VM's `D2RAgent` connects to the D2RHost on its physical server as `agentKind: "vm"`.
- `/node`: a worker D2RHost connects outbound to the master D2RHost as `agentKind: "host"`.

VM agents must point to their local D2RHost, not directly to the master. A worker gets its `/node` URL from `masterUrl`; when that URL has no path, the worker appends `/node`. The built-in listener is plain WebSocket; `wss://` is supported when a TLS-terminating reverse proxy fronts it.

## Handshake

The first message must be a hello:

```json
{
  "type": "hello",
  "agentId": "d2r-hc-01",
  "agentKind": "vm",
  "sharedSecret": "replace_me",
  "version": "0.1.0",
  "hostName": "D2R-HC-01",
  "heartbeatSeconds": 15
}
```

The receiving D2RHost authenticates `agentId`, `agentKind`, and `sharedSecret` against its local `agents` configuration. A master therefore lists each worker by its `nodeId` with `kind: "host"` and the worker's `masterSharedSecret`.

For startup connection tests, an agent may include `"probeOnly": true`. `D2RHost` authenticates it, replies with `hello_ack`, and closes the probe socket without replacing an existing connection:

```json
{
  "type": "hello_ack",
  "agentId": "d2r-hc-01",
  "ok": true
}
```

## VM-Agent Status

VM agents periodically push status to their local D2RHost:

```json
{
  "type": "status",
  "agentId": "d2r-hc-01",
  "status": {
    "battleNetRunning": true,
    "d2rRunning": false,
    "d2rActivityState": "Unknown",
    "characterScreenIdleSinceUtc": null,
    "lastLobbyOrGameInteractionUtc": null,
    "idleQuitEnabled": true,
    "idleQuitMinutes": 30
  }
}
```

`d2rVisibleState` carries one extra value that is not a pixel classification:
`GraphicsDeviceFailure`, set when D2R's native `Failed to initialize graphics device` dialog is on
screen. It comes with a `d2rGraphicsDeviceFailure` object so the failure is diagnosable without a
screenshot:

```json
{
  "detected": true,
  "streak": 2,
  "relaunchLimit": 5,
  "needsVmPowerCycle": false,
  "lastSeenUtc": "2026-08-01T14:28:00Z",
  "detail": "\"Error\" (#32770) owned by D2R (pid 4812), OK button found"
}
```

`streak` counts this incident's dismiss-and-relaunch attempts and `needsVmPowerCycle` reports that
the agent has stopped retrying, which is the host's cue to power-cycle the guest. The object is
omitted (null) when the dialog is absent and no incident is open. Agents that predate these fields
omit both, which reads as "no graphics-device failure".

`d2rVisibleState` carries a second such value: `GammaCalibration`, set when D2R has reset its own
`Settings.json` and stopped on its first-run gamma screen instead of reaching character select (see
[ui-state-catalog.md](runbooks/ui-state-catalog.md#settingsjson-corruption)). It comes with a
`d2rSettingsRepair` object:

```json
{
  "detected": true,
  "state": "GammaCalibration",
  "sightings": 2,
  "confirmSightings": 2,
  "needsDonorSettings": true,
  "repairEnabled": true,
  "firstSeenUtc": "2026-08-02T13:20:00Z",
  "lastSeenUtc": "2026-08-02T13:24:00Z",
  "settingsPath": "C:\\Users\\d2r\\Saved Games\\Diablo II Resurrected\\Settings.json",
  "settingsReadable": false,
  "settingsSha256": null,
  "settingsLength": null,
  "settingsLastWriteUtc": null,
  "settingsError": "... is not usable as a settings donor: root object has only 1 property ...",
  "repairsApplied": 0,
  "lastRepairUtc": null,
  "lastRepairMessage": null
}
```

`needsDonorSettings` is the flag the host acts on: the screen has been seen `confirmSightings` times
in a row and this agent has repair enabled. `detected` alone (one sighting) is enough to keep a VM
out of the donor pool but not to overwrite anything. The object is omitted (null) when the client is
healthy and no repair has ever been applied; agents that predate it omit it, which reads the same.

## Settings Repair Commands

Two VM-agent commands move a settings file between fleet members. Both are master-orchestrated,
because the donor and the broken VM can sit on different physical nodes.

`settings_export` takes no arguments and returns the caller's settings file verbatim. It bypasses
the agent's UI command gate (it only reads a file) and refuses if the exporting client is itself on
the gamma screen:

```json
{
  "path": "C:\\Users\\d2r\\Saved Games\\Diablo II Resurrected\\Settings.json",
  "content": "{ ... }",
  "sha256": "9f2c...",
  "length": 1042,
  "lastWriteUtc": "2026-07-30T22:14:03Z"
}
```

`settings_repair` takes `{ "settingsContent": "...", "settingsSourceAgentId": "d2r-hc-01" }`. The
receiving agent validates the payload (real JSON, an object, at least 3 properties, 64 bytes to
512 KB - deliberately schema-free, since D2R's key names change across game patches), quits D2R,
waits `settingsRepairSettleSeconds` for the client's own exit write to land, backs the existing file
up next to itself as `Settings.json.<timestamp>.bak`, and writes the donor copy through a temp file
plus rename. It fails rather than writing if the client is still running. It does not relaunch - the
host issues `menu_ready` afterwards, so retry and escalation stay in one place.

## Worker-to-Master Status

A worker connects to the master with the same hello envelope, using its `nodeId` as the agent ID:

```json
{
  "type": "hello",
  "agentId": "server-b",
  "agentKind": "host",
  "sharedSecret": "replace_with_long_random_node_secret_02",
  "version": "0.1.0",
  "hostName": "SERVER-B",
  "heartbeatSeconds": 15
}
```

The worker sends a heartbeat containing physical-host telemetry and its local, non-secret inventory:

```json
{
  "type": "status",
  "agentId": "server-b",
  "status": {
    "nodeId": "server-b",
    "hostName": "SERVER-B",
    "capturedAtUtc": "2026-07-21T12:00:00Z",
    "vmCommandTimeoutSeconds": 90,
    "vmSafeHostPowerTransitions": true,
    "machineTelemetry": {
      "memoryTotalBytes": 68719476736,
      "memoryAvailableBytes": 34359738368,
      "memoryUsedBytes": 34359738368,
      "cpuPercent": 18.5
    },
    "agents": [
      {
        "id": "d2r-hc-03",
        "kind": "vm",
        "displayName": "BO-03 VM Agent",
        "remoteUrl": null,
        "snapshot": {
          "id": "d2r-hc-03",
          "kind": "vm",
          "displayName": "BO-03 VM Agent",
          "hostName": "D2R-HC-03",
          "version": "0.1.0",
          "connected": true,
          "connectedAt": "2026-07-21T11:50:00Z",
          "lastSeenAt": "2026-07-21T12:00:00Z",
          "lastStatusJson": "{}",
          "statusReceivedAt": "2026-07-21T12:00:00Z"
        }
      }
    ],
    "accounts": [
      {
        "key": "hc3",
        "agentId": "d2r-hc-03",
        "displayName": "BO-03",
        "vmName": "d2r-hc-03",
        "characterSlot": 1
      }
    ]
  }
}
```

Worker heartbeats never serialize either the worker-to-master secret or local VM-agent secrets. `nodeHeartbeatSeconds` controls their interval (clamped to 5-300 seconds by the link). The receiver treats `agentOfflineAfterSeconds` as a minimum and extends it when needed for the authenticated client-advertised interval plus bounded status-collection jitter. Each remote VM's `connected` value is trusted from the worker that evaluated it against that worker's clock and policy. `vmCommandTimeoutSeconds` advertises the worker-owned Hyper-V command budget, capped by the 15-minute worker safety limit. `vmSafeHostPowerTransitions` explicitly says this worker implements the durable state-check/stop/restore transaction; absence, `false`, or a non-boolean value means unsupported.

Account keys, VM-agent IDs, and node/host-agent IDs must be globally unique, case-insensitively. The master combines advertised worker inventory with its local configuration. A disconnected or stale worker makes all of its advertised VM agents unavailable; a stale local VM-agent snapshot affects only that agent. Fleet orchestration skips unavailable accounts and continues with other nodes.

## Worker Commands

The master tunnels a VM-agent command through its owning worker with `agent_command`:

```json
{
  "type": "command",
  "commandId": "outer-uuid",
  "command": "agent_command",
  "timeoutMs": 65000,
  "args": {
    "agentId": "d2r-hc-03",
    "command": "menu_ready",
    "args": {
      "accountKey": "hc3",
      "vmName": "d2r-hc-03"
    },
    "timeoutMs": 60000
  }
}
```

The worker validates the nested request and sends it through its local VM-agent registry. It returns the nested result in the normal `command_result` envelope. If the worker or local VM agent is offline/stale, the command fails without affecting other fleet targets.

The worker also accepts these physical-host commands from the master:

```text
vm_status
vm_start
vm_stop
vm_reboot
vm_snapshot
system_sleep
system_shutdown
system_restart
```

VM commands require `args.vmName`; `vm_snapshot` optionally accepts `args.snapshotName`. They execute through the worker's local PowerShell configuration and `allowedVmNamePrefixes`.

The `system_*` commands still act on the worker's physical Windows machine, but they first run a local VM safety transaction. The worker takes the distinct, non-empty `vmName` values from its own `accounts` configuration, reads every Hyper-V state, and durably records only the VMs that are `Running`. It then stops and confirms those VMs `Off` before queueing sleep (or its hibernation fallback), shutdown, or restart. VMs already `Off` are not recorded and therefore stay off. A failed query or any other state, including a transitional, paused, or saved state, fails the command without queueing the host action. A partial stop pass is rolled back; a rollback that cannot finish remains in the worker's local SQLite journal for startup recovery.

After a successful preparation, the worker keeps that host transition armed in memory and rejects another `system_*` power command until resume or failure recovery releases it. This guard is independent of the journal contents, so it also applies when no configured VM was `Running`.

A successful worker system command returns the normal `command_result`. Its `data` includes the exact recorded set:

```json
{
  "nodeId": "server-b",
  "action": "sleep",
  "queued": true,
  "stoppedVms": ["d2r-hc-03", "d2r-hc-04"]
}
```

The resume journal belongs to the node performing the Hyper-V work; it is not sent to or owned by the master. After ordinary sleep, the still-running worker process restores the recorded VMs when the suspend call returns and retries failures every 15 seconds. After shutdown/restart, the worker's startup task reads its own `databasePath` and restores them before reconnecting normally. If `shutdown.exe` returns but the worker process is still alive 30 seconds later, its watchdog treats that host transition as failed and restores the journal in-process. Restore is idempotent: an entry already `Running` is cleared without another start, and an entry is otherwise cleared only after `Start-VM` confirms `Running`.

The `system_*` command names predate this state machine and did not change on the wire, so the master requires explicit capability negotiation before dispatch. `vmSafeHostPowerTransitions:true` must come from a status frame received on the worker's current authenticated connection. A cached `true` retained for inventory display across a reconnect does not count until that socket sends a new status. The eventual command send is also bound to that connection generation; if the worker reconnects between authorization and send, the send is rejected and the replacement connection must publish status first. Older workers, malformed flags, and the hello-before-first-status window therefore fail closed for sleep, shutdown, and restart; the result asks the operator to update/restart the worker and wait for a fresh heartbeat.

Commands are not retried across a disconnect, and canceling the master's wait does not recall an already-dispatched command. A lost result after dispatch is an unknown outcome: a VM or system action may already have applied, so callers should reconcile current state before issuing it again. System preparation can now include multiple Hyper-V state/stop checks, so the master waits the worker-advertised `vmCommandTimeoutSeconds` plus transport headroom instead of the former fixed 20-second acknowledgement window.

Discord `system sleep` targets every online node by default; `system shutdown` and `system restart` default to the master node. `node:<node-id>` targets that known node (and may explicitly name the master), while `all:true`—or the sleep default—targets currently online workers first and the master last. `all:false` narrows any of them back to the master alone. Offline workers are listed and skipped. A worker without the current-connection VM-safe power capability, or whose Windows sleep preflight reports no usable sleep state, returns failure before queueing; any selected worker failure keeps the master online for recovery.

## VM-Agent Commands

`D2RHost` sends commands:

```json
{
  "type": "command",
  "commandId": "uuid",
  "command": "launch_d2r",
  "timeoutMs": 55000,
  "args": {
    "accountKey": "hc1",
    "vmName": "d2r-hc-01"
  }
}
```

The top-level `timeoutMs` is the receiver-side deadline and is normally five seconds shorter than the sender's wait budget, leaving time for the failure result to travel back. For `agent_command`, the nested `args.timeoutMs` independently controls the worker's wait for its local VM agent.

Menu automation commands use the same envelope and are handled by the VM agent:

```text
menu_ready
menu_lobby
menu_play
menu_prepare_join_game
menu_submit_join_game
menu_join_game
menu_create_game
menu_join_friend
menu_follow_bind
menu_follow_bind_game
menu_follow_auto_check
menu_save_exit
follow_set_template
follow_clear_template
follow_set_leader_template
follow_remove_leader_template
follow_clear_leader_template
follow_stop_auto
sample_player_count
self_update
quit_d2r
```

Game-oriented menu commands accept optional args such as:

```json
{
  "characterSlot": 1,
  "friendRow": 1,
  "partyPosition": 3,
  "gameName": "baal-001",
  "password": "pw",
  "difficulty": "hell"
}
```

`menu_follow_bind` captures a friends-drawer name fingerprint from the selected `friendRow`;
`menu_follow_bind_game` captures an in-game party-bar name mask from the visible portrait at
`partyPosition` (1-8, counted left to right on the vantage account's screen). Both reply with a
`fingerprint` string that the host distributes to every online agent via `follow_set_template` /
`follow_set_leader_template` (`{ "fingerprint": "..." }`); the agents persist them next to the
executable as `follow-template.txt` and `leader-template.txt`.

`leader-template.txt` holds one serialized nametag per line, in bind order (a pre-multi
single-line file is a one-entry list): the operator binds one nametag per alt they play.
`follow_set_leader_template` with `{ "fingerprint": "...", "append": true }` appends a nametag
(exact duplicates collapse); without `append` it replaces the whole list.
`follow_remove_leader_template` (`{ "fingerprint": "..." }`) removes exactly one entry - the
bind-verification rollback uses it so a bad capture never wipes the other bound alts.
`follow_clear_template` removes both files (a full unbind); `follow_clear_leader_template`
removes all bound nametags.

Every VM-agent status frame carries the digests of both replicas so the master can reconcile
them against its own persisted copy without shipping the fingerprints on each heartbeat:

```json
"followTemplates": {
  "friendDigest": "3f0a1c...",
  "leaderDigest": "none",
  "leaderCount": 0,
  "error": null
}
```

Both digests are computed over the same canonical form the agent's loaders accept - the trimmed
fingerprint, and the normalized newline-joined nametag list - so formatting differences never read
as divergence. `none` means the replica is empty, absent, or unreadable; an unreadable file is
reported as empty so the next push repairs it. A status frame with no `followTemplates` object at
all is an older agent, which the master treats as "cannot tell" rather than "holds nothing".

`sample_player_count` accepts an optional `{ "fingerprint": "..." }` (the host's session-locked
nametag; the scan short-circuits once that entry is found) and replies with the current in-game
player count plus a per-nametag reading for every bound entry:

```json
{
  "playerCount": 5,
  "lastPartyMemberCount": 4,
  "lastPartyMemberCountUtc": "...",
  "visibleState": "InGame",
  "inGame": true,
  "leaderBound": true,
  "leaderPresent": true,
  "leaderSlot": 2,
  "leaderScore": 0.832,
  "leaderMatches": [
    { "fingerprint": "pn1:...", "present": false, "slot": null, "score": 0.31 },
    { "fingerprint": "pn1:...", "present": true, "slot": 2, "score": 0.832 }
  ]
}
```

Each `present` is `null` (not `false`) whenever that entry could not actually be checked - not
visibly in a game, the capture failed, or the stored template doesn't fit this vantage's bands -
so the host can distinguish "definitely gone" from "this pulse couldn't check". The top-level
`leaderPresent`/`leaderSlot`/`leaderScore` are an any-nametag aggregate kept for display and
older hosts; decisions are made from `leaderMatches`, keyed by fingerprint content so agents
whose stored lists diverged can never be misread by index.

`visibleState` is the sampling client's own screen (the `d2rVisibleState` classifier value) and
`inGame` is its verdict about itself: `true` only for `InGame`, `false` for the screens D2R
cannot show from inside a game (`LobbyOrGame`, `CharacterScreen`, `OfflineCharacterScreen`,
`NotRunning`, `GraphicsDeviceFailure`), and `null` for `DiabloSplash`/`Unknown` - a load screen or
degraded capture is not evidence. Follow-auto's watch uses a `false` from a bot it counts as joined to rejoin that bot
mid-game (a post-join "Connection Interrupted" drops one client back to the lobby, where every
other field in this reply is a `null` that reads as "nothing to report"). Agents that predate
these fields simply omit them, so an older agent never triggers that rejoin.

Follow-auto's host-side loop round-robins the pulse across every online account on a divided
heartbeat (`FollowAutoPulsePolicy.GetHeartbeat` halves the shared count-drop cadence, then
divides by the online vantage count and floors at 1s), so the fleet notices a change several
times faster than any one VM could. Each run resolves WHICH bound nametag it is following: the
first pulse that verifiably sees one locks onto it for the rest of the run
(`FollowAutoPulsePolicy.PickNametagLockIndex`: highest score wins, bind order breaks ties), and
until a lock exists no nametag can trigger a leave - the loop uses count-drop semantics, exactly
as if nothing were bound. `FollowAutoPulsePolicy.Classify` then reads each sample against the
locked entry: present -> rebaseline; player-count drop with no locked-nametag signal -> leave;
locked nametag missing -> raise a flag. A flag does not leave on its own - the host immediately
forces a check of the SAME locked nametag on a *different* online VM, and only leaves if that
independent vantage also can't see it (the leave reason names both accounts). A second VM that
still sees it makes the first split read transient. If the same account produces two such
independently-contradicted misses, however, the host treats that account as isolated in another
game: it sends Save and Exit only there, removes it from the joined set, and lets the normal
follow check rejoin it to the current game. This targeted resync is capped at once per account
per game so a chronic per-VM fingerprint mismatch cannot cause a leave/rejoin loop. If no other
VM can get a clean read at that instant, the loop keeps waiting rather than leaving on one
screen's word. With only a single VM online there is no independent screen, so that lone
vantage falls back to requiring two back-to-back misses.

Before any of that, each pulse is checked against the sampling bot's own `inGame` field. A bot the
host counts as joined that reports `false` on two consecutive pulses
(`FollowAutoPulsePolicy.NextOutOfGameStreak` / `ShouldResyncOutOfGameVantage`) is out of the game
entirely rather than merely unable to see the leader - the observed cause is a "Connection
Interrupted" that lands one client back at the lobby *after* its join was already confirmed, which
the join flow's own retry can no longer see. The host marks only that account recovery-pending and
lets the normal follow check rejoin it, sending no Save and Exit (it is already at the menus). No
cross-VM confirmation is possible here, since only that client can see its own screen, so the
consecutive-read streak is the guard: a live in-game frame can misclassify as `LobbyOrGame` once
(the lobby thresholds can match outdoor scenery), and `null` reads hold the streak without growing
it. These rejoins are capped at `MaxOutOfGameResyncsPerGame` (3) per account per game so a vantage
that chronically misreads its own live game cannot churn rejoins; on hitting the cap the monitor
names the account and points at its resolution/reference images.

After `menu_play`, `menu_join_game`, `menu_create_game`, and `menu_join_friend`, the VM agent can wait and press `G` to switch to legacy graphics. This is controlled by `ui.toggleLegacyGraphicsAfterEnteringGame` and `ui.legacyGraphicsToggleDelaySeconds` in `vm-agent.config.json`.

`self_update` checks the latest GitHub release for `D2RAgent-win-x64.zip`. If the connected VM agent is older than the latest release, it starts the in-place updater, replies with `updateStarted: true`, and exits after sending the command result so the updater can replace the files and restart the published exe from that release. `D2RHost` only queues this command after the host has completed its own startup update check and the VM agent has authenticated. When update notifications are enabled, the master posts Discord messages for master-local VM-agent results; worker-local update notifications are not forwarded to the master.

For an account owned by the master, Hyper-V commands run directly inside the local D2RHost and do not traverse WebSocket. For a worker-owned account, the master sends the corresponding `vm_*` command over `/node`, and the worker runs it locally.

VM agents and workers respond with the same result envelope:

```json
{
  "type": "command_result",
  "agentId": "d2r-hc-01",
  "commandId": "uuid",
  "ok": true,
  "message": "Launch command sent.",
  "data": {}
}
```

Screenshot responses put a base64 image in `data`:

```json
{
  "type": "command_result",
  "agentId": "d2r-hc-01",
  "commandId": "uuid",
  "ok": true,
  "message": "Screenshot captured.",
  "data": {
    "mimeType": "image/png",
    "base64": "..."
  }
}
```
