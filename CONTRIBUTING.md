# Contributing

This repo is a C# Windows app split into a host controller and VM satellites. Keep changes small, testable, and easy to operate on real Windows machines.

## Development Setup

Use .NET 8.

```bash
dotnet restore D2ROps.sln
dotnet build D2ROps.sln --configuration Release
```

Publish the same Windows artifacts CI builds:

```bash
dotnet publish agents/D2RHost/D2RHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/check/D2RHost
dotnet publish agents/D2RAgent/D2RAgent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/check/D2RAgent
```

Before committing, run:

```bash
git status --short
dotnet build D2ROps.sln --configuration Release
```

For release-sensitive changes, also run both `dotnet publish` commands above.

## Repo Layout

- `agents/D2RHost`: Discord bot, HTTP/WebSocket host, SQLite state, and local Hyper-V control.
- `agents/D2RAgent`: logged-in VM desktop agent for Battle.net/D2R process and UI flows.
- `agents/AgentCommon`: shared config, protocol, and WebSocket client code.
- `samples`: JSON examples used by release artifacts and docs.
- `scripts`: Windows scheduled-task installers.
- `docs/runbooks`: D2R UI state and flow references.

## Commit Format

Use Conventional Commits:

```text
type(scope): short imperative summary
```

Common types:

- `feat`: user-facing feature
- `fix`: bug fix
- `docs`: documentation-only change
- `ci`: workflow or automation change
- `build`: build system or dependency change
- `refactor`: code change without behavior change
- `test`: tests or test helpers
- `chore`: maintenance

Examples:

```text
feat(host): add first-run config wizard
fix(agent): retry host probe after port update
docs: describe VM menu flow setup
ci(release): generate contributor changelog
```

Set the optional local commit template:

```bash
git config commit.template .gitmessage
```

## Pull Requests

PR titles should use the same Conventional Commit style because release notes are generated from git history and PR titles.

Every PR should include:

- Summary of what changed.
- Validation commands run.
- Any config, migration, release, or manual test notes.
- Screenshots only when UI/docs screenshots changed.

Avoid committing private Battle.net tags, tokens, local config, databases, release artifacts, or screenshots with personal identifiers.

## Release Process

Only maintainers cut releases.

Releases are created from SemVer tags:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The release workflow builds:

- `D2RHost-win-x64.zip`
- `D2RAgent-win-x64.zip`

The workflow also generates release notes with:

- Compare link to the previous tag.
- Changelog grouped by commit type when possible.
- Contributor list from commits in the release range.

## Local Git Hygiene

Useful checks before pushing:

```bash
git diff --check
git status --short
git log --oneline -5
```

Keep generated outputs under ignored paths such as `artifacts/`, `bin/`, and `obj/`.
