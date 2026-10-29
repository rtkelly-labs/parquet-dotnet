using Lake.Stack.Commands;

namespace Lake.Stack.Tests;

public class RewireAndPromoteTests {

    /// <summary>
    /// The situation after a daily sync: the stack has been rebased onto a new upstream commit (with a
    /// fixup folded in), the old head is tagged, and branches built on the old stack or on upstream are
    /// left behind.
    /// </summary>
    static (string OldStack, string NewStack) SyncWithBranchesLeftBehind(TestRepo repo) {
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed better"))));
        string oldStack = repo.Fork.Resolve("HEAD");

        repo.Fork.Run("switch", "--quiet", "-c", "feature", "master");
        TestRepo.Commit(repo.Fork, "my feature 1", ("src/B.cs", TestRepo.Lines("b", 20, (1, "mine"))));
        TestRepo.Commit(repo.Fork, "my feature 2", ("src/B.cs", TestRepo.Lines("b", 20, (1, "mine"), (2, "mine too"))));
        repo.Fork.Run("switch", "--quiet", "-c", "from-upstream", "upstream/master");
        TestRepo.Commit(repo.Fork, "an old experiment", ("src/C.cs", TestRepo.Lines("c", 5, (1, "experiment"))));
        repo.Fork.Run("switch", "--quiet", "master");

        repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));
        Assert.True(SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false)).Changed);
        repo.Fork.Run("tag", PromoteCommand.TagFor(oldStack, DateTimeOffset.UtcNow), oldStack);
        return (oldStack, repo.Fork.Resolve("HEAD"));
    }

    [Fact]
    public void Rewire_moves_only_each_branchs_own_commits_onto_the_new_stack() {
        using var repo = new TestRepo();
        (string oldStack, string newStack) = SyncWithBranchesLeftBehind(repo);
        repo.Fork.Run("switch", "--quiet", "-c", "current", "master");
        TestRepo.Commit(repo.Fork, "already on the new stack", ("src/E.cs", "e\n"));
        repo.Fork.Run("switch", "--quiet", "master");

        (int code, string output, _) = repo.Tool("rewire", "--apply", "--all");

        Assert.Equal(0, code);
        Assert.Contains("current: already on master", output);
        Assert.Equal(["my feature 2", "my feature 1"], repo.Fork.Lines("log", "--format=%s", "master..feature"));
        Assert.True(repo.Fork.IsAncestor(newStack, "feature"));
        Assert.False(repo.Fork.IsAncestor(oldStack, "feature"), "the old stack's commits are not carried along");
        Assert.Equal(["an old experiment"], repo.Fork.Lines("log", "--format=%s", "master..from-upstream"));
        Assert.Equal("master", repo.Fork.CurrentBranchOrCommit());
    }

    [Fact]
    public void Rewire_without_apply_changes_nothing() {
        using var repo = new TestRepo();
        SyncWithBranchesLeftBehind(repo);
        string feature = repo.Fork.Resolve("feature");

        (int code, string output, _) = repo.Tool("rewire", "feature");

        Assert.Equal(0, code);
        Assert.Contains("feature: would move 2 commit(s)", output);
        Assert.Contains("dry run", output);
        Assert.Equal(feature, repo.Fork.Resolve("feature"));
    }

    [Fact]
    public void Rewire_stops_at_a_conflict() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.Fork.Run("switch", "--quiet", "-c", "clash", "upstream/master");
        TestRepo.Commit(repo.Fork, "the same line", ("src/A.cs", TestRepo.Lines("a", 20, (3, "mine"))));
        repo.Fork.Run("switch", "--quiet", "master");

        (int code, _, string error) = repo.Tool("rewire", "--apply", "clash");

        Assert.Equal(1, code);
        Assert.Contains("clash: conflict", error);
    }

    [Fact]
    public void Promote_tags_the_old_head_and_moves_the_remote_branch() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.Fork.Run("push", "--quiet", "origin", "master");
        string oldStack = repo.Fork.Resolve("HEAD");
        repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));
        SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));
        string newStack = repo.Fork.Resolve("HEAD");
        repo.Fork.Run("push", "--quiet", "--force", "origin", "HEAD:refs/heads/lake/next");

        (int code, string output, _) = repo.Tool("promote", "--from", oldStack, "--to", newStack);

        Assert.Equal(0, code);
        Assert.Contains("tagged lake/stack/", output);
        Assert.Equal(newStack, repo.Origin.Resolve("refs/heads/master"));
        string tag = Assert.Single(repo.Origin.Lines("tag", "--list", "lake/stack/*"));
        Assert.Equal(oldStack, repo.Origin.Resolve(tag));
        Assert.Null(repo.Origin.TryResolve("refs/heads/lake/next"));
    }

    [Fact]
    public void Promote_refuses_when_the_remote_moved_meanwhile() {
        using var repo = new TestRepo();
        string oldStack = repo.Fork.Resolve("HEAD");
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        string merged = repo.Fork.Resolve("HEAD");
        repo.Fork.Run("push", "--quiet", "origin", "master");   // a pull request merged while the sync ran
        repo.Fork.Run("reset", "--quiet", "--hard", oldStack);
        repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));
        SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        (int code, _, string error) = repo.Tool("promote", "--from", oldStack, "--to", "HEAD");

        Assert.Equal(1, code);
        Assert.Contains("is no longer at", error);
        Assert.Equal(merged, repo.Origin.Resolve("refs/heads/master"));
    }

    [Fact]
    public void Promote_refuses_a_stack_that_breaks_the_rules() {
        using var repo = new TestRepo();
        string oldStack = repo.Fork.Resolve("HEAD");
        TestRepo.Commit(repo.Fork, "not a change", ("src/A.cs", "x\n"));

        (int code, _, string error) = repo.Tool("promote", "--from", oldStack, "--to", "HEAD");

        Assert.Equal(1, code);
        Assert.Contains("breaks the stack's rules", error);
        Assert.Equal(oldStack, repo.Origin.Resolve("refs/heads/master"));
    }
}
