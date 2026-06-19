# Agent Protocol

Agents connect to:

```text
ws://controller-host:8080/agent
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

Agents periodically push status:

```json
{
  "type": "status",
  "agentId": "d2r-hc-01",
  "status": {
    "battleNetRunning": true,
    "d2rRunning": false
  }
}
```

The controller sends commands:

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
