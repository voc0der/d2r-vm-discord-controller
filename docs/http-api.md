# HTTP Command API

`D2RHost` exposes the whole `/d2r` command surface over HTTP so something other than Discord — a script, a home-automation box, a local model — can drive the fleet.

This is the reference. For the setup walkthrough see the [HTTP command API section of the README](../README.md#http-command-api); for the WebSocket protocol the VM agents and worker nodes speak, see [protocol.md](protocol.md).

- **Off by default.** Nothing is mounted as callable until `/d2r config api enabled:true` mints a key.
- **Master-only.** A worker node relays commands for its own VMs but has no view of the fleet, so it could answer for part of the estate while looking like it answered for all of it. On a worker these routes are never mapped and return `404`.
- **One key, full reach.** The key can do everything `/d2r` can do, including VM power and node sleep/shutdown/restart. Treat it as equivalent to Discord access to the controller.

## Base URL

The API shares `httpPort` with the rest of the host (default `8080`), which the managed firewall rule already opens to `windowsFirewall.trustedNetworks` (default `LocalSubnet`).

```text
http://<master-host>:8080
```

There is no TLS in the built-in listener. Put a reverse proxy in front of it if you need one.

## Authentication

Send the key in either header. `X-API-Key` wins if both are present.

```http
X-API-Key: d2rk_...
Authorization: Bearer d2rk_...
```

A bare `Authorization: <key>` with no scheme is also accepted, because it is a common curl slip.

### Managing the key

The key lifecycle lives in Discord, not in the API — an API that could mint its own credentials would not be much of a gate. `config api` is still callable over HTTP (so a caller can close the API), but any call that would mint a key is refused in-band: the new key would come back in the response body, so a leaked key could replace itself and lock out the operator.

| Command | Effect |
|---|---|
| `/d2r config api enabled:true` | Enables the API. Mints a key **only if none exists**, and prints it once. |
| `/d2r config api enabled:true overwrite:true` | Mints a replacement. The previous key stops working immediately. |
| `/d2r config api enabled:false` | Closes the API. The key is **kept**, so re-enabling does not require re-keying callers. |
| `/d2r config show` | Reports whether the API is on and which key id is installed. Never the key. |

Only `SHA-256(key)` is written to `d2r-host.config.json`, as `api.keyHash`. The key itself is never stored and cannot be recovered — if you lose it, mint a replacement. `api.keyId` is a non-secret handle (`d2rk_abc123...`) for logs and `/d2r config show`; it is not a credential and will not authenticate.

Re-running `enabled:true` deliberately does **not** rotate. Rotation locks out whatever is already calling, and that should never be a side effect of checking whether the API is on.

### Failure responses

Two distinct answers, deliberately — an operator debugging their own automation needs to tell "I never enabled this" from "my key is wrong".

`503 Service Unavailable` — the API is disabled, or enabled with no key stored (it fails closed):

```json
{ "ok": false, "error": "The HTTP command API is disabled on this host. Enable it from Discord with /d2r config api enabled:true." }
```

`401 Unauthorized` — the API is on and the presented key is missing or wrong:

```json
{ "ok": false, "error": "A valid API key is required. Send it as `X-API-Key: <key>` or `Authorization: Bearer <key>`." }
```

## Endpoints

All of these require the key.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/commands` | Every command this host accepts, with options, types, required flags and choices |
| `GET` | `/api/dclone` | The live Diablo Clone park as data |
| `POST` | `/api/command` | Run a command, named in the body |
| `POST` | `/api/d2r/{command}` | Run an ungrouped command, named in the path |
| `POST` | `/api/d2r/{group}/{command}` | Run a grouped command (`vm`, `game`, `config`, `system`) |

### `GET /api/commands`

Generated from the same `DiscordSlashCommands.Build()` that registers the Discord commands, so it cannot drift from what the bot offers. This is the endpoint to introspect rather than hard-coding a command list.

```json
{
  "commands": [
    {
      "path": "dclone",
      "group": null,
      "command": "dclone",
      "description": "Park every online bot in its own game and hold it open for a Diablo Clone hunt",
      "options": [
        { "name": "bots", "type": "Integer", "required": false, "description": "How many bots to park, one game each; defaults to every online account", "choices": null },
        { "name": "difficulty", "type": "String", "required": false, "description": "Difficulty for every parked game; defaults to Hell", "choices": ["normal", "nightmare", "hell"] }
      ]
    }
  ]
}
```

### `POST /api/command`

```json
{ "group": null, "command": "dclone", "options": { "bots": 6 } }
```

`group` is `null` (or omitted) for an ungrouped command. `options` may be omitted when the command takes none.

`400` with `{ "ok": false, "error": "command is required." }` if `command` is missing or blank. Note this one response has no `message` field — it is rejected before a command context exists.

### `POST /api/d2r/{command}` and `POST /api/d2r/{group}/{command}`

The same thing with the command in the path and the **options object itself** as the body:

```bash
curl -s -X POST "$BASE/api/d2r/vm/start" \
  -H "X-API-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"account":"hc1"}'
```

### `GET /api/dclone`

Reads back a running park as data instead of scraping the Discord monitor. This is also what makes a **monitor-less** park usable: on a host with no notification channel, `dclone` still runs and this is the only place the minted credentials can be read.

```json
{
  "running": true,
  "startedUtc": "2026-09-10T18:04:11.2210000+00:00",
  "difficulty": "hell",
  "parked": 5,
  "total": 6,
  "games": [
    { "accountKey": "hc1", "gameName": "ewk52we", "password": "q", "state": "Parked", "detail": "holding the game open", "reparks": 0 },
    { "accountKey": "hc2", "gameName": null, "password": null, "state": "Reparking", "detail": "left its game; building a replacement", "reparks": 1 }
  ]
}
```

`state` is one of `Preparing`, `Parked`, `Reparking`, `Failed`, `Offline`. `gameName`/`password` are `null` for any slot not currently holding a game. With no park ever started: `{"running": false, "startedUtc": null, "difficulty": null, "parked": 0, "total": 0, "games": []}`.

## The response envelope

Every command answers with the same shape:

```json
{
  "ok": true,
  "message": "Queued 6 online ready command(s) with 20s stagger.",
  "details": ["ready failed for hc4: its node or VM agent went offline before dispatch."],
  "file": null,
  "error": null
}
```

| Field | Meaning |
|---|---|
| `ok` | **The command ran.** Not that it got what you wanted — see below. |
| `message` | What Discord would have shown as the reply. The last one written wins, matching Discord's defer-then-edit flow. |
| `details` | The follow-ups a fan-out posts per account, in order. Usually empty. |
| `file` | `{ "fileName", "contentType", "base64" }` for `screenshot`; `null` otherwise. |
| `error` | `null` on success; otherwise `"UnknownCommand"`, `"Timeout"`, or the .NET exception type name. |

### `ok` is not "it worked"

**`ok:true` means the handler ran and answered. It does not mean the fleet did what you asked.** Every ordinary refusal is in-band, exactly as it is in Discord — read `message`:

- `"No online accounts are available to park."`
- `"A dclone park is already running. Stop it with /d2r dclone stop:true first."`
- `"follow-auto is running and owns the same VMs. Stop it with /d2r follow auto:false first."`
- `"follow-auto posts a live monitor message and cannot run without a Discord channel. ..."`

A caller that only checks the status code will read all of those as success. Check `message` too.

### Status codes

| Code | When |
|---|---|
| `200` | `ok:true` |
| `400` | `ok:false` — unknown command, unknown option, a handler that threw (including a single-client command whose agent call threw), **and timeouts** |
| `401` | Missing or wrong key |
| `404` | Route not mounted — this node is a worker, not the master |
| `503` | API disabled, or enabled with no stored key |

The non-ok mapping is coarse: a `Timeout` is a `400` even though nothing about the request was malformed. Branch on `error`, not on the status code, when you need to tell those apart. Likewise, an unknown *option* currently reports `error: "UnknownCommand"` alongside a genuinely unknown *command*; the `message` distinguishes them.

## How options map

Option names are exactly the Discord ones, hyphens and all — `character-slot`, `idle-minutes`, `bind-in-game`, `channel-id`, `updates-enabled`. Matching is case-insensitive.

| Catalog type | Send |
|---|---|
| `String` | A JSON string |
| `Integer` | A JSON number (a numeric string also works) |
| `Boolean` | JSON `true`/`false` (a `"true"`/`"false"` string also works) |

An option the command does not have is **rejected**, not ignored — a typo would otherwise run the command with a default you did not intend, which is exactly the failure an automated caller cannot see:

```json
{ "ok": false, "message": "`dclone` does not have option(s) bot; it accepts: bots, difficulty, stop, watch, metric.", "details": [], "file": null, "error": "UnknownCommand" }
```

The `metric` option exists on every command (it appends host/VM telemetry in Discord) and is accepted but pointless over HTTP.

## Timing

Commands run **synchronously**. A request blocks until the work finishes, so:

- `ready` can legitimately take minutes — its own budget is 420s per client.
- A fan-out returns **every account's result** in `details` rather than just "queued".
- `screenshot` returns the image inline rather than as a Discord attachment.

Past **600 seconds** the response returns with `ok:false` and `error:"Timeout"`. The command is **not cancelled** — a `ready` pass or a game create is not something to abandon halfway — so poll `/api/status`-style reads or `/api/dclone` for the eventual outcome rather than retrying blind.

## Commands needing a Discord channel

Two commands refuse over HTTP when the host has no notification channel configured, because their entire output is a live message with controls on it and a run without one cannot be steered:

- `follow` with `auto: true`
- `join` with `auto: true`

They answer `ok:true` with a `message` naming the fix (`/d2r config notifications enabled:true channel-id:<id>`).

`dclone` deliberately does **not** refuse. With no channel it runs monitor-less, and `GET /api/dclone` is where you read the game names and passwords.

## Command catalog

Ungrouped commands are called as `{"command": "<name>"}`; grouped ones as `{"group": "vm", "command": "start"}` or `POST /api/d2r/vm/start`. `!` marks a required option. `metric` is omitted below — it is on every command.

### Clients

| Command | Options | Description |
|---|---|---|
| `status` | `account` | Controller health plus one or all account client statuses |
| `start` | `account`, `all` | Launch one account, or ready all accounts when `all` is true |
| `stop` | `!account` | Kill the D2R process for an account |
| `quit` | `account`, `all` | Focus D2R and close it with Alt+F4 |
| `restart-client` | `!account` | Restart the D2R process for an account |
| `ready` | `account` | Launch D2R and skip intros for one account, or all online accounts when omitted |
| `screenshot` | `!account` | Capture the VM's primary screen — returns `file` |
| `remote` | `!account` | Show the configured remote-control URL for an account VM |
| `save-exit` | `account`, `all` | Open the in-game menu and click Save and Exit |

### Games

| Command | Options | Description |
|---|---|---|
| `lobby` | `!account`, `character-slot` | Select character and open Lobby |
| `play` | `!account`, `character-slot` | Select character and click Play |
| `join` | `account`, `all`, `auto`, `name`, `password`, `difficulty` `{normal\|nightmare\|hell}`, `character-slot`, `delay`, `idle-minutes`, `watch` | Join a game, or auto-join template games when `auto` is true |
| `create-game` | `account`, `all`, `name`, `password`, `difficulty`, `character-slot`, `watch` | Create one game, or create and join across all accounts |
| `follow` | `account`, `all`, `character-slot`, `friend-row`, `bind`, `bind-in-game`, `auto`, `bots`, `delay`, `idle-minutes`, `watch` | Follow the bound friend, bind a friend, or join by visible row |
| `dclone` | `bots`, `difficulty`, `stop`, `watch` | Park every online bot in its own game for a Diablo Clone hunt |
| `template` | `!name`, `password` | Set the create/join auto-naming template |
| `game set` | `!name`, `password`, `difficulty`, `notes` | Store the current game name/password for clients to join |
| `game show` | — | Show the stored game details |
| `game clear` | — | Clear the stored game details |

### Virtual machines

| Command | Options | Description |
|---|---|---|
| `vm status` | `!account` | Hyper-V status for an account VM |
| `vm start` | `!account` | Start an account VM |
| `vm stop` | `!account` | Graceful shutdown via guest integration services |
| `vm turnoff` | `!account` | Cut power without asking the guest — unsaved work is lost |
| `vm reboot` | `!account` | Restart an account VM |
| `vm snapshot` | `!account`, `name` | Create a Hyper-V checkpoint |

### Host and configuration

| Command | Options | Description |
|---|---|---|
| `restart` | — | Respawn D2RHost so startup self-update can apply |
| `system sleep` | `node`, `all` | Sleep every online node, or one via `node`/`all:false` |
| `system shutdown` | `node`, `all` | Shut down one node, or every online node |
| `system restart` | `node`, `all` | Restart one node, or every online node |
| `config show` | — | Show runtime controller config |
| `config stagger` | `!seconds` | Persist all-client stagger seconds and **restart the host** |
| `config notifications` | `!enabled`, `channel-id`, `updates-enabled` | Persist notification settings and **restart the host** |
| `config api` | `!enabled`, `overwrite` | Turn this API on/off and mint its key — minting is refused over HTTP, so the key is only ever shown in Discord |

`config stagger`, `config notifications`, `restart`, and `system restart`/`shutdown`/`sleep` all tear down or suspend the process answering your request. Expect the connection to drop rather than a tidy response.

## Unauthenticated endpoints

These predate the API, stay open, and are unchanged by it — anything already pointed at them keeps working. They are read-only and exist on workers too.

| Method | Route | Returns |
|---|---|---|
| `GET` | `/healthz` | Node mode/id, agent counts, firewall status |
| `GET` | `/agents` | Per-agent connectivity snapshot |
| `GET` | `/nodes` | Fleet node list (master) or this node's identity (worker) |
| `GET` | `/config/accounts` | Configured account keys |

`/agent` and `/node` are WebSocket-only and authenticate with their own shared secrets; see [protocol.md](protocol.md).

## Worked examples

```bash
KEY=d2rk_...
BASE=http://d2rhost.lan:8080

# What can this host do?
curl -s -H "X-API-Key: $KEY" $BASE/api/commands | jq -r '.commands[].path'

# Warm every online client, and see each one's result.
curl -s -H "X-API-Key: $KEY" -X POST $BASE/api/d2r/ready \
  -H 'Content-Type: application/json' -d '{}' | jq '{ok, message, details}'

# Park six bots for a Clone hunt, then read the games back.
curl -s -H "X-API-Key: $KEY" -X POST $BASE/api/command \
  -H 'Content-Type: application/json' \
  -d '{"command":"dclone","options":{"bots":6}}' | jq -r .message
curl -s -H "X-API-Key: $KEY" $BASE/api/dclone \
  | jq -r '.games[] | select(.gameName != null) | "\(.gameName) / \(.password)  <- \(.accountKey)"'

# Stop the park.
curl -s -H "X-API-Key: $KEY" -X POST $BASE/api/command \
  -H 'Content-Type: application/json' -d '{"command":"dclone","options":{"stop":true}}'

# Grab a screenshot and write the PNG out.
curl -s -H "X-API-Key: $KEY" -X POST $BASE/api/d2r/screenshot \
  -H 'Content-Type: application/json' -d '{"account":"hc1"}' \
  | jq -r '.file.base64' | base64 -d > hc1.png

# One VM, hard power cut (unsaved guest work is lost).
curl -s -H "X-API-Key: $KEY" -X POST $BASE/api/d2r/vm/turnoff \
  -H 'Content-Type: application/json' -d '{"account":"hc4"}'
```
