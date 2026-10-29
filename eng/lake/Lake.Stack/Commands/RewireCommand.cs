namespace Lake.Stack.Commands;

public enum RewireOutcome {
    /// <summary>Its own commits were (or, in a dry run, would be) moved onto the stack.</summary>
    Moved,
    AlreadyOnStack,
    /// <summary>It has no commits of its own: it is part of the stack or of upstream.</summary>
    NothingOwn,
    NotFound,
    Conflict,
}

public sealed record RewireEntry(string Branch, RewireOutcome Outcome, string? ForkPoint, IReadOnlyList<string> OwnCommits);

/// <summary>
/// Puts other branches back on top of the stack after it has been rebased. A branch's own commits are
/// those after the newest point where it meets the stack, a previous stack head (the lake/stack/* tags
/// the sync leaves), a release (lake-v*), or upstream; only those are moved, with
/// <c>git rebase --onto &lt;stack&gt; &lt;fork point&gt; &lt;branch&gt;</c>.
/// </summary>
public static class RewireCommand {

    public static int Run(StackContext context, string onto, bool apply, bool all, IReadOnlyList<string> names) {
        Git git = context.Git;
        List<string> branches = all ? AllBranches(git, onto) : names.ToList();
        if(branches.Count == 0) {
            throw new StackException("which branches? Name them, or pass --all");
        }
        if(apply && !git.IsClean()) {
            throw new StackException("the working tree has changes: commit or stash them first");
        }

        IReadOnlyList<RewireEntry> entries = Rewire(context, onto, apply, branches);
        if(!apply) {
            context.Out.WriteLine("(dry run: add --apply to move them)");
        }
        return entries.Any(e => e.Outcome == RewireOutcome.Conflict) ? ExitCodes.Problems : ExitCodes.Ok;
    }

    /// <summary>Every local branch except the stack itself and what the sync generates.</summary>
    public static List<string> AllBranches(Git git, string onto) =>
        git.Lines("for-each-ref", "--format=%(refname:short)", "refs/heads/")
            .Where(b => b != onto && b != PromoteCommand.NextBranch && !b.StartsWith(BranchesCommand.Prefix, StringComparison.Ordinal))
            .ToList();

    public static IReadOnlyList<RewireEntry> Rewire(StackContext context, string onto, bool apply, IReadOnlyList<string> branches) {
        Git git = context.Git;
        string stack = git.Resolve(onto);
        if(apply) {
            git.Run("config", "rerere.enabled", "true");
        }
        string here = git.CurrentBranchOrCommit();
        var entries = new List<RewireEntry>();
        bool stopped = false;
        foreach(string branch in branches) {
            RewireEntry entry = RewireOne(context, onto, stack, branch, apply);
            entries.Add(entry);
            if(entry.Outcome == RewireOutcome.Conflict) {
                stopped = true;
                break;
            }
        }
        if(!stopped && apply) {
            git.Run("checkout", "--quiet", here);
        }
        return entries;
    }

    static RewireEntry RewireOne(StackContext context, string onto, string stack, string branch, bool apply) {
        Git git = context.Git;
        TextWriter output = context.Out;
        if(git.TryResolve("refs/heads/" + branch) == null) {
            context.Error.WriteLine($"{branch}: no such local branch");
            return new RewireEntry(branch, RewireOutcome.NotFound, null, []);
        }
        string forkPoint = ForkPoint(context, stack, branch);
        IReadOnlyList<string> own = git.Lines("log", "--no-merges", "--format=%h %s", $"{forkPoint}..refs/heads/{branch}");
        if(own.Count == 0) {
            output.WriteLine($"{branch}: nothing of its own (it is part of the stack or upstream); leave it or delete it");
            return new RewireEntry(branch, RewireOutcome.NothingOwn, forkPoint, own);
        }
        if(git.IsAncestor(stack, "refs/heads/" + branch)) {
            output.WriteLine($"{branch}: already on {onto} ({own.Count} commit(s) of its own)");
            return new RewireEntry(branch, RewireOutcome.AlreadyOnStack, forkPoint, own);
        }
        if(!apply) {
            output.WriteLine($"{branch}: would move {own.Count} commit(s) from {forkPoint[..10]} onto {onto}");
            foreach(string commit in own) {
                output.WriteLine("    " + commit);
            }
            return new RewireEntry(branch, RewireOutcome.Moved, forkPoint, own);
        }
        output.WriteLine($"{branch}: moving {own.Count} commit(s) from {forkPoint[..10]} onto {onto}");
        GitResult rebase = git.TryRun("rebase", "--quiet", "--onto", stack, forkPoint, branch);
        if(rebase.ExitCode != 0) {
            context.Error.WriteLine($"{branch}: conflict. Resolve it, 'git rebase --continue', then run rewire again for the rest.");
            context.Error.WriteLine((rebase.Error + rebase.Output).Trim());
            return new RewireEntry(branch, RewireOutcome.Conflict, forkPoint, own);
        }
        output.WriteLine($"    done; publish it with: git push --force-with-lease origin {branch}");
        return new RewireEntry(branch, RewireOutcome.Moved, forkPoint, own);
    }

    /// <summary>
    /// The newest of the branch's merge bases with the stack, upstream, and every lake/stack/* and
    /// lake-v* tag. Merge bases along one history are ordered by ancestry, so the newest is the one every
    /// other is an ancestor of.
    /// </summary>
    public static string ForkPoint(StackContext context, string stack, string branch) {
        Git git = context.Git;
        IEnumerable<string> candidates = new[] { stack, context.Upstream }
            .Concat(git.Lines("tag", "--list", PromoteCommand.TagPrefix + "*"))
            .Concat(git.Lines("tag", "--list", "lake-v*"));
        string? best = null;
        foreach(string candidate in candidates) {
            GitResult mergeBase = git.TryRun("merge-base", "refs/heads/" + branch, candidate);
            if(mergeBase.ExitCode != 0) {
                continue;
            }
            string point = mergeBase.Output.Trim();
            if(best == null || git.IsAncestor(best, point)) {
                best = point;
            }
        }
        return best ?? throw new StackException($"{branch} shares no history with {stack} or {context.Upstream}");
    }
}
