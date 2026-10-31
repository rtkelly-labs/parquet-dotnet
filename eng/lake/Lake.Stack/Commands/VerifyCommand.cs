using System.Diagnostics;

namespace Lake.Stack.Commands;

public enum VerifyOutcome {
    /// <summary>The suite passes with the change, and fails without its library code.</summary>
    Proven,
    /// <summary>The suite passes with the change; its evidence is a benchmark, or it has no library code.</summary>
    Passes,
    /// <summary>The change's branch does not build from upstream with its depends_on.</summary>
    DoesNotApply,
    /// <summary>The suite fails on the change's own branch.</summary>
    Fails,
    /// <summary>The suite still passes without the change's library code: nothing tests it.</summary>
    Untested,
}

public sealed record VerifyResult(string Change, VerifyOutcome Outcome, string Detail);

/// <summary>
/// Proves each code change on its own, as upstream would get it: on <c>lake/change/NNNN-name</c> (upstream, its
/// depends_on, then it), the test suite passes; with the change's library code reverted (its files outside the
/// test directory), the suite fails or does not compile, so the change is what makes its tests pass. A change whose
/// record says <c>evidence: benchmark</c>, or that has no library code (tests only), is only required to pass.
/// </summary>
public static class VerifyCommand {

    public const string DefaultTestProject = "src/Parquet.Test/Parquet.Test.csproj";

    public static int Run(StackContext context, string head, IReadOnlyList<string> changes, bool list, string testProject,
        Func<string, int>? runTests = null) {
        Git git = context.Git;
        StackModel model = StackModel.Load(git, head, context.Upstream);
        List<StackCommit> code = model.Changes
            .Where(c => model.Records.TryGetValue(c.Number!, out ChangeRecord? r) && r.Kind == ChangeKind.Code)
            .Where(c => changes.Count == 0 || changes.Contains(c.Number!))
            .ToList();
        if(list) {
            // for a CI matrix: ["0002","0003",...]
            context.Out.WriteLine("[" + string.Join(",", code.Select(c => $"\"{c.Number}\"")) + "]");
            return ExitCodes.Ok;
        }
        foreach(string missing in changes.Where(n => code.All(c => c.Number != n))) {
            throw new StackException($"lake({missing}) is not a code change in the stack");
        }

        runTests ??= directory => DotnetTest(context, directory, testProject);
        string testDirectory = Path.GetDirectoryName(testProject)!.Replace('\\', '/') + "/";
        var results = code.Select(c => Verify(context, model, c, testDirectory, runTests)).ToList();

        context.Out.WriteLine();
        foreach(VerifyResult r in results) {
            context.Out.WriteLine($"lake({r.Change}) {r.Outcome.ToString().ToLowerInvariant()}: {r.Detail}");
        }
        bool ok = results.All(r => r.Outcome is VerifyOutcome.Proven or VerifyOutcome.Passes);
        return ok ? ExitCodes.Ok : ExitCodes.Problems;
    }

    static VerifyResult Verify(StackContext context, StackModel model, StackCommit change, string testDirectory, Func<string, int> runTests) {
        Git git = context.Git;
        string number = change.Number!;
        ChangeRecord record = model.Records[number];
        context.Out.WriteLine($"== lake({number}) {record.Title}");
        string path = Path.Combine(Path.GetTempPath(), "lake-verify-" + Guid.NewGuid().ToString("n")[..12]);
        git.Run("worktree", "add", "--quiet", "--detach", path, model.Upstream);
        try {
            var tree = new Git(path);
            IReadOnlyList<StackCommit> commits = model.WithDependencies(number);
            StackCommit? blocking = commits.FirstOrDefault(c => !BranchesCommand.Replay(tree, c));
            if(blocking != null) {
                return new VerifyResult(number, VerifyOutcome.DoesNotApply, $"'{blocking.Subject}' does not apply on upstream with its depends_on");
            }
            if(runTests(path) != 0) {
                return new VerifyResult(number, VerifyOutcome.Fails, "the suite fails on its own branch");
            }

            List<string> library = change.Files
                .Where(f => !f.StartsWith(testDirectory, StringComparison.Ordinal) && !f.StartsWith(ChangeRecord.Directory + "/", StringComparison.Ordinal))
                .ToList();
            if(record.Evidence == ChangeEvidence.Benchmark) {
                return new VerifyResult(number, VerifyOutcome.Passes, "passes on its own branch (evidence: benchmark)");
            }
            if(library.Count == 0) {
                return new VerifyResult(number, VerifyOutcome.Passes, "passes on its own branch (tests only)");
            }

            // Put the library files back as they were before this change (its dependencies stay).
            foreach(string file in library) {
                if(tree.Succeeds("cat-file", "-e", $"HEAD~1:{file}")) {
                    tree.Run("checkout", "--quiet", "HEAD~1", "--", file);
                } else {
                    tree.Run("rm", "--quiet", "-f", "--", file);
                }
            }
            return runTests(path) != 0
                ? new VerifyResult(number, VerifyOutcome.Proven, $"passes, and fails without its {library.Count} library file(s)")
                : new VerifyResult(number, VerifyOutcome.Untested, $"the suite still passes without its library file(s): {string.Join(", ", library)}");
        } finally {
            git.TryRun("worktree", "remove", "--force", path);
            git.TryRun("worktree", "prune");
        }
    }

    static int DotnetTest(StackContext context, string directory, string testProject) {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false };
        foreach(string arg in new[] { "test", testProject, "--configuration", "Release", "--nologo", "--verbosity", "quiet" }) {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start) ?? throw new StackException("dotnet did not start");
        process.WaitForExit();
        return process.ExitCode;
    }
}
