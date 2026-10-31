using Lake.Stack.Commands;

namespace Lake.Stack.Tests;

public class VerifyTests {

    /// <summary>A stand-in test suite: it passes when src/A.cs has the fix.</summary>
    static int SuiteNeedsTheFix(string directory) =>
        File.ReadAllText(Path.Combine(directory, "src/A.cs")).Contains("fixed") ? 0 : 1;

    static (int Code, string Output) Verify(TestRepo repo, Func<string, int> runTests, params string[] changes) {
        var output = new StringWriter();
        int code = VerifyCommand.Run(new StackContext(repo.Fork, "upstream/master", output, new StringWriter()),
            "HEAD", changes, list: false, VerifyCommand.DefaultTestProject, runTests);
        return (code, output.ToString());
    }

    static void AddFix(TestRepo repo, string evidence = "") =>
        TestRepo.Commit(repo.Fork, "lake(0002): Fix A",
            ("docs/changes/0002-fix-a.md", TestRepo.Record("Fix A").Replace("depends_on: []", "depends_on: []" + evidence)),
            ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))),
            ("src/Parquet.Test/ATest.cs", "a test of the fix\n"));

    [Fact]
    public void A_change_whose_tests_need_its_code_is_proven() {
        using var repo = new TestRepo();
        AddFix(repo);

        (int code, string output) = Verify(repo, SuiteNeedsTheFix);

        Assert.Equal(0, code);
        Assert.Contains("lake(0002) proven", output);
        Assert.Single(repo.Fork.Lines("worktree", "list"));
    }

    [Fact]
    public void A_change_with_a_pending_fixup_is_proven_without_both() {
        using var repo = new TestRepo();
        AddFix(repo);
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed better"))));

        (int code, string output) = Verify(repo, SuiteNeedsTheFix);

        // reverting to HEAD~1 would have left the change itself in place, and the suite passing: "untested"
        Assert.Equal(0, code);
        Assert.Contains("lake(0002) proven", output);
        Assert.Single(repo.Fork.Lines("worktree", "list"));
    }

    [Fact]
    public void A_dependencys_fixup_above_the_change_stays_when_the_change_is_reverted() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "base", "Base", files: ("src/B.cs", TestRepo.Lines("b", 20, (3, "base"))));
        TestRepo.Commit(repo.Fork, "lake(0003): Fix A",
            ("docs/changes/0003-fix-a.md", TestRepo.Record("Fix A", dependsOn: ["0002"])),
            ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))),
            ("src/Parquet.Test/ATest.cs", "a test of the fix\n"));
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Base", ("src/B.cs", TestRepo.Lines("b", 20, (3, "base, fixed up"))));

        // the suite needs the change's fix, and the dependency's fixup, which sits above the change in the stack
        (int code, string output) = Verify(repo, d =>
            File.ReadAllText(Path.Combine(d, "src/B.cs")).Contains("fixed up")
                ? (File.ReadAllText(Path.Combine(d, "src/A.cs")).Contains("fixed") ? 0 : 1)
                : 2, "0003");

        Assert.Equal(0, code);
        Assert.Contains("lake(0003) proven", output);
    }

    [Fact]
    public void A_change_nothing_tests_is_reported() {
        using var repo = new TestRepo();
        AddFix(repo);

        (int code, string output) = Verify(repo, _ => 0);

        Assert.Equal(1, code);
        Assert.Contains("lake(0002) untested", output);
        Assert.Contains("src/A.cs", output);
    }

    [Fact]
    public void A_change_failing_on_its_own_branch_is_reported() {
        using var repo = new TestRepo();
        AddFix(repo);

        (int code, string output) = Verify(repo, _ => 1);

        Assert.Equal(1, code);
        Assert.Contains("lake(0002) fails", output);
    }

    [Fact]
    public void A_benchmarked_change_only_has_to_pass() {
        using var repo = new TestRepo();
        AddFix(repo, "\nevidence: benchmark");

        (int code, string output) = Verify(repo, _ => 0);

        Assert.Equal(0, code);
        Assert.Contains("lake(0002) passes", output);
    }

    [Fact]
    public void A_tests_only_change_only_has_to_pass() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "more-tests", "More tests", files: ("src/Parquet.Test/MoreTest.cs", "tests\n"));

        (int code, string output) = Verify(repo, _ => 0);

        Assert.Equal(0, code);
        Assert.Contains("tests only", output);
    }

    [Fact]
    public void List_prints_the_code_changes_for_a_ci_matrix() {
        using var repo = new TestRepo();
        AddFix(repo);
        repo.AddChange("0003", "fix-b", "Fix B", files: ("src/B.cs", "b\n"));

        (int code, string output, _) = repo.Tool("verify", "--list");

        Assert.Equal(0, code);
        Assert.Equal("[\"0002\",\"0003\"]", output.Trim());
    }
}
