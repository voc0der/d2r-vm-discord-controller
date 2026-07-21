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

Worker heartbeats never serialize either the worker-to-master secret or local VM-agent secrets. `nodeHeartbeatSeconds` controls their interval (clamped to 5-300 seconds by the link). The receiver treats `agentOfflineAfterSeconds` as a minimum and extends it when needed for the authenticated client-advertised interval plus bounded status-collection jitter. Each remote VM's `connected` value is trusted from the worker that evaluated it against that worker's clock and policy. `vmCommandTimeoutSeconds` advertises the worker-owned Hyper-V command budget, capped by the 15-minute worker safety limit.

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

VM commands require `args.vmName`; `vm_snapshot` optionally accepts `args.snapshotName`. They execute through the worker's local PowerShell configuration and `allowedVmNamePrefixes`. System commands queue a power action on the worker machine itself, not on a VM guest.

Commands are not retried across a disconnect, and canceling the master's wait does not recall an already-dispatched command. A lost result after dispatch is an unknown outcome: a VM or system action may already have applied, so callers should reconcile current state before issuing it again.

Discord system commands default to the master node. `node:<node-id>` targets that known node (and may explicitly name the master), while `all:true` targets currently online workers first and the master last. Offline workers are listed and skipped. If a selected worker does not confirm the action, the master is kept online for recovery.

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

`sample_player_count` accepts an optional `{ "fingerprint": "..." }` (the host's session-locked
nametag; the scan short-circuits once that entry is found) and replies with the current in-game
player count plus a per-nametag reading for every bound entry:

```json
{
  "playerCount": 5,
  "lastPartyMemberCount": 4,
  "lastPartyMemberCountUtc": "...",
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
still sees it clears the flag as a transient; if no other VM can get a clean read at that
instant, the loop keeps waiting rather than leaving on one screen's word. With only a single VM
online there is no independent screen, so that lone vantage falls back to requiring two
back-to-back misses.

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
