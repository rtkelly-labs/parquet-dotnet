using System.Diagnostics;

namespace Lake.Stack.Commands;

public sealed record RebaselineOptions(
    string? Onto = null,
    bool Continue = false,
    bool Abort = false,
    bool Push = false,
    bool Test = true,
    string Remote = "origin",
    string Branch = "master",
    Func<StackContext, int>? RunTests = null);

/// <summary>
/// Moves the stack onto a newer upstream, as a deliberate step the maintainer takes: upstream changes
/// can break the changes on top, so nothing does this on a schedule. In order, stopping at the first
/// thing that needs a person:
/// <list type="number">
/// <item>rebases the checked-out stack branch onto upstream (folding in fixups) and reports what moved;</item>
/// <item>on a conflict, stops; resolve it, <c>git rebase --continue</c>, then <c>rebaseline --continue</c>;</item>
/// <item>checks the stack's rules and runs the test suite on the result;</item>
/// <item>with <c>--push</c> only: tags the old head, moves the remote's stack branch with a lease on it,
/// and regenerates the change branches. Without it, the rebased stack stays local for you to look at.</item>
/// </list>
/// The old head is remembered in git config until the rebaseline is pushed or aborted.
/// </summary>
public static class RebaselineCommand {

    const string FromKey = "lake.rebaseline.from";
    const string OntoKey = "lake.rebaseline.onto";
    const string VerifiedKey = "lake.rebaseline.verified";

    public const string DefaultTestProject = "src/Parquet.Test/Parquet.Test.csproj";

    public static int Run(StackContext context, RebaselineOptions options) {
        Git git = context.Git;
        if(options.Abort) {
            return Abort(context);
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

        if(!options.Push) {
            context.Out.WriteLine();
            context.Out.WriteLine($"Ready, not published: {options.Branch} is {head[..10]} locally, {options.Remote}/{options.Branch} is still {from[..10]}.");
            context.Out.WriteLine("Look it over (git range-diff, the CI build of a pushed branch), then publish with: rebaseline --continue --push");
            return ExitCodes.Ok;
        }

        int promoted = PromoteCommand.Run(context, new PromoteOptions(from, head, options.Remote, options.Branch));
        if(promoted != ExitCodes.Ok) {
            return promoted;
        }
        int branches = BranchesCommand.Run(context, "HEAD", onto, options.Remote);
        Clear(git);
        return branches;
    }

    static int Abort(StackContext context) {
        Git git = context.Git;
        string from = Config(git, FromKey) ?? throw new StackException("no rebaseline in progress");
        if(RebaseInProgress(git)) {
            git.Run("rebase", "--abort");
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
        foreach(string key in new[] { FromKey, OntoKey, VerifiedKey }) {
            git.TryRun("config", "--unset", key);
        }
    }
}
