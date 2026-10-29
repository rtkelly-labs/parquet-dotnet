using Lake.Stack.Commands;

namespace Lake.Stack.Tests;

public class SyncTests {

    [Fact]
    public void Replays_the_stack_on_new_upstream_commits_and_keeps_theirs_unchanged() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        string upstream = repo.UpstreamCommit("upstream: edit B", ("src/B.cs", TestRepo.Lines("b", 20, (10, "upstream"))));

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        Assert.Null(result.Conflict);
        Assert.True(result.Changed);
        Assert.Equal(upstream, result.NewBase);
        Assert.True(repo.Fork.IsAncestor(upstream, "HEAD"), "upstream's commit, with its own hash, is in the new stack");
        Assert.Equal("2", repo.Fork.Run("rev-list", "--count", "upstream/master..HEAD"));
        Assert.Contains("fixed", repo.Read("src/A.cs"));
        Assert.Contains("upstream", repo.Read("src/B.cs"));
    }

    [Fact]
    public void Leaves_a_stack_that_is_already_on_upstream_alone() {
        using var repo = new TestRepo();
        string before = repo.Fork.Resolve("HEAD");

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        Assert.False(result.Changed);
        Assert.Equal(before, repo.Fork.Resolve("HEAD"));
    }

    [Fact]
    public void Folds_a_fixup_into_its_change() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.AddChange("0003", "fix-b", "Fix B", files: ("src/B.cs", TestRepo.Lines("b", 20, (3, "fixed"))));
        TestRepo.Commit(repo.Fork, "fixup! lake(0002): Fix A", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed better"))));

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        Assert.True(result.Changed);
        StackModel model = StackModel.Load(repo.Fork, "HEAD", "upstream/master");
        Assert.Equal(["lake(0001): Packaging", "lake(0002): Fix A", "lake(0003): Fix B"], model.Commits.Select(c => c.Subject));
        Assert.Contains("fixed better", repo.Fork.Run("show", model.CommitOf("0002")!.Sha + ":src/A.cs"));
    }

    [Fact]
    public void Reports_a_change_whose_code_upstream_took() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.UpstreamCommit("upstream: the same fix, by someone else", ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        Assert.Equal(["0002"], result.CodeGone);
        Assert.Contains("Only the record is left", SyncCommand.Report(repo.Context(), result, "upstream/master"));
    }

    [Fact]
    public void Drops_a_commit_upstream_has_exactly() {
        using var repo = new TestRepo();
        string change = repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        // Upstream cherry-picks the whole commit, record and all.
        repo.Upstream.Run("fetch", "--quiet", repo.Fork.Directory, "master");
        repo.Upstream.Run("cherry-pick", change);
        repo.Fork.Run("fetch", "--quiet", "upstream");

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false));

        Assert.Equal(["0002"], result.Dropped);
        Assert.Null(StackModel.Load(repo.Fork, "HEAD", "upstream/master").CommitOf("0002"));
    }

    [Fact]
    public void Stops_at_a_conflict_and_can_leave_the_stack_untouched() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "ours"))));
        repo.UpstreamCommit("upstream: the same line", ("src/A.cs", TestRepo.Lines("a", 20, (3, "theirs"))));
        string before = repo.Fork.Resolve("HEAD");

        SyncResult result = SyncCommand.Sync(repo.Context(), new SyncOptions(Fetch: false, AbortOnConflict: true));

        Assert.NotNull(result.Conflict);
        Assert.Equal("lake(0002): Fix A", result.Conflict!.Subject);
        Assert.Equal(["src/A.cs"], result.Conflict.Files);
        Assert.Equal(before, repo.Fork.Resolve("HEAD"));
        Assert.False(Directory.Exists(repo.Fork.GitPath("rebase-merge")));
    }

    [Fact]
    public void Leaves_a_conflict_to_resolve_and_continues_once_it_is() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "ours"))));
        repo.UpstreamCommit("upstream: the same line", ("src/A.cs", TestRepo.Lines("a", 20, (3, "theirs"))));

        (int code, string output, _) = repo.Tool("sync", "--no-fetch");

        Assert.Equal(1, code);
        Assert.Contains("stopped", output);
        Assert.True(Directory.Exists(repo.Fork.GitPath("rebase-merge")));
        // Resolve and continue, as docs/maintaining.md says: the stack is rebased and passes its check.
        File.WriteAllText(Path.Combine(repo.Fork.Directory, "src/A.cs"), TestRepo.Lines("a", 20, (3, "both")));
        repo.Fork.Run("add", "src/A.cs");
        repo.Fork.TryRun(["rebase", "--continue"], new Dictionary<string, string> { ["GIT_EDITOR"] = ":" });
        Assert.Contains("both", repo.Read("src/A.cs"));
        Assert.Equal(0, repo.Tool("check").Code);
    }

    [Fact]
    public void Refuses_a_dirty_working_tree() {
        using var repo = new TestRepo();
        File.WriteAllText(Path.Combine(repo.Fork.Directory, "src/A.cs"), "uncommitted\n");

        (int code, _, string error) = repo.Tool("sync", "--no-fetch");

        Assert.Equal(2, code);
        Assert.Contains("working tree has changes", error);
    }

    [Fact]
    public void Writes_outputs_for_the_workflow() {
        using var repo = new TestRepo();
        repo.AddChange("0002", "fix-a", "Fix A", files: ("src/A.cs", TestRepo.Lines("a", 20, (3, "fixed"))));
        repo.UpstreamCommit("upstream: edit B", ("src/B.cs", TestRepo.Lines("b", 20, (10, "upstream"))));
        string outputs = Path.GetTempFileName();
        string report = Path.GetTempFileName();

        (int code, _, _) = repo.Tool("sync", "--no-fetch", "--outputs", outputs, "--report", report);

        Assert.Equal(0, code);
        string written = File.ReadAllText(outputs);
        Assert.Contains("changed=true", written);
        Assert.Contains($"after={repo.Fork.Resolve("HEAD")}", written);
        Assert.Contains("conflict=false", written);
        Assert.Contains("## Rebased onto", File.ReadAllText(report));
    }
}

public class SyncPublishTests {

    [Fact]
    public void Publishes_a_changed_stack_to_lake_next() {
        using var repo = new TestRepo();
        repo.UpstreamCommit("upstream: new", ("src/D.cs", "d\n"));

        (int code, _, _) = repo.Tool("sync", "--no-fetch", "--publish", "origin");

        Assert.Equal(0, code);
        Assert.Equal(repo.Fork.Resolve("HEAD"), repo.Origin.Resolve("refs/heads/lake/next"));
    }
}
