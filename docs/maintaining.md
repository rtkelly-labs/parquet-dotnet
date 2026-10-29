# Maintaining the fork

How this repository is laid out, and how to keep every branch on top of upstream with as little
manual work as possible. The rules themselves (what a change is, the record format) are in
[changes/README.md](changes/README.md); this page is the operations guide.

## The structure

Everything is built from one line of history: upstream's `master`, kept exactly as upstream wrote it.

```text
upstream/master   A ─ B ─ C ─ U                        aloneguid/parquet-dotnet, never rewritten
                              │
master                        └─ 0001 ─ 0002 ─ … ─ 0025      the stack: one commit per change
                                         │
lake/change/0017-physical-values   U ─ 0002 ─ 0008 ─ 0017   generated: upstream + a change + what it needs
your-branch                                     └─ … ─ 0025 ─ x ─ y   your own commits, on top of the stack
```

### Remotes

| Remote | Repository | Used for |
|---|---|---|
| `origin` | `rtkelly-labs/parquet-dotnet` | the fork: everything below lives here |
| `upstream` | `aloneguid/parquet-dotnet` | read only; fetched, never pushed to |

```bash
git remote add upstream https://github.com/aloneguid/parquet-dotnet.git
git fetch upstream && git fetch origin --tags
```

Keep the GitHub fork relationship: it is what lets a person open a pull request upstream from a
`lake/change/*` branch.

### Refs

| Ref | What it is | Who moves it | How |
|---|---|---|---|
| `upstream/master` | upstream's history | upstream | never touched here |
| `master` | upstream + the stack (`lake(0001)` … `lake(NNNN)`) | squash-merged PRs (new changes, fixups); the sync (rebases) | fast-forward for PRs; `--force-with-lease` for rebases, always after tagging the old head |
| `lake/change/NNNN-name` | upstream + one change + its `depends_on`, without records | the sync (`lake-stack branches`) | regenerated; never commit to one |
| `lake/next` | a rebased stack waiting for its build | the sync | deleted once `master` moves |
| `lake/stack/<date>-<sha>` (tags) | every head `master` moved away from | the sync | created, never moved or deleted |
| `lake-v<version>` (tags) | releases | the `lake.yml` publish job | created, never moved |
| any other branch | work in progress, built on the stack | you | `lake-stack rewire` after each sync |

Because `master` is rewritten when it is rebased, **pin a tag, never a commit of `master`**
(Lake.NET's submodule, a consumer's build). Tags keep every published and every previous stack
reachable for good, so "rebased continually" never loses anything.

### Files

| Path | Belongs to | Purpose |
|---|---|---|
| `src/`, `docs/README.md`, everything else of upstream's | upstream, edited only by `lake(0002+)` | the library |
| `README.md`, `AGENTS.md`, `AI_POLICY.md`, `docs/vision.md`, `docs/maintaining.md` | `lake(0001)` | the fork's front page, rules for agents, reasoning, this guide |
| `docs/changes/README.md`, `docs/changes/upstreaming.md` | `lake(0001)` | the stack's rules; how to take a change upstream |
| `docs/changes/NNNN-name.md` | `lake(NNNN)` | one record per change, in the change's own commit |
| `eng/lake/Lake.Stack/`, `eng/lake/Lake.Stack.Tests/` | `lake(0001)` | the stack tool and its tests (below) |
| `eng/lake/RenameNamespace.cs`, `Lake.Parquet.targets`, `VerifyLakeAssembly.cs`, `PACKAGE_README.md` | `lake(0001)` | the build-time rename to `Lake.Parquet` |
| `.github/workflows/lake.yml`, `lake-sync.yml` | `lake(0001)` | the fork's CI and the daily rebase |

None of `lake(0001)`'s files exist upstream, so the bottom of the stack never conflicts.

## The tool: `eng/lake/Lake.Stack`

A small C# console app, with its tests in `eng/lake/Lake.Stack.Tests`. Every step of the process, by
hand or in CI, goes through it, so the rules live in one place and are tested on real throwaway
repositories (an upstream, a bare origin and a fork). Run it from the repository's root; the rest of
this page writes `lake-stack` for it:

```bash
alias lake-stack='dotnet run --project eng/lake/Lake.Stack --configuration Release --'
lake-stack --help                     # every command and option
dotnet test eng/lake/Lake.Stack.Tests # its tests (CI runs them before using it)
```

| Command | What it does | Moves refs? |
|---|---|---|
| `check` | every commit above upstream is `lake(NNNN)` (or a pending `fixup!`), one per change, with a valid record in that commit; no merges; `depends_on` only names changes below; an addition edits no upstream file | no |
| `check-pr --title <t> --base <sha>` | a pull request's title names its change (or is a fixup), and a new change brings its record | no |
| `status` | each change, its kind, status and how many code files it touches | no |
| `sync` | rebases the checked-out stack onto upstream, folding in fixups; reports the range-diff, the changes upstream took whole (dropped) and those left with only a record; `--publish origin` pushes a changed stack to `lake/next` | the current branch (and `lake/next`) |
| `branches` | regenerates every `lake/change/*`; `--push origin` force-pushes them and deletes those of retired changes | `lake/change/*` |
| `rewire [--apply] (<branch>… \| --all)` | moves each branch's own commits onto the current stack; a dry run without `--apply` | the named branches |
| `promote --from <old> --to <new>` | refuses a new stack that fails `check`, tags the old head `lake/stack/<date>-<sha>`, and moves the remote's `master` with a lease on the old head | the remote's `master`, a tag, `lake/next` |

`--upstream <ref>` (or `LAKE_UPSTREAM`; default `upstream/master`) sets the upstream every command
works against, and `--repo <path>` the repository. Exit codes: 0 done, 1 something to fix (a broken
rule, a conflict, a branch that would not build), 2 it could not run (a dirty tree, a bad argument).

All of it is plain git underneath: `rebase --autosquash`, `cherry-pick`, `merge-base`. `rerere` is
switched on, so a conflict resolved once is resolved again automatically on the next rebase. Change
branches are rebuilt with each source commit's own author, committer and dates, so an unchanged change
gives the same commit and the daily push of it is a no-op.

The code is laid out by command: `Commands/SyncCommand.cs`, `BranchesCommand.cs`, `RewireCommand.cs`,
`PromoteCommand.cs`, `CheckCommand.cs`, `CheckPrCommand.cs` and `StatusCommand.cs`, over
`StackModel.cs` (the commits above upstream and their records), `ChangeRecord.cs` (the record format)
and `Git.cs` (git, run with an argument list, never through a shell). A change to how the process
works is a change there, with a test, as a `fixup! lake(0001): …`.

## Keeping everything on top of upstream

Three layers, from fully automatic to by hand.

### 1. Automatic: the daily sync

[`lake-sync.yml`](../.github/workflows/lake-sync.yml) runs every day at 04:17 UTC, and on demand
(Actions → Lake.Parquet sync → Run workflow; the `onto` input takes an upstream branch or tag, for
example a release tag to pin Lake.Parquet to a release):

1. `lake-stack sync` rebases `master` onto upstream's `master`, folding in fixups, and pushes the
   result to `lake/next`;
2. the whole `lake.yml` build runs on it (stack check, rename,
   build, every test on .NET 8 and 10, pack);
3. only if that passes: `lake-stack promote` tags the old head `lake/stack/<date>-<sha>` and moves
   `master` to the new head with a lease on the old one, and `lake-stack branches --push origin`
   regenerates every `lake/change/*` branch (and deletes those of retired changes);
4. otherwise `master` stays where it is and an issue labelled `lake-sync` reports the conflict or the
   failing build.

`--force-with-lease` means a pull request merged while the sync ran is never lost: the push is
refused, and the next run picks the new commit up.

Setup, once: a `LAKE_SYNC_TOKEN` secret (a GitHub App token or fine-grained PAT with `contents:
write`) that master's branch rules let force-push (a bypass actor), and tag creation allowed for it.
Without it the workflow falls back to `GITHUB_TOKEN`, which can push `lake/next` and report, but not
move a protected `master`.

### 2. Semi-automatic: your branches, after a sync

The sync moves `master` and the change branches. Any other branch (a change in progress, an
experiment, an old branch from before the stack) is still built on the previous stack. One command
puts them all back on top:

```bash
git fetch origin --prune --tags && git fetch upstream
git switch master && git reset --hard origin/master    # the new stack (only if you have no local work on master)
git switch -c <branch> origin/<branch>                  # for each remote branch you want rewired, if not already local
lake-stack rewire --all                                 # dry run: what each branch would move
lake-stack rewire --apply --all                         # do it; stops at the first conflict
git push --force-with-lease origin <branch>             # for each branch it moved (it prints the command)
```

How it finds a branch's own commits: it takes the newest point where the branch meets `master`, any
`lake/stack/*` or `lake-v*` tag, or upstream, and moves only the commits after it, with
`git rebase --onto master <that point> <branch>`. So it works the same for:

- a branch cut from an older stack: only your commits move, not the old copies of the stack's;
- a branch cut from upstream before the stack existed (such as `feature/variant-encoding`): its
  commits move onto the stack, and then build against every change;
- a branch already on the current stack: left alone.

It needs the `lake/stack/*` tags locally (`git fetch origin --tags`); without them a branch cut from
an older stack falls back to upstream as its fork point and replays the old stack's commits too
(which then conflict or duplicate). If that happens, `git rebase --abort` and fetch the tags.

On a conflict: resolve it, `git add`, `git rebase --continue`, then run `rewire --apply --all` again;
branches already on the stack are skipped.

To turn a rewired branch into a change: squash it into one commit titled `lake(NNNN): title` with
its record, and open a pull request (a fix to an existing change: `fixup! lake(NNNN): title`).

### 3. By hand: when the sync stops

An issue labelled `lake-sync` means upstream changed code that one of the changes also changes.

```bash
git fetch upstream && git switch master && git reset --hard origin/master
lake-stack sync                               # stops at the conflicting change's commit
# resolve in that commit only, keeping both intents; the change's own tests say whether it is right
git add -A && git rebase --continue           # repeat until done; rerere records every resolution
lake-stack check
dotnet test src/Parquet.Test/Parquet.Test.csproj
git push --force origin HEAD:lake/next              # then build and promote it, below
```

Don't wait for the next scheduled sync: it runs on a fresh machine without your `rerere` records,
so it would stop at the same conflict. Promote the resolution yourself once `lake.yml` is green on
`lake/next` (Actions → Lake.Parquet → Run workflow, on `lake/next`):

```bash
git fetch origin
next=$(git rev-parse origin/lake/next)
lake-stack promote --from origin/master --to "$next"   # checks it, tags the old head, moves master
lake-stack branches --head "$next" --push origin
```

From then on the daily sync starts from the resolved stack.

How to resolve, by case:

- **Upstream changed code a change also changes**: keep both intents.
- **Upstream took the change** (ours, after review, possibly rewritten): take upstream's code, drop the
  change's commit (`git rebase -i`), keep any test of ours that still adds something as a new change.
  When upstream took it exactly, the sync already dropped it and says so.
- **Upstream fixed the same bug differently**: take upstream's fix, keep our test; if it still fails,
  that difference is a bug to report upstream (by hand) and to keep here meanwhile.

## Editing the stack itself

| Task | How |
|---|---|
| New change | PR from a branch on `master`, one commit `lake(NNNN): title` with `docs/changes/NNNN-name.md`, squash-merged with that title |
| Fix a change | PR with a `fixup! lake(NNNN): title` commit (`git commit --fixup=<its commit>`), squash-merged with that title; the next sync folds it in |
| Fix the fork's tooling or docs | the same, as `fixup! lake(0001): …` |
| Reword, reorder or split changes | `git rebase -i $(git merge-base master upstream/master)`, then land it as in "By hand" above |
| Retire a change | drop its commit (`git rebase -i`) and land it the same way; its record goes with it |
| Release | Actions → Lake.Parquet → Run workflow on `master` with `publish`; tags `lake-v<version>` |

Pending fixups are fine on `master` between syncs: `check` reports them, the build includes them, and
the next sync folds each into its change.

## Recovering

Nothing the sync replaces is lost. To see or restore an earlier stack:

```bash
git tag --list 'lake/stack/*' --sort=-creatordate | head      # previous heads, newest first
t=lake/stack/<tag>                                               # what changed since:
git range-diff $(git merge-base $t upstream/master)..$t $(git merge-base master upstream/master)..master
git push --force-with-lease origin lake/stack/<tag>:master     # roll back (an admin; tag the current head first)
```
