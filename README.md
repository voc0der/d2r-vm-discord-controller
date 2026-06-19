# D2R VM Discord Controller

Turn-key ops panel for controlling Diablo II: Resurrected Windows VMs from Discord.

The repo contains:

- `apps/controller`: Node.js/TypeScript Discord bot, HTTP health API, WebSocket server, SQLite state.
- `agents/D2RAgent`: C# Windows user-session agent for Battle.net/D2R launch, status, restart, and screenshots.
- `agents/HyperVAgent`: C# Windows host agent for Hyper-V VM start/stop/reboot/checkpoint/status.
- `.github/workflows`: CI plus release publishing to GHCR, Docker Hub, and GitHub release zips.

This intentionally stays in the ops lane: launch, kill, restart, VM lifecycle, status, screenshots. It does not send gameplay input or automate play.

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

## Local Controller Setup

1. Create a Discord application and bot, then invite it to your server with `applications.commands` and `bot` scopes.
2. Copy and edit the sample files:

```bash
cp samples/controller.env.example .env
mkdir -p config data
cp samples/controller.config.example.json config/controller.config.json
```

3. Put your Discord token/client/guild IDs in `.env`.
4. Put your Discord user ID, accounts, VM agent IDs, host agent ID, and shared secrets in `config/controller.config.json`.
5. Optional: set `CLIENT_STAGGER_SECONDS` in `.env` to stagger `*-all` client commands from Docker.
6. Register slash commands:

```bash
npm install
npm run register:commands
```

7. Start the controller:

```bash
docker compose up -d --build
```

Health checks:

```bash
curl http://localhost:8080/healthz
curl http://localhost:8080/agents
```

## Windows Agent Setup

Build locally:

```bash
dotnet publish agents/D2RAgent/D2RAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/D2RAgent
dotnet publish agents/HyperVAgent/HyperVAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/HyperVAgent
```

Or use the zips attached to a tagged GitHub release.

For each D2R VM:

1. Copy `D2RAgent.exe` to `C:\D2ROps`.
2. Copy `samples/vm-agent.config.example.json` to `C:\D2ROps\vm-agent.config.json`.
3. Edit `agentId`, `controllerUrl`, `sharedSecret`, and paths.
4. Install the scheduled task from an elevated PowerShell prompt inside the VM:

```powershell
.\install-vm-agent.ps1 -ExePath .\D2RAgent.exe
Start-ScheduledTask -TaskName "D2R VM Agent"
```

Run the VM agent as a scheduled task at user logon, not as a Windows service. D2R and Battle.net are desktop apps, so the agent needs the logged-in user session for screenshots and process launch behavior.

For the Hyper-V host:

1. Copy `HyperVAgent.exe` to `C:\D2ROps`.
2. Copy `samples/hyperv-agent.config.example.json` to `C:\D2ROps\hyperv-agent.config.json`.
3. Edit `agentId`, `controllerUrl`, `sharedSecret`, and `allowedVmNamePrefixes`.
4. Install the startup scheduled task from elevated PowerShell:

```powershell
.\install-hyperv-agent.ps1 -ExePath .\HyperVAgent.exe
Start-ScheduledTask -TaskName "D2R Hyper-V Agent"
```

## Release Setup

GitHub Actions expects:

- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

GHCR uses the built-in `GITHUB_TOKEN`.

Create a release by pushing a SemVer tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The release workflow publishes:

- `ghcr.io/<owner>/d2r-vm-discord-controller`
- `docker.io/<dockerhub-user>/d2r-vm-discord-controller`
- `D2RAgent-win-x64.zip`
- `HyperVAgent-win-x64.zip`

## Development

```bash
npm install
npm run check
npm test
npm run build
dotnet build D2ROps.sln
```

Run the controller without Discord while testing WebSocket agents:

```bash
DISABLE_DISCORD=true CONFIG_PATH=./config/controller.config.json npm run dev:controller
```

Client-side D2R menu flow references live in [docs/runbooks](docs/runbooks/README.md).
