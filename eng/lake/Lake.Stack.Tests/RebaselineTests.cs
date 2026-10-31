using Lake.Stack.Commands;

namespace Lake.Stack.Tests;

public class RebaselineTests {

    static readonly Func<StackContext, int> Passing = _ => 0;
    static readonly Func<StackContext, int> Failing = _ => 1;

    static (int Code, string Output, string Error) Rebaseline(TestRepo repo, RebaselineOptions options) {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new StackContext(repo.Fork, "upstream/master", output, error);
        int code = RebaselineCommand.Run(context, options with { RunTests = options.RunTests ?? Passing });
        return (code, output.ToString(), error.ToString());
    }

    static string PublishedStack(TestRepo repo) {
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.Fork.Run("push", "--quiet", "origin", "master");
        return repo.Fork.Resolve("HEAD");
    }

    [Fact]
    public void Verifies_locally_and_publishes_only_when_asked() {
        using var repo = new TestRepo();
        string published = PublishedStack(repo);
        string upstream = repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));

        (int code, string output, _) = Rebaseline(repo, new RebaselineOptions());

        Assert.Equal(0, code);
        Assert.Contains("Ready, not published", output);
        Assert.True(repo.Fork.IsAncestor(upstream, "HEAD"));
        Assert.Equal(published, repo.Origin.Resolve("refs/heads/master"));

        int testRuns = 0;
        (code, output, _) = Rebaseline(repo, new RebaselineOptions(Continue: true, Push: true, RunTests: _ => { testRuns++; return 0; }));

        Assert.Equal(0, code);
        Assert.Equal(0, testRuns);   // the same head was already verified
        Assert.Equal(repo.Fork.Resolve("HEAD"), repo.Origin.Resolve("refs/heads/master"));
        string tag = Assert.Single(repo.Origin.Lines("tag", "--list", "lake/stack/*"));
        Assert.Equal(published, repo.Origin.Resolve(tag));
        Assert.NotNull(repo.Origin.TryResolve("refs/heads/lake/change/0002-fix-a"));
        Assert.False(repo.Fork.Succeeds("config", "--get", "lake.rebaseline.from"));
    }

    [Fact]
    public void Stops_at_a_conflict_and_continues_once_it_is_resolved() {
        using var repo = new TestRepo();
        string published = PublishedStack(repo);
        repo.UpstreamCommit("upstream: the same line", ("src/A.cs", TestRepo.Lines("a", 20, (3, "theirs"))));

        (int code, string output, _) = Rebaseline(repo, new RebaselineOptions());

        Assert.Equal(1, code);
        Assert.Contains("rebaseline --continue", output);
        File.WriteAllText(Path.Combine(repo.Fork.Directory, "src/A.cs"), TestRepo.Lines("a", 20, (3, "both")));
        repo.Fork.Run("add", "src/A.cs");
        repo.Fork.TryRun(["rebase", "--continue"], new Dictionary<string, string> { ["GIT_EDITOR"] = ":" });

        (code, _, _) = Rebaseline(repo, new RebaselineOptions(Continue: true, Push: true));

        Assert.Equal(0, code);
        Assert.Contains("both", repo.Origin.Run("show", "refs/heads/master:src/A.cs"));
        Assert.Equal(published, repo.Origin.Resolve(Assert.Single(repo.Origin.Lines("tag", "--list", "lake/stack/*"))));
    }

    [Fact]
    public void Does_not_publish_when_the_tests_fail() {
        using var repo = new TestRepo();
        string published = PublishedStack(repo);
        repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));

        (int code, _, string error) = Rebaseline(repo, new RebaselineOptions(Push: true, RunTests: Failing));

        Assert.Equal(1, code);
        Assert.Contains("tests fail", error);
        Assert.Equal(published, repo.Origin.Resolve("refs/heads/master"));
        Assert.Empty(repo.Origin.Lines("tag", "--list", "lake/stack/*"));
    }

    [Fact]
    public void Starts_only_from_the_published_stack() {
        using var repo = new TestRepo();
        PublishedStack(repo);
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "unpushed"))));

        StackException e = Assert.Throws<StackException>(() => Rebaseline(repo, new RebaselineOptions()));

        Assert.Contains("is not origin/master", e.Message);
    }

    [Fact]
    public void Abort_puts_everything_back() {
        using var repo = new TestRepo();
        string published = PublishedStack(repo);
        repo.UpstreamCommit("upstream: the same line", ("src/A.cs", TestRepo.Lines("a", 20, (3, "theirs"))));
        Assert.Equal(1, Rebaseline(repo, new RebaselineOptions()).Code);

        (int code, _, _) = Rebaseline(repo, new RebaselineOptions(Abort: true));

        Assert.Equal(0, code);
        Assert.Equal(published, repo.Fork.Resolve("HEAD"));
        Assert.False(Directory.Exists(repo.Fork.GitPath("rebase-merge")));
        Assert.False(repo.Fork.Succeeds("config", "--get", "lake.rebaseline.from"));
    }

    [Fact]
    public void Nothing_to_do_when_already_on_upstream() {
        using var repo = new TestRepo();
        PublishedStack(repo);

        (int code, string output, _) = Rebaseline(repo, new RebaselineOptions(Push: true));

        Assert.Equal(0, code);
        Assert.Contains("Nothing to do", output);
        Assert.Empty(repo.Origin.Lines("tag", "--list", "lake/stack/*"));
    }
}
