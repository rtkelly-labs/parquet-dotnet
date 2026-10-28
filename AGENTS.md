# AGENTS.md

This is the Lake.Parquet fork of Parquet.Net (aloneguid/parquet-dotnet). Almost all of `src/` is
upstream's code; the fork adds files under `eng/lake/`, `docs/changes/`, `docs/vision.md` and its own
workflow, and carries a short list of recorded changes. Read `README.md` and `docs/vision.md` first.

## Rules that are specific to this repository

- Keep upstream's code as upstream wrote it. Do not reformat, rename or restructure upstream files:
  every edit to one is a merge conflict on the next upstream sync. Add new files where possible.
- Every change on top of upstream is one PR with one record in `docs/changes/` (see its README).
- Upstream merges arrive as merge commits through sync PRs (rig `repo-governance fork-sync`). Never
  rebase onto upstream, and never squash a sync PR.
- Source keeps the `Parquet` namespace. `Lake.Parquet` exists only in the CI build (`eng/lake/`).
- Upstream does not accept AI-generated issues or PRs and blocks accounts that send them. Never open
  an issue, PR or comment on aloneguid/parquet-dotnet. Prepare material for a person instead.

## Package manager and commands

.NET via `dotnet` (SDKs 8 and 10). Upstream's build and tests:

```bash
dotnet build src/Parquet.slnx
dotnet test src/Parquet.Test/Parquet.Test.csproj
```

The Lake.Parquet build, as CI runs it, on a copy:

```bash
rsync -a --exclude .git ./ /tmp/lake/
dotnet run eng/lake/RenameNamespace.cs -- /tmp/lake/src Parquet Lake.Parquet
T="$PWD/eng/lake/Lake.Parquet.targets"
dotnet build /tmp/lake/src/Parquet/Parquet.csproj -c Release \
  -p:CustomBeforeMicrosoftCommonTargets="$T" -p:CustomBeforeMicrosoftCommonCrossTargetingTargets="$T"
dotnet run eng/lake/VerifyLakeAssembly.cs -- /tmp/lake/src/Parquet/bin/Release/net10.0/Lake.Parquet.dll
```
