using Lake.Stack.Commands;

namespace Lake.Stack.Tests;

public class BranchesTests {

    [Fact]
    public void Each_code_change_gets_a_branch_of_upstream_plus_it_and_its_dependencies() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.AddChange("0003", "fix-b", "Fix B", files: ("src/B.cs", TestRepo.Lines("b", 20, (3, "fixed"))));
        repo.AddChange("0004", "on-a", "Builds on A", dependsOn: ["0002"], files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"), (4, "more"))));

        BranchesResult result = BranchesCommand.Build(repo.Context(), "HEAD", null);

        Assert.Equal(["lake/change/0002-fix-a", "lake/change/0003-fix-b", "lake/change/0004-on-a"], result.Built);
        Assert.Empty(result.Failed);
        Assert.Equal(
            ["lake(0004): Builds on A", "lake(0002): Fix A"],
            repo.Fork.Lines("log", "--format=%s", "upstream/master..lake/change/0004-on-a"));
        // No records, nothing of 0001's: the diff is what an upstream pull request would contain.
        Assert.Equal(["src/A.cs"], repo.Fork.Lines("diff", "--name-only", "upstream/master", "lake/change/0004-on-a"));
        Assert.Single(repo.Fork.Lines("worktree", "list"));
    }

    [Fact]
    public void Rebuilding_an_unchanged_stack_gives_the_same_commits() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        BranchesCommand.Build(repo.Context(), "HEAD", null);
        string first = repo.Fork.Resolve("lake/change/0002-fix-a");
        Thread.Sleep(1100);

        BranchesCommand.Build(repo.Context(), "HEAD", null);

        Assert.Equal(first, repo.Fork.Resolve("lake/change/0002-fix-a"));
    }

    [Fact]
    public void A_change_that_needs_an_unlisted_dependency_fails_and_cleans_up() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.AddChange("0003", "on-a", "Builds on A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed again"))));

        (int code, _, string error) = repo.Tool("branches");

        Assert.Equal(1, code);
        Assert.Contains("lake/change/0003-on-a: lake(0003) does not apply", error);
        Assert.NotNull(repo.Fork.TryResolve("lake/change/0002-fix-a"));
        Assert.Single(repo.Fork.Lines("worktree", "list"));
    }

    [Fact]
    public void Pushes_the_branches_and_deletes_those_of_retired_changes() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        string retiring = repo.AddChange("0003", "fix-b", "Fix B", files: ("src/B.cs", TestRepo.Lines("b", 20, (3, "fixed"))));
        Assert.Equal(0, repo.Tool("branches", "--push", "origin").Code);
        Assert.NotNull(repo.Origin.TryResolve("refs/heads/lake/change/0003-fix-b"));

        repo.Fork.Run("reset", "--quiet", "--hard", retiring + "~1");
        (int code, string output, _) = repo.Tool("branches", "--push", "origin");

        Assert.Equal(0, code);
        Assert.Contains("lake/change/0003-fix-b deleted", output);
        Assert.Null(repo.Fork.TryResolve("refs/heads/lake/change/0003-fix-b"));
        Assert.Null(repo.Origin.TryResolve("refs/heads/lake/change/0003-fix-b"));
        Assert.NotNull(repo.Origin.TryResolve("refs/heads/lake/change/0002-fix-a"));
    }
}
