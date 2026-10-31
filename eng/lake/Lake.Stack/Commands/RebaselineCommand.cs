using System.Diagnostics;

namespace Lake.Stack.Commands;

public sealed record RebaselineOptions(
    string? Onto = null,
    bool Continue = false,
    bool Abort = false,
    bool Status = false,
    string? Stage = null,
    bool Push = false,
    bool SkipCiCheck = false,
    bool Test = true,
    string Remote = "origin",
    string Branch = "master",
    Func<StackContext, int>? RunTests = null,
    CiStatus? Ci = null);

/// <summary>
/// Moves the stack onto a newer upstream, as a deliberate step the maintainer takes: upstream changes
/// can break the changes on top, so nothing does this on a schedule. In order, stopping at the first
/// thing that needs a person:
/// <list type="number">
/// <item>rebases the checked-out stack branch onto upstream (folding in fixups) and reports what moved;</item>
/// <item>on a conflict, stops; resolve it, <c>git rebase --continue</c>, then <c>rebaseline --continue</c>;</item>
/// <item>checks the stack's rules and runs the test suite on the result;</item>
/// <item>with <c>--stage &lt;name&gt;</c>: pushes the result to <c>staging/&lt;name&gt;</c>, where CI builds it as
/// it would build master;</item>
/// <item>with <c>--push</c> only, and only once the staged commit is the one being published and its
/// <c>lake-ci</c> check passed: tags the old head, moves the remote's stack branch with a lease on it,
/// regenerates the change branches and deletes the staging branch.</item>
/// </list>
/// The old head and the staging branch are remembered in git config until the rebaseline is pushed or
/// aborted.
/// </summary>
public static class RebaselineCommand {

    const string FromKey = "lake.rebaseline.from";
    const string OntoKey = "lake.rebaseline.onto";
    const string VerifiedKey = "lake.rebaseline.verified";
    const string StageKey = "lake.rebaseline.stage";

    public const string StagingPrefix = "staging/";

    public const string DefaultTestProject = "src/Parquet.Test/Parquet.Test.csproj";

    public static int Run(StackContext context, RebaselineOptions options) {
        Git git = context.Git;
        if(options.Abort) {
            return Abort(context, options);
        }
        if(options.Status) {
            return Status(context, options);
        }

        string from;
        string onto;
        if(options.Continue) {
            from = Config(git, FromKey) ?? throw new StackException("no rebaseline in progress: start one with 'rebaseline'");
            onto = Config(git, OntoKey) ?? context.Upstream;
            if(RebaseInProgress(git)) {
                throw new StackException("the rebase is not finished: resolve the conflict, 'git add', 'git rebase --continue', then 'rebaseline --continue'");
            }
        } else {
            if(Config(git, FromKey) is string pending) {
                throw new StackException($"a rebaseline from {pending[..10]} is in progress: finish it with 'rebaseline --continue [--push]', or 'rebaseline --abort'");
            }
            if(git.CurrentBranchOrCommit() != options.Branch) {
                throw new StackException($"check out {options.Branch} first: a rebaseline moves the stack branch");
            }
            git.Run("fetch", "--quiet", options.Remote);
            from = git.Resolve("HEAD");
            string? published = git.TryResolve($"{options.Remote}/{options.Branch}");
            if(published != from) {
                throw new StackException($"{options.Branch} is not {options.Remote}/{options.Branch}: push or reset it first, so the rebaseline starts from what is published");
            }
            onto = options.Onto ?? context.Upstream;
            git.Run("config", FromKey, from);
            git.Run("config", OntoKey, onto);

            SyncResult sync = SyncCommand.Sync(context, new SyncOptions(onto, Fetch: true));
            context.Out.Write(SyncCommand.Report(context, sync, onto));
            if(sync.Conflict != null) {
                context.Out.WriteLine();
                context.Out.WriteLine("Then: rebaseline --continue (or rebaseline --abort to put everything back).");
                return ExitCodes.Problems;
            }
            if(!sync.Changed) {
                Clear(git);
                context.Out.WriteLine("Nothing to do: the stack is already on upstream with nothing to fold in.");
                return ExitCodes.Ok;
            }
        }

        string head = git.Resolve("HEAD");
        if(options.Continue) {
            context.Out.WriteLine($"Rebaseline from {from[..10]} onto {onto}: now {head[..10]}.");
        }

        if(CheckCommand.Run(context, "HEAD") != ExitCodes.Ok) {
            context.Error.WriteLine("Fix the stack (a fixup! commit, then git rebase -i --autosquash), then rebaseline --continue.");
            return ExitCodes.Problems;
        }
        if(options.Test && Config(git, VerifiedKey) != head) {
            int tests = (options.RunTests ?? RunTestSuite)(context);
            if(tests != 0) {
                context.Error.WriteLine("The tests fail on the rebased stack: upstream's changes broke a change on top. Fix it in that change");
                context.Error.WriteLine("(git commit --fixup=<its commit>, git rebase -i --autosquash), then rebaseline --continue.");
                return ExitCodes.Problems;
            }
            git.Run("config", VerifiedKey, head);
        }

        string? stage = options.Stage is string name ? StagingBranch(name) : Config(git, StageKey);
        if(stage != null) {
            Stage(context, options, stage, head);
        }

        if(!options.Push) {
            context.Out.WriteLine();
            context.Out.WriteLine($"Ready, not published: {options.Branch} is {head[..10]} locally, {options.Remote}/{options.Branch} is still {from[..10]}.");
            context.Out.WriteLine(stage == null
                ? "Next: stage it for CI with rebaseline --continue --stage <name> (staging/<name>, a version such as v0.1.1)."
                : $"Next: wait for {GitHubCi.Check} on {stage} (rebaseline --status), then publish with rebaseline --continue --push.");
            return ExitCodes.Ok;
        }

        if(options.SkipCiCheck) {
            context.Error.WriteLine($"warning: publishing without a green staged build (--skip-ci-check)");
        } else if(!StagedAndGreen(context, options, stage, head)) {
            return ExitCodes.Problems;
        }

        int promoted = PromoteCommand.Run(context, new PromoteOptions(from, head, options.Remote, options.Branch));
        if(promoted != ExitCodes.Ok) {
            return promoted;
        }
        int branches = BranchesCommand.Run(context, "HEAD", onto, options.Remote);
        if(stage != null) {
            git.TryRun("push", "--quiet", options.Remote, "--delete", stage);
            context.Out.WriteLine($"{options.Remote}/{stage} deleted: {options.Branch} has it now");
        }
        Clear(git);
        return branches;
    }

    /// <summary><c>v0.1.1</c> or <c>staging/v0.1.1</c> -> <c>staging/v0.1.1</c>.</summary>
    public static string StagingBranch(string name) =>
        name.StartsWith(StagingPrefix, StringComparison.Ordinal) ? name : StagingPrefix + name;

    /// <summary>Pushes the rebased stack to its staging branch, where CI builds it, unless it is already there.</summary>
    static void Stage(StackContext context, RebaselineOptions options, string stage, string head) {
        Git git = context.Git;
        git.Run("config", StageKey, stage);
        string? staged = RemoteHead(git, options.Remote, stage);
        if(staged == head) {
            return;
        }
        git.Run("push", "--quiet", "--force", options.Remote, $"{head}:refs/heads/{stage}");
        context.Out.WriteLine(staged == null
            ? $"Staged {head[..10]} as {options.Remote}/{stage}: CI is building it."
            : $"Re-staged {options.Remote}/{stage}: {staged[..10]} -> {head[..10]}; CI is building it again.");
    }

    /// <summary>True when the staging branch holds exactly this head and its lake-ci check passed.</summary>
    static bool StagedAndGreen(StackContext context, RebaselineOptions options, string? stage, string head) {
        if(stage == null) {
            context.Error.WriteLine("Not staged: stage it first (rebaseline --continue --stage <name>), and publish once its CI is green.");
            return false;
        }
        string? staged = RemoteHead(context.Git, options.Remote, stage);
        if(staged != head) {
            context.Error.WriteLine($"{options.Remote}/{stage} is {staged?[..10] ?? "missing"}, not {head[..10]}: stage this head (rebaseline --continue) and let CI build it.");
            return false;
        }
        CiState state = (options.Ci ?? GitHubCi.For(context.Git, options.Remote))(head, GitHubCi.Check);
        if(state != CiState.Success) {
            context.Error.WriteLine($"{GitHubCi.Check} on {stage} ({head[..10]}) is {state.ToString().ToLowerInvariant()}: publish once it has passed.");
            return false;
        }
        context.Out.WriteLine($"{GitHubCi.Check} passed on {stage} ({head[..10]}).");
        return true;
    }

    static int Status(StackContext context, RebaselineOptions options) {
        Git git = context.Git;
        string from = Config(git, FromKey) ?? throw new StackException("no rebaseline in progress");
        string head = git.Resolve("HEAD");
        context.Out.WriteLine($"Rebaseline from {from[..10]} onto {Config(git, OntoKey)}: now {head[..10]}{(RebaseInProgress(git) ? ", rebase in progress" : "")}.");
        context.Out.WriteLine($"Tested locally: {(Config(git, VerifiedKey) == head ? "yes" : "no")}.");
        if(Config(git, StageKey) is not string stage) {
            context.Out.WriteLine("Not staged.");
            return ExitCodes.Ok;
        }
        string? staged = RemoteHead(git, options.Remote, stage);
        if(staged != head) {
            context.Out.WriteLine($"{options.Remote}/{stage} is {staged?[..10] ?? "missing"}, not this head: rebaseline --continue re-stages it.");
            return ExitCodes.Ok;
        }
        CiState state = (options.Ci ?? GitHubCi.For(git, options.Remote))(head, GitHubCi.Check);
        context.Out.WriteLine($"{options.Remote}/{stage}: {GitHubCi.Check} is {state.ToString().ToLowerInvariant()}.");
        return ExitCodes.Ok;
    }

    static string? RemoteHead(Git git, string remote, string branch) {
        IReadOnlyList<string> lines = git.Lines("ls-remote", "--heads", remote, "refs/heads/" + branch);
        return lines.Count == 0 ? null : lines[0].Split('\t')[0];
    }

    static int Abort(StackContext context, RebaselineOptions options) {
        Git git = context.Git;
        string from = Config(git, FromKey) ?? throw new StackException("no rebaseline in progress");
        if(RebaseInProgress(git)) {
            git.Run("rebase", "--abort");
        }
        if(Config(git, StageKey) is string stage && RemoteHead(git, options.Remote, stage) != null) {
            git.TryRun("push", "--quiet", options.Remote, "--delete", stage);
            context.Out.WriteLine($"{options.Remote}/{stage} deleted.");
        }
        git.Run("reset", "--quiet", "--hard", from);
        Clear(git);
        context.Out.WriteLine($"Aborted: back at {from[..10]}.");
        return ExitCodes.Ok;
    }

    /// <summary>Runs the library's test suite, streaming its output.</summary>
    static int RunTestSuite(StackContext context) {
        context.Out.WriteLine($"Running {DefaultTestProject} on the rebased stack…");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = context.Git.Directory, UseShellExecute = false };
        foreach(string arg in new[] { "test", DefaultTestProject, "--configuration", "Release" }) {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start) ?? throw new StackException("dotnet did not start");
        process.WaitForExit();
        return process.ExitCode;
    }

    static bool RebaseInProgress(Git git) =>
        Directory.Exists(git.GitPath("rebase-merge")) || Directory.Exists(git.GitPath("rebase-apply"));

    static string? Config(Git git, string key) {
        GitResult result = git.TryRun("config", "--get", key);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    static void Clear(Git git) {
        foreach(string key in new[] { FromKey, OntoKey, VerifiedKey, StageKey }) {
            git.TryRun("config", "--unset", key);
        }
    }
}
