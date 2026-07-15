# D2R VM Discord Controller

Turn-key Windows ops panel for controlling Diablo II: Resurrected VMs from Discord.

The repo contains:

- `agents/D2RHost`: C# Windows host app. Runs the Discord bot, HTTP health API, VM-agent WebSocket server, SQLite state, and Hyper-V VM controls.
- `agents/D2RAgent`: C# Windows user-session VM agent for Battle.net/D2R launch, screenshots, menu movement, join/create/follow, and save-exit.
- `.github/workflows`: CI and release builds for Windows zip artifacts.

This intentionally stays in the ops lane: launch, kill, restart, VM lifecycle, status, screenshots, and menu navigation to get idle clients into or out of games. It does not automate gameplay.

## Shape

```text
Discord
  -> D2RHost.exe on the Windows Hyper-V host
      -> HTTP health API on :8080
      -> WebSocket /agent listener
      -> SQLite state
      -> local Hyper-V PowerShell commands
          <- D2RAgent.exe inside each logged-in VM
```

The host PC does not need Docker or Node. The VM agents connect outbound to the host over WebSocket, so the VMs only need to reach `ws://HOST_LAN_IP:8080/agent`.

`D2RHost.exe` and `D2RAgent.exe` are the default published executable names. Release builds can rename them with the repo-level Actions variables `HOST_EXE_NAME` and `AGENT_EXE_NAME`; the release zip names stay `D2RHost-win-x64.zip` and `D2RAgent-win-x64.zip` so self-update can keep finding the right assets.

## Updates

The host executable checks the latest GitHub release on startup. If a newer version exists, the host starts an in-place updater, exits, and restarts before it accepts VM-agent connections. After the updated host is running and VM agents authenticate, the host sends each authenticated VM agent a self-update command.

When `guildChannel` is configured and `updateNotificationsEnabled` is true, D2RHost posts Discord notifications when it first comes online, when a host update completes, and when a VM agent starts its self-update.

The VM agent executable can still check for an update when launched from an interactive Windows console. If a newer version exists, the app asks whether to update in place.

When an update is started, the app starts a PowerShell updater, exits, downloads the matching release zip, replaces the files in the exe directory, and restarts the published exe from that release. If the exe name changed, the updater also points the scheduled task at the new exe before starting it.

Versions before `v0.1.3` do not include the updater, so those installs need one manual replacement before future updates can self-apply.

Host-forced satellite updates require a VM agent version that supports the `self_update` command. Older VM agents may need one manual update before the host can force future satellite updates.

Set this environment variable to skip update checks:

```powershell
$env:D2ROPS_DISABLE_UPDATE_CHECK = "true"
```

## Discord Commands

- `/d2r status [account]`
- `/d2r start [account] [all]`
- `/d2r stop account`
- `/d2r quit [account] [all]`
- `/d2r restart-client account`
- `/d2r screenshot account`
- `/d2r remote account`
- `/d2r ready [account]`
- `/d2r lobby account [character-slot]`
- `/d2r play account [character-slot]`
- `/d2r join [account] [all] [auto] [name] [password] [difficulty] [character-slot] [delay] [idle-minutes] [watch]`
- `/d2r create-game [account] [all] [name] [password] [difficulty] [character-slot] [watch]`
- `/d2r follow [account] [all] [character-slot] [friend-row] [bind] [bind-in-game] [auto] [delay] [idle-minutes] [watch]`
- `/d2r save-exit [account] [all]`
- `/d2r template name [password]`
- `/d2r restart`
- `/d2r game set name [password] [difficulty] [notes]`
- `/d2r game show`
- `/d2r game clear`
- `/d2r system sleep`
- `/d2r system shutdown`
- `/d2r system restart`
- `/d2r vm status account`
- `/d2r vm start account`
- `/d2r vm stop account`
- `/d2r vm reboot account`
- `/d2r vm snapshot account [name]`
- `/d2r config show`
- `/d2r config stagger seconds`
- `/d2r config notifications enabled [channel-id] [updates-enabled]`

`/d2r game set` stores the current game details in SQLite. `join` and `create-game` use those stored values when options are omitted.

For folded commands, `all` defaults to true. Pass `all:false account:<x>` for one account. All-client commands skip accounts whose VM agent is offline when the command is queued.

`/d2r create-game` with `all:true` uses the first online configured account by account key as the creator. After that create flow succeeds, the remaining online accounts join the same game with the configured all-client stagger. If you do not pass `character-slot`, the host uses each account's optional `characterSlot` value from `d2r-host.config.json`, then falls back to the VM agent's local default.

Join/create session notifications include `Leave` and `Quit` buttons. `Leave` queues save-exit for all online accounts; `Quit` queues D2R quit for all online accounts.

`/d2r ready` queues the ready flow for every online account. Pass `account:<x>` to warm one account. `/d2r start` with `all:true` uses the same all-account ready flow, so cold-booted clients should land on character select instead of merely starting the D2R process.

`/d2r restart` respawns `D2RHost`. On startup, the host runs its normal self-update check before reconnecting to Discord, so this is the quick way to apply a pushed host update once the command exists in Discord.

`/d2r system sleep`, `/d2r system shutdown`, and `/d2r system restart` publicly announce the requested host power action in the command channel, then run it on the D2RHost machine only. They do not send shutdown/restart commands to the VM clients.

Menu commands that need D2R running, such as `lobby`, `play`, `join`, `create-game`, and `follow`, run `/d2r ready` first when the latest VM status is not already a known character/lobby/game state. The Discord response calls out that extra ready step.

`/d2r follow bind:true account:<x> [friend-row:<n>]` captures whoever is sitting in that account's selected friend row right now (default row 1, no need to type a name - useful from a phone with no keyboard) and distributes that snippet to every online account. `bind:false` clears it everywhere, including any in-game leader bind. A plain `/d2r follow` starts auto-following that bound friend; `auto:false` stops it.

`/d2r follow bind-in-game:<1-8> [account:<x>]` additionally fingerprints the in-game party-bar name at that position, counted left to right across the portraits visible on the vantage account's screen (default vantage: the first online account; the fingerprint is distributed everywhere, so any VM can check it). A character never shows in its own party bar, so count only the other members you see from that vantage; the bar sorts alphabetically, so check the current order rather than assuming join order. Bind it to your own character from inside a game with your bots. **Play multiple alts? Bind each one once (repeat the command while playing each alt) - every bind appends a nametag to the rolodex, and each follow-auto run figures out which alt you're on: the first game locks onto whichever bound nametag it actually sees (highest match score wins, bind order breaks ties) and follows that one for the rest of the run. Until one is spotted, none of them can trigger a leave (count-drop behavior applies), so playing an unbound alt is safe too.** **The bind is verified before it takes effect: your character is the one name every bot can see, so if any other bot reports the captured name missing from its own party bar, that name is that bot's character (you counted the wrong slot) and that nametag is removed everywhere with the account named - your other bound nametags are untouched** - so you can't silently end up following a bot. Once set, follow-auto leaves a game when *that player* is gone instead of whenever the player count drops - in public games a stranger leaving no longer makes every bot leave and immediately rejoin. The check is forceful: as soon as one VM loses sight of you, it immediately makes a different VM look, and the bots leave the moment that second VM agrees (rather than waiting for the next scheduled pulse). `bind-in-game:0` clears all bound nametags and restores the count-drop behavior. Follow-auto posts one live status message for the run, showing the current game number and a Stop button while it is active. When the Stop button ends follow-auto, that message offers `Save and Exit`, `Quit`, and `Sleep` shortcuts for two minutes; `Sleep` quits all online clients successfully before putting the host to sleep. With `watch:true`, follow-auto also writes the diagnostics log for the whole run, across multiple games, including the latest match-score checkpoint. Old low-detail follow-bind snippets must be rebound before follow-auto will click rows. Use `all:false account:<x>` to right-click a manually-specified row once.

## Host Setup

1. Create a Discord application and bot, then invite it to your server with `applications.commands` and `bot` scopes.
2. Download `D2RHost-win-x64.zip` from a release or build it locally.
3. Extract the zip and copy the whole host app folder to `C:\D2ROps`.

```powershell
Expand-Archive .\D2RHost-win-x64.zip -DestinationPath .\D2RHost-win-x64 -Force
New-Item -ItemType Directory -Force -Path C:\D2ROps | Out-Null
Copy-Item .\D2RHost-win-x64\* C:\D2ROps\ -Recurse -Force
```

4. Double-click the host exe (`D2RHost.exe` unless `HOST_EXE_NAME` changed it), or run it from PowerShell:

```powershell
C:\D2ROps\D2RHost.exe
```

If `C:\D2ROps\d2r-host.config.json` does not exist, the host opens a terminal setup flow and writes it. If the JSON already exists, setup is skipped and the host starts normally.

The setup flow asks for:

- Set `discordToken` or set a machine-level `DISCORD_TOKEN` environment variable.
- Optional: set `discordGuildId` for instant guild slash-command registration.
- Add your Discord user ID to `allowedDiscordUserIds`.
- Add one `agents` entry per VM agent. Each `sharedSecret` must match that VM's config.
- Add one `accounts` entry per controlled D2R account, mapping it to the VM agent and Hyper-V VM name.
- Set the default character slot for each account. Discord's `character-slot` option overrides this per command.
- Set `allowedVmNamePrefixes` so the host only operates your D2R VMs.
- Set `startAllDelaySeconds` or the `CLIENT_STAGGER_SECONDS` environment variable to stagger all-client commands.

You can still copy `samples/d2r-host.config.example.json` and edit it by hand if you prefer.

5. Install the host scheduled task from an elevated PowerShell prompt after the config exists:

```powershell
.\install-d2r-host.ps1 -ExePath .\D2RHost.exe
Start-ScheduledTask -TaskName "D2R Host Controller"
```

If the extracted release folder contains only one `.exe`, `install-d2r-host.ps1` can auto-detect it and `-ExePath` is optional.

The default scheduled task runs as `SYSTEM` at startup. If you keep the Discord token in an environment variable instead of the config file, use a machine-level environment variable so the task can see it.

Health checks:

```powershell
Invoke-RestMethod http://localhost:8080/healthz
Invoke-RestMethod http://localhost:8080/agents
```

## VM Agent Setup

For each D2R VM:

1. Download `D2RAgent-win-x64.zip` from a release or build it locally.
2. Extract the zip and copy the whole VM agent app folder to `C:\D2ROps`.

```powershell
Expand-Archive .\D2RAgent-win-x64.zip -DestinationPath .\D2RAgent-win-x64 -Force
New-Item -ItemType Directory -Force -Path C:\D2ROps | Out-Null
Copy-Item .\D2RAgent-win-x64\* C:\D2ROps\ -Recurse -Force
```

3. Double-click the VM agent exe (`D2RAgent.exe` unless `AGENT_EXE_NAME` changed it), or run it from PowerShell:

```powershell
C:\D2ROps\D2RAgent.exe
```

If `C:\D2ROps\vm-agent.config.json` does not exist, the VM agent asks for `agentId`, `controllerUrl`, `sharedSecret`, and Battle.net path, then writes the JSON. Use the agent values printed by the host setup.

If the JSON already exists, the VM agent skips setup and probes the host. If it cannot connect, it asks whether the hostname or port changed, rewrites the JSON, and retries.

The D2R launch default is Battle.net's direct product command:

```json
"battleNetPath": "C:\\Program Files (x86)\\Battle.net\\Battle.net.exe",
"battleNetArgs": "--exec=\"launch OSI\"",
"preferBattleNetExecLaunch": true,
"battleNetExecRetryDelaySeconds": 12
```

That avoids trying to start `D2R.exe` directly, which usually just lands back at Battle.net. Before launching, the VM agent shows the desktop to minimize other windows; if Battle.net is already running, it restores Battle.net before sending the D2R launch command. If Battle.net is not already running, the agent waits `battleNetExecRetryDelaySeconds` seconds and sends the D2R launch command a second time. You can still copy `samples/vm-agent.config.example.json` and edit it by hand if you prefer. UI coordinate/timing tuning remains in that JSON.

By default, a VM agent quits D2R with Alt+F4 after 30 minutes at the character screen without lobby/game interaction. Tune that with `idleQuitEnabled`, `idleQuitMinutes`, and `idleQuitCheckSeconds` in `vm-agent.config.json`.

4. Install the scheduled task from an elevated PowerShell prompt inside the VM after the config exists:

```powershell
.\install-vm-agent.ps1 -ExePath .\D2RAgent.exe
Start-ScheduledTask -TaskName "D2R VM Agent"
```

If the extracted release folder contains only one `.exe`, `install-vm-agent.ps1` can auto-detect it and `-ExePath` is optional.

`install-vm-agent.ps1` also creates (or updates) inbound Windows Firewall rules for Battle.net and D2R scoped to all network profiles, not just Private/Domain. This avoids join failures right after the host wakes from sleep, where Windows briefly reclassifies the network as Public while Network Location Awareness re-identifies it - the first P2P game-join attempt can hit "Connection Interrupted" during that window even though Battle.net login/lobby (plain outbound traffic) already works fine, since the Public firewall profile blocks the inbound side of the game connection. Re-run the script if you change `battleNetPath`/`d2rPath` in the config.

Run the VM agent as a scheduled task at user logon, not as a Windows service. D2R and Battle.net are desktop apps, so the agent needs the logged-in user session for screenshots and input.

The PC can start already logged in with the VM listener loaded. On `/d2r ready`, the agent launches or focuses Battle.net, clicks Play every `ui.readyNudgeMinDelayMs` to `ui.readyNudgeMaxDelayMs` until D2R starts, waits for D2R to expose a focusable window, then keeps nudging intro/title states at the same jittered interval until it visually confirms the character screen by sampling the Play/Lobby button regions. If cold startup is still racing ahead of D2R, raise `d2rStartTimeoutSeconds` or `ui.characterScreenReadyTimeoutSeconds` in `vm-agent.config.json`.

## Menu Automation

The VM agent can drive the flows captured in `docs/runbooks/assets/d2r-ui/`:

- Ready flow: repeated Battle.net Play attempts, jittered intro/title nudges, title splash detection, then Play/Lobby visual confirmation.
- Character screen to Play.
- Character screen to Lobby.
- Lobby Join Game.
- Lobby Create Game.
- Lobby friends drawer right-click Join Game.
- In-game Save and Exit.
- D2R window quit via Alt+F4.

Before menu commands click into the lobby, the agent waits for the character screen or lobby tabs to be visually detectable. For `join` and `create-game`, it retries the final Join/Create button until the active lobby tab disappears or `ui.gameEntryStartTimeoutSeconds` expires. After `play`, `join`, `create-game`, or `follow`, the agent waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G` to switch to legacy graphics for lower idle GPU use. Disable that with `ui.toggleLegacyGraphicsAfterEnteringGame: false` in the VM config.

All-client commands are staggered and skip offline VM agents. Set `CLIENT_STAGGER_SECONDS=30` on the host, or set `startAllDelaySeconds` in `d2r-host.config.json`.

## Build Locally

```bash
dotnet build D2ROps.sln --configuration Release
dotnet publish agents/D2RHost/D2RHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/D2RHost
dotnet publish agents/D2RAgent/D2RAgent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/D2RAgent
```

Override the published executable names with `-p:HostExeName=Whatever` and `-p:AgentExeName=Whatever`, or set `HOST_EXE_NAME` and `AGENT_EXE_NAME`. Values may include or omit `.exe`; the build strips the extension for the assembly name.

Run the host without Discord while testing VM-agent WebSocket connections:

```powershell
$env:DISABLE_DISCORD = "true"
.\D2RHost.exe C:\D2ROps\d2r-host.config.json
```

Client-side D2R menu flow references live in [docs/runbooks](docs/runbooks/README.md).
