using Lake.Stack.Commands;
using Xunit;

namespace Lake.Stack.Tests;

public class HistoryTests {

    [Fact]
    public void Each_publication_of_a_change_branch_is_a_version_that_stays_reachable() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        Assert.Equal(0, repo.Tool("branches", "--push", "origin").Code);
        string first = repo.Fork.Resolve("lake/change/0002-fix-a");

        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed better"))));
        Assert.Equal(0, repo.Tool("branches", "--push", "origin").Code);
        string second = repo.Fork.Resolve("lake/change/0002-fix-a");
        Assert.NotEqual(first, second);

        IReadOnlyList<HistoryVersion> versions = HistoryCommand.Versions(repo.Fork, "0002-fix-a");
        Assert.Equal([second, first], versions.Select(v => v.Published));
        // the history is on the remote too, and both versions are reachable from it
        string remoteHistory = repo.Origin.Resolve("refs/heads/lake/history/0002-fix-a");
        Assert.True(repo.Origin.IsAncestor(first, remoteHistory));
        Assert.True(repo.Origin.IsAncestor(second, remoteHistory));
        // each record carries the published tree
        Assert.Equal(repo.Fork.Run("rev-parse", second + "^{tree}"), repo.Fork.Run("rev-parse", versions[0].Record + "^{tree}"));
    }

    [Fact]
    public void Publishing_the_same_commit_again_records_nothing() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        Assert.Equal(0, repo.Tool("branches", "--push", "origin").Code);
        Assert.Equal(0, repo.Tool("branches", "--push", "origin").Code);

        Assert.Single(HistoryCommand.Versions(repo.Fork, "0002-fix-a"));
    }

    [Fact]
    public void Records_made_from_another_clone_are_kept() {
        using var repo = new TestRepo();
        string head = repo.Fork.Resolve("HEAD");
        Assert.NotNull(HistoryCommand.Record(repo.Fork, HistoryCommand.Stack, head, "origin"));

        // a fresh clone of the fork without the local history ref records its next version after the remote's
        repo.Fork.Run("update-ref", "-d", "refs/heads/lake/history/stack");
        string next = TestRepo.Commit(repo.Fork, "lake(0001): Packaging", ("README.md", "fork"));
        Assert.NotNull(HistoryCommand.Record(repo.Fork, HistoryCommand.Stack, next, "origin"));

        Assert.Equal([next, head], HistoryCommand.Versions(repo.Fork, HistoryCommand.Stack).Select(v => v.Published));
    }

    [Fact]
    public void Show_lists_versions_and_how_to_compare_them() {
        using var repo = new TestRepo();
        string a = repo.Fork.Resolve("HEAD");
        string b = TestRepo.Commit(repo.Fork, "lake(0001): Packaging", ("README.md", "fork"));
        HistoryCommand.Record(repo.Fork, HistoryCommand.Stack, a, null);
        HistoryCommand.Record(repo.Fork, HistoryCommand.Stack, b, null);

        (int code, string output, _) = repo.Tool("history", "show", "stack");

        Assert.Equal(0, code);
        Assert.Contains(b[..12], output);
        Assert.Contains($"git range-diff {a[..12]}...{b[..12]}", output);
    }
}
