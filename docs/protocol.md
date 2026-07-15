# Agent Protocol

VM agents connect to the host app:

```text
ws://d2r-host:8080/agent
```

The first message must be a hello:

```json
{
  "type": "hello",
  "agentId": "d2r-hc-01",
  "agentKind": "vm",
  "sharedSecret": "replace_me",
  "version": "0.1.0",
  "hostName": "D2R-HC-01"
}
```

For startup connection tests, an agent may include `"probeOnly": true`. `D2RHost` authenticates it, replies with `hello_ack`, and closes the probe socket without replacing any already-connected VM agent:

```json
{
  "type": "hello_ack",
  "agentId": "d2r-hc-01",
  "ok": true
}
```

Agents periodically push status:

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

`D2RHost` sends commands:

```json
{
  "type": "command",
  "commandId": "uuid",
  "command": "launch_d2r",
  "args": {
    "accountKey": "hc1",
    "vmName": "d2r-hc-01"
  }
}
```

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

`self_update` checks the latest GitHub release for `D2RAgent-win-x64.zip`. If the connected VM agent is older than the latest release, it starts the in-place updater, replies with `updateStarted: true`, and exits after sending the command result so the updater can replace the files and restart the published exe from that release. `D2RHost` only queues this command after the host has completed its own startup update check and the VM agent has authenticated. When update notifications are enabled, the host posts a Discord message for VM-agent updates from this command result.

Hyper-V commands do not use this protocol anymore. They run locally inside `D2RHost` on the Windows Hyper-V host.

Agents respond:

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
