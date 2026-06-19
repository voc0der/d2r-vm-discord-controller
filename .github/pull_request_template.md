## Summary

- 

## Validation

- [ ] `dotnet build D2ROps.sln --configuration Release`
- [ ] `dotnet publish agents/D2RHost/D2RHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/check/D2RHost`
- [ ] `dotnet publish agents/D2RAgent/D2RAgent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/check/D2RAgent`
- [ ] Not applicable; docs or metadata only

## Notes

- Config changes:
- Release notes:
- Manual testing:

## Checklist

- [ ] PR title uses Conventional Commit format, for example `feat(host): add setup wizard`.
- [ ] No secrets, local configs, databases, or private Battle.net tags are committed.
- [ ] Docs or samples were updated when behavior changed.
