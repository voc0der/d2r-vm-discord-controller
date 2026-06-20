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

## Updates

`D2RHost.exe` and `D2RAgent.exe` check the latest GitHub release when launched from an interactive Windows console. If a newer version exists, the app asks whether to update in place.

When accepted, the app starts a PowerShell updater, exits, downloads the matching release zip, replaces the files in the exe directory, and restarts the same exe. Non-interactive runs, such as scheduled tasks, skip the prompt.

Versions before `v0.1.3` do not include the updater, so those installs need one manual replacement before future updates can self-apply.

Set this environment variable to skip update checks:

```powershell
$env:D2ROPS_DISABLE_UPDATE_CHECK = "true"
```

## Discord Commands

- `/d2r status [account]`
- `/d2r start account`
- `/d2r stop account`
- `/d2r restart-client account`
- `/d2r screenshot account`
- `/d2r remote account`
- `/d2r ready account`
- `/d2r lobby account [character-slot]`
- `/d2r play account [character-slot]`
- `/d2r join-game account [name] [password] [difficulty] [character-slot]`
- `/d2r create-game account [name] [password] [difficulty] [character-slot]`
- `/d2r follow account [character-slot] [friend-row]`
- `/d2r save-exit account`
- `/d2r leave account`
- `/d2r join-all [name] [password] [difficulty] [character-slot]`
- `/d2r follow-all [character-slot] [friend-row]`
- `/d2r save-exit-all`
- `/d2r leave-all`
- `/d2r start-all`
- `/d2r health`
- `/game set name [password] [difficulty] [notes]`
- `/game show`
- `/game clear`
- `/vm status account`
- `/vm start account`
- `/vm stop account`
- `/vm reboot account`
- `/vm snapshot account [name]`

`/game set` stores the current game details in SQLite. `join-game`, `create-game`, and `join-all` use those stored values when options are omitted.

## Host Setup

1. Create a Discord application and bot, then invite it to your server with `applications.commands` and `bot` scopes.
2. Download `D2RHost-win-x64.zip` from a release or build it locally.
3. Copy the host app to `C:\D2ROps`.

```powershell
Copy-Item .\D2RHost.exe C:\D2ROps\
```

4. Double-click `D2RHost.exe`, or run it from PowerShell:

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
- Set `allowedVmNamePrefixes` so the host only operates your D2R VMs.
- Set `startAllDelaySeconds` or the `CLIENT_STAGGER_SECONDS` environment variable to stagger all-client commands.

You can still copy `samples/d2r-host.config.example.json` and edit it by hand if you prefer.

5. Install the host scheduled task from an elevated PowerShell prompt after the config exists:

```powershell
.\install-d2r-host.ps1 -ExePath .\D2RHost.exe
Start-ScheduledTask -TaskName "D2R Host Controller"
```

The default scheduled task runs as `SYSTEM` at startup. If you keep the Discord token in an environment variable instead of the config file, use a machine-level environment variable so the task can see it.

Health checks:

```powershell
Invoke-RestMethod http://localhost:8080/healthz
Invoke-RestMethod http://localhost:8080/agents
```

## VM Agent Setup

For each D2R VM:

1. Download `D2RAgent-win-x64.zip` from a release or build it locally.
2. Copy `D2RAgent.exe` to `C:\D2ROps`.
3. Double-click `D2RAgent.exe`, or run it from PowerShell:

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

That avoids trying to start `D2R.exe` directly, which usually just lands back at Battle.net. If Battle.net is not already running, the agent waits `battleNetExecRetryDelaySeconds` seconds and sends the D2R launch command a second time. You can still copy `samples/vm-agent.config.example.json` and edit it by hand if you prefer. UI coordinate/timing tuning remains in that JSON.

4. Install the scheduled task from an elevated PowerShell prompt inside the VM after the config exists:

```powershell
.\install-vm-agent.ps1 -ExePath .\D2RAgent.exe
Start-ScheduledTask -TaskName "D2R VM Agent"
```

Run the VM agent as a scheduled task at user logon, not as a Windows service. D2R and Battle.net are desktop apps, so the agent needs the logged-in user session for screenshots and input.

The PC can start already logged in with the VM listener loaded. On `/d2r ready`, the agent launches or focuses Battle.net, clicks Play when needed, waits for D2R, clicks through intro videos, and lands on the character screen.

## Menu Automation

The VM agent can drive the flows captured in `docs/runbooks/assets/d2r-ui/`:

- Ready flow: Battle.net Play, then repeated clicks through intro videos.
- Character screen to Play.
- Character screen to Lobby.
- Lobby Join Game.
- Lobby Create Game.
- Lobby friends drawer right-click Join Game.
- In-game Save and Exit.

After `play`, `join-game`, `create-game`, or `follow`, the agent waits `ui.legacyGraphicsToggleDelaySeconds` seconds and presses `G` to switch to legacy graphics for lower idle GPU use. Disable that with `ui.toggleLegacyGraphicsAfterEnteringGame: false` in the VM config.

All-client commands are staggered. Set `CLIENT_STAGGER_SECONDS=30` on the host, or set `startAllDelaySeconds` in `d2r-host.config.json`.

## Build Locally

```bash
dotnet build D2ROps.sln --configuration Release
dotnet publish agents/D2RHost/D2RHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/D2RHost
dotnet publish agents/D2RAgent/D2RAgent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/D2RAgent
```

Run the host without Discord while testing VM-agent WebSocket connections:

```powershell
$env:DISABLE_DISCORD = "true"
.\D2RHost.exe C:\D2ROps\d2r-host.config.json
```

Client-side D2R menu flow references live in [docs/runbooks](docs/runbooks/README.md).
