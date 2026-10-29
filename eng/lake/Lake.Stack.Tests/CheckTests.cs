namespace Lake.Stack.Tests;

public class CheckTests {

    [Fact]
    public void A_stack_of_recorded_changes_passes() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.AddChange("0003", "fix-b", "Fix B", dependsOn: ["0002"], files: ("src/B.cs", TestRepo.Lines("b", 20, (3, "fixed"))));

        (int code, string output, _) = repo.Tool("check");

        Assert.Equal(0, code);
        Assert.Contains("ok: 3 commits", output);
    }

    [Fact]
    public void A_pending_fixup_is_reported_but_passes() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed better"))));

        (int code, string output, _) = repo.Tool("check");

        Assert.Equal(0, code);
        Assert.Contains("pending:", output);
    }

    [Fact]
    public void A_commit_that_is_not_a_change_fails() {
        using var repo = new TestRepo();
        TestRepo.Commit(repo.Fork, "tweak A", ("src/A.cs", "changed\n"));
        AssertProblem(repo, "is not a change");
    }

    [Fact]
    public void A_change_without_its_record_fails() {
        using var repo = new TestRepo();
        TestRepo.Commit(repo.Fork, "lake(0002): Fix A", ("src/A.cs", "changed\n"));
        AssertProblem(repo, "has no record");
    }

    [Fact]
    public void A_record_added_by_another_commit_fails() {
        using var repo = new TestRepo();
        TestRepo.Commit(repo.Fork, "lake(0002): Fix A", ("src/A.cs", "changed\n"));
        TestRepo.Commit(repo.Fork, "lake(0003): The record", ("docs/changes/0002-fix-a.md", TestRepo.Record("Fix A")));
        AssertProblem(repo, "must be added or changed in its own commit");
    }

    [Fact]
    public void Two_commits_for_one_change_fail() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", "one\n"));
        TestRepo.Commit(repo.Fork, "lake(0002): Fix A again", ("src/A.cs", "two\n"));
        AssertProblem(repo, "more than one commit");
    }

    [Fact]
    public void A_dependency_above_the_change_fails() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", dependsOn: ["0003"], files: ("src/A.cs", "one\n"));
        repo.AddChange("0003", "fix-b", "Fix B", files: ("src/B.cs", "two\n"));
        AssertProblem(repo, "which is above it");
    }

    [Fact]
    public void An_addition_that_edits_upstream_files_fails() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "docs", "Docs", kind: "addition", files: ("src/A.cs", "edited\n"));
        AssertProblem(repo, "is an addition but changes upstream's files");
    }

    [Fact]
    public void A_merge_commit_above_upstream_fails() {
        using var repo = new TestRepo();
        repo.UpstreamCommit("upstream: third", ("src/D.cs", "d\n"));
        repo.Fork.Run("merge", "--quiet", "--no-ff", "-m", "merge upstream", "upstream/master");
        AssertProblem(repo, "is a merge commit");
    }

    static void AssertProblem(TestRepo repo, string problem) {
        (int code, _, string error) = repo.Tool("check");
        Assert.Equal(1, code);
        Assert.Contains(problem, error);
    }
}

public class CheckPrTests {

    [Theory]
    [InlineData("lake(0002): Fix A", true, 0)]
    [InlineData("lake(0002): Fix A", false, 1)]
    [InlineData("fixup! lake(0002): Fix A", false, 0)]
    [InlineData("Fix A", true, 1)]
    public void The_title_names_the_change_and_a_new_change_brings_its_record(string title, bool withRecord, int expected) {
        using var repo = new TestRepo();
        string @base = repo.Fork.Resolve("HEAD");
        repo.Fork.Run("switch", "--quiet", "-c", "pr");
        TestRepo.Commit(repo.Fork, "work in progress", ("src/A.cs", "fixed\n"));
        if(withRecord) {
            TestRepo.Commit(repo.Fork, "the record", ("docs/changes/0002-fix-a.md", TestRepo.Record("Fix A")));
        }

        (int code, _, _) = repo.Tool("check-pr", "--title", title, "--base", @base);

        Assert.Equal(expected, code);
    }
}
