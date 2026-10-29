namespace Lake.Stack.Commands;

public sealed record BranchesResult(IReadOnlyList<string> Built, IReadOnlyList<string> Failed, IReadOnlyList<string> Deleted);

/// <summary>
/// Regenerates <c>lake/change/NNNN-name</c> for every code change: upstream, then the changes its
/// depends_on needs (transitively), then the change, without the records. Its diff against upstream is
/// what an upstream pull request would contain. Branches are rebuilt with each source commit's own
/// committer and date, so an unchanged change gives the same commit and a push is a no-op.
/// </summary>
public static class BranchesCommand {

    public const string Prefix = "lake/change/";

    public static int Run(StackContext context, string head, string? onto, string? pushTo) {
        BranchesResult result = Build(context, head, onto);
        if(pushTo != null) {
            Push(context, pushTo, result);
        }
        return result.Failed.Count == 0 ? ExitCodes.Ok : ExitCodes.Problems;
    }

    public static BranchesResult Build(StackContext context, string head, string? onto) {
        Git git = context.Git;
        string ontoSha = git.Resolve(onto ?? context.Upstream);
        StackModel model = StackModel.Load(git, head, ontoSha);
        var built = new List<string>();
        var failed = new List<string>();

        string path = Path.Combine(Path.GetTempPath(), "lake-branches-" + Guid.NewGuid().ToString("n")[..12]);
        git.Run("worktree", "add", "--quiet", "--detach", path, ontoSha);
        try {
            var tree = new Git(path);
            foreach(StackCommit change in model.Changes) {
                if(!model.Records.TryGetValue(change.Number!, out ChangeRecord? record) || record.Kind != ChangeKind.Code) {
                    continue;
                }
                tree.Run("reset", "--quiet", "--hard", ontoSha);
                tree.Run("clean", "--quiet", "-fd");
                IReadOnlyList<StackCommit> commits = model.WithDependencies(change.Number!);
                StackCommit? blocking = commits.FirstOrDefault(c => !Replay(tree, c));
                if(blocking != null) {
                    string needs = string.Join(", ", commits.Select(c => c.Number));
                    context.Error.WriteLine($"{record.BranchName}: lake({blocking.Number}) does not apply on {ontoSha[..10]} with [{needs}]: it needs another change in depends_on, or it conflicts with upstream (sync first)");
                    failed.Add(record.BranchName);
                    continue;
                }
                string tip = tree.Resolve("HEAD");
                git.Run("branch", "--force", record.BranchName, tip);
                built.Add(record.BranchName);
                context.Out.WriteLine($"{record.BranchName} {tip[..10]} ({commits.Count} commit{(commits.Count == 1 ? "" : "s")})");
            }
        } finally {
            git.TryRun("worktree", "remove", "--force", path);
            git.TryRun("worktree", "prune");
        }

        // Branches of changes that left the stack go too; a failed one keeps its last good state.
        var deleted = new List<string>();
        foreach(string branch in git.Lines("for-each-ref", "--format=%(refname:short)", "refs/heads/" + Prefix)) {
            if(!built.Contains(branch) && !failed.Contains(branch)) {
                git.Run("branch", "--quiet", "-D", branch);
                deleted.Add(branch);
                context.Out.WriteLine($"{branch} deleted: its change is no longer in the stack");
            }
        }
        return new BranchesResult(built, failed, deleted);
    }

    /// <summary>Applies one stack commit without its record, keeping its message, author and committer.</summary>
    static bool Replay(Git tree, StackCommit commit) {
        if(tree.TryRun("cherry-pick", "--no-commit", commit.Sha).ExitCode != 0) {
            tree.TryRun("cherry-pick", "--abort");
            tree.TryRun("reset", "--quiet", "--hard");
            return false;
        }
        tree.Run("rm", "-r", "--quiet", "--cached", "--ignore-unmatch", "--", ChangeRecord.Directory);
        string records = Path.Combine(tree.Directory, ChangeRecord.Directory);
        if(Directory.Exists(records)) {
            Directory.Delete(records, recursive: true);
        }
        string[] committer = tree.Run("log", "-1", "--format=%cn%x00%ce%x00%cI", commit.Sha).Split('\0');
        tree.Run(
            ["commit", "--quiet", "--allow-empty", "--no-verify", "--reuse-message=" + commit.Sha],
            new Dictionary<string, string> {
                ["GIT_COMMITTER_NAME"] = committer[0],
                ["GIT_COMMITTER_EMAIL"] = committer[1],
                ["GIT_COMMITTER_DATE"] = committer[2],
            });
        return true;
    }

    /// <summary>Force-pushes the built branches and deletes the remote's branches of retired changes.</summary>
    public static void Push(StackContext context, string remote, BranchesResult result) {
        Git git = context.Git;
        if(result.Built.Count > 0) {
            git.Run(["push", "--quiet", "--force", remote, .. result.Built.Select(b => $"refs/heads/{b}:refs/heads/{b}")]);
        }
        foreach(string line in git.Lines("ls-remote", "--heads", remote, "refs/heads/" + Prefix + "*")) {
            string branch = line.Split('\t')[1]["refs/heads/".Length..];
            if(!result.Built.Contains(branch) && !result.Failed.Contains(branch)) {
                git.Run("push", "--quiet", remote, "--delete", branch);
                context.Out.WriteLine($"{remote}/{branch} deleted");
            }
        }
        context.Out.WriteLine($"pushed {result.Built.Count} change branch(es) to {remote}");
    }
}
