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
menu_join_game
menu_create_game
menu_join_friend
menu_save_exit
self_update
quit_d2r
```

Game-oriented menu commands accept optional args such as:

```json
{
  "characterSlot": 1,
  "friendRow": 1,
  "gameName": "baal-001",
  "password": "pw",
  "difficulty": "hell"
}
```

After `menu_play`, `menu_join_game`, `menu_create_game`, and `menu_join_friend`, the VM agent can wait and press `G` to switch to legacy graphics. This is controlled by `ui.toggleLegacyGraphicsAfterEnteringGame` and `ui.legacyGraphicsToggleDelaySeconds` in `vm-agent.config.json`.

`self_update` checks the latest GitHub release for `D2RAgent-win-x64.zip`. If the connected VM agent is older than the latest release, it starts the in-place updater, replies with `updateStarted: true`, and exits after sending the command result so the updater can replace and restart the exe. `D2RHost` only queues this command after the host has completed its own startup update check and the VM agent has authenticated.

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
