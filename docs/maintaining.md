# Maintaining the fork

How this repository is laid out, how to move it onto a newer upstream when you decide to, and how
to bring every other branch along with one command. The rules themselves (what a change is, the record format) are in
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
| `master` | upstream + the stack (`lake(0001)` … `lake(NNNN)`) | squash-merged PRs (new changes, fixups); you, with `lake-stack rebaseline --push` | fast-forward for PRs; `--force-with-lease` for a rebaseline, always after tagging the old head |
| `lake/change/NNNN-name` | upstream + one change + its `depends_on`, without records | `lake-stack rebaseline --push` (through `lake-stack branches`) | regenerated; never commit to one |
| `lake/stack/<date>-<sha>` (tags) | every head `master` moved away from | `lake-stack rebaseline --push` | created, never moved or deleted |
| `lake-v<version>` (tags) | releases | the `lake.yml` publish job | created, never moved |
| any other branch | work in progress, built on the stack | you | `lake-stack rewire` after each rebaseline |

`master` only ever moves to a newer upstream when you run a rebaseline: nothing does it on a
schedule, because upstream's changes can break the changes on top and each move needs a person to
look at it. Between rebaselines, `master` only moves forward (pull requests).

Because a rebaseline rewrites `master`, **pin a tag or a commit that a tag keeps**, never a moving
branch head you expect to stay put (Lake.NET's submodule, a consumer's build). Every head a
rebaseline moves away from is tagged first, so a pinned commit never becomes unreachable.

### Files

| Path | Belongs to | Purpose |
|---|---|---|
| `src/`, `docs/README.md`, everything else of upstream's | upstream, edited only by `lake(0002+)` | the library |
| `README.md`, `AGENTS.md`, `AI_POLICY.md`, `docs/vision.md`, `docs/maintaining.md` | `lake(0001)` | the fork's front page, rules for agents, reasoning, this guide |
| `docs/changes/README.md`, `docs/changes/upstreaming.md` | `lake(0001)` | the stack's rules; how to take a change upstream |
| `docs/changes/NNNN-name.md` | `lake(NNNN)` | one record per change, in the change's own commit |
| `eng/lake/Lake.Stack/`, `eng/lake/Lake.Stack.Tests/` | `lake(0001)` | the stack tool and its tests (below) |
| `eng/lake/RenameNamespace.cs`, `Lake.Parquet.targets`, `VerifyLakeAssembly.cs`, `PACKAGE_README.md` | `lake(0001)` | the build-time rename to `Lake.Parquet` |
| `.github/workflows/lake.yml` | `lake(0001)` | the fork's CI: stack rules, the renamed build and tests, releases |

None of `lake(0001)`'s files exist upstream, so the bottom of the stack never conflicts.

## The tool: `eng/lake/Lake.Stack`

A small C# console app, with its tests in `eng/lake/Lake.Stack.Tests`. Every step of the process goes
through it (CI only runs `check` and `check-pr`), so the rules live in one place and are tested on real throwaway
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
| `rebaseline` | **the step that moves the fork onto a newer upstream**: rebase (folding in fixups), report, check, run the test suite; `--push` publishes it; `--continue` resumes after a conflict or a fix; `--abort` puts everything back | the local `master`; with `--push`, the remote's `master`, a tag and `lake/change/*` |
| `sync` | only the rebase part of `rebaseline`, for trying a rebase out locally | the current branch |
| `branches` | regenerates every `lake/change/*`; `--push origin` force-pushes them and deletes those of retired changes | `lake/change/*` |
| `rewire [--apply] (<branch>… \| --all)` | moves each branch's own commits onto the current stack; a dry run without `--apply` | the named branches |
| `promote --from <old> --to <new>` | the publishing part of `rebaseline --push`: refuses a new stack that fails `check`, tags the old head `lake/stack/<date>-<sha>`, and moves the remote's `master` with a lease on the old head | the remote's `master`, a tag |

`--upstream <ref>` (or `LAKE_UPSTREAM`; default `upstream/master`) sets the upstream every command
works against, and `--repo <path>` the repository. Exit codes: 0 done, 1 something to fix (a broken
rule, a conflict, a branch that would not build), 2 it could not run (a dirty tree, a bad argument).

All of it is plain git underneath: `rebase --autosquash`, `cherry-pick`, `merge-base`. `rerere` is
switched on, so a conflict resolved once is resolved again automatically on the next rebase. Change
branches are rebuilt with each source commit's own author, committer and dates, so an unchanged change
gives the same commit and pushing it again is a no-op.

The code is laid out by command: `Commands/RebaselineCommand.cs`, `SyncCommand.cs`, `BranchesCommand.cs`, `RewireCommand.cs`,
`PromoteCommand.cs`, `CheckCommand.cs`, `CheckPrCommand.cs` and `StatusCommand.cs`, over
`StackModel.cs` (the commits above upstream and their records), `ChangeRecord.cs` (the record format)
and `Git.cs` (git, run with an argument list, never through a shell). A change to how the process
works is a change there, with a test, as a `fixup! lake(0001): …`.

## Rebaselining onto upstream

A rebaseline is a decision, not a routine: run it when you want something from upstream (a release,
a fix), and expect to spend time on it, because upstream's changes can break the changes on top.
Nothing runs it for you.

```bash
git fetch origin && git switch master && git reset --hard origin/master   # start from what is published
lake-stack rebaseline                          # or: --onto upstream/<branch> or an upstream release tag
```

It stops at the first thing that needs you, and tells you what to run next:

1. **Rebase.** The stack is replayed onto upstream's `master`; upstream's commits keep their hashes,
   pending fixups are folded into their changes, and changes upstream already has drop out. It prints
   how many upstream commits came in, the range-diff of every change, the changes upstream took whole
   (dropped) and those left with only their record (upstream has the code: retire them).
2. **Conflicts.** If upstream changed code a change also changes, the rebase stops in that change's
   commit (see [When the rebase stops](#when-the-rebase-stops)). Resolve it, `git add`,
   `git rebase --continue`, then `lake-stack rebaseline --continue`.
3. **Verify.** `check` on the new stack, then the library's whole test suite
   (`src/Parquet.Test`, needs `pyarrow` and `pandas`). If a test fails, upstream broke a change: fix it
   in that change (`git commit --fixup=<its commit>`, `git rebase -i --autosquash $(git merge-base HEAD upstream/master)`),
   then `lake-stack rebaseline --continue`.
4. **Stop, unpublished.** The rebased stack is on your local `master` only. Look it over: the
   range-diff, `lake-stack status`, and if you like the CI build, by pushing it to a branch of its own
   (`git push origin HEAD:refs/heads/rebaseline-check`; `lake.yml` runs on its pull request or push).
5. **Publish, when you are satisfied**: `lake-stack rebaseline --continue --push`. It tags the old head
   `lake/stack/<date>-<sha>`, moves `origin/master` with a lease on the old head (if a pull request
   merged meanwhile, nothing moves: rebaseline again to include it), and regenerates every
   `lake/change/*` branch, deleting those of retired changes. The tests are not run again if `master`
   has not changed since they passed.

`lake-stack rebaseline --abort` at any point puts `master` back where it started. Until it is pushed
or aborted, the starting point is kept in git config (`lake.rebaseline.*`), so `--continue` works
across sessions. Publishing needs push rights that may force-push `master` and create tags (a
repository admin, past the branch rules).

### When the rebase stops

The rebase stops in the commit of the change that conflicts. Resolve it in that commit only, keeping
both intents; the change's own tests say whether the result is right. `rerere` is on, so the
resolution is recorded and replayed if you abort and start again on the same machine.

- **Upstream changed code a change also changes**: keep both intents.
- **Upstream took the change** (ours, after review, possibly rewritten): take upstream's code, drop the
  change's commit (`git rebase -i`), keep any test of ours that still adds something as a new change.
  When upstream took it exactly, the rebase already dropped it and the report says so.
- **Upstream fixed the same bug differently**: take upstream's fix, keep our test; if it still fails,
  that difference is a bug to report upstream (by hand) and to keep here meanwhile.

### Your other branches, after a rebaseline

A rebaseline moves `master` and the change branches. Any other branch (a change in progress, an
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
- a branch cut from upstream before the stack existed: its commits move onto the stack, and then
  build against every change;
- a branch already on the current stack: left alone.

It needs the `lake/stack/*` tags locally (`git fetch origin --tags`); without them a branch cut from
an older stack falls back to upstream as its fork point and replays the old stack's commits too
(which then conflict or duplicate). If that happens, `git rebase --abort` and fetch the tags.

On a conflict: resolve it, `git add`, `git rebase --continue`, then run `rewire --apply --all` again;
branches already on the stack are skipped.

To turn a rewired branch into a change: squash it into one commit titled `lake(NNNN): title` with
its record, and open a pull request (a fix to an existing change: `fixup! lake(NNNN): title`).

## Editing the stack itself

| Task | How |
|---|---|
| New change | PR from a branch on `master`, one commit `lake(NNNN): title` with `docs/changes/NNNN-name.md`, squash-merged with that title |
| Fix a change | PR with a `fixup! lake(NNNN): title` commit (`git commit --fixup=<its commit>`), squash-merged with that title; the next rebaseline folds it in |
| Fix the fork's tooling or docs | the same, as `fixup! lake(0001): …` |
| Fold fixups in, without moving to a newer upstream | `lake-stack rebaseline --onto $(git merge-base master upstream/master)`: the same steps, on the base you already have |
| Reword, reorder, split or retire changes | inside a rebaseline: once it stops ready and unpublished, `git rebase -i $(git merge-base HEAD upstream/master)`, then `lake-stack rebaseline --continue --push` (a changed head is checked and tested again); a retired change's record goes with its commit |
| Release | Actions → Lake.Parquet → Run workflow on `master` with `publish`; tags `lake-v<version>` |

Pending fixups are fine on `master` between rebaselines: `check` reports them, the build includes
them, and the next rebaseline folds each into its change.

## Recovering

Nothing a rebaseline replaces is lost. To see or restore an earlier stack:

```bash
git tag --list 'lake/stack/*' --sort=-creatordate | head      # previous heads, newest first
t=lake/stack/<tag>                                               # what changed since:
git range-diff $(git merge-base $t upstream/master)..$t $(git merge-base master upstream/master)..master
git push --force-with-lease origin lake/stack/<tag>:master     # roll back (an admin; tag the current head first)
```
