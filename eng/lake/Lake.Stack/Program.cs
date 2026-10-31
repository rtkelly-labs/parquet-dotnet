using System.CommandLine;
using Lake.Stack;
using Lake.Stack.Commands;

return Cli.Run(args, Console.Out, Console.Error);

namespace Lake.Stack {

    /// <summary>The command line. Each command's work is in Commands/; docs/maintaining.md is the guide.</summary>
    public static class Cli {

        public static int Run(string[] args, TextWriter output, TextWriter error) {
            var repo = new Option<string>("--repo") {
                Description = "The repository (default: the current directory)",
                DefaultValueFactory = _ => ".",
                Recursive = true,
            };
            var upstream = new Option<string>("--upstream") {
                Description = "Upstream's branch (default: $LAKE_UPSTREAM, else upstream/master)",
                DefaultValueFactory = _ => Environment.GetEnvironmentVariable("LAKE_UPSTREAM") ?? "upstream/master",
                Recursive = true,
            };
            var head = new Option<string>("--head") {
                Description = "The stack to read (default: HEAD)",
                DefaultValueFactory = _ => "HEAD",
            };

            StackContext Context(ParseResult parse) =>
                new(new Git(parse.GetValue(repo)!), parse.GetValue(upstream)!, output, error);

            var check = new Command("check", "Fail unless every commit above upstream follows the stack's rules") { head };
            check.SetAction(parse => Guard(error, () => CheckCommand.Run(Context(parse), parse.GetValue(head)!)));

            var status = new Command("status", "List the changes in the stack, with their status") { head };
            status.SetAction(parse => Guard(error, () => StatusCommand.Run(Context(parse), parse.GetValue(head)!)));

            var syncOnto = new Option<string?>("--onto") { Description = "Rebase onto this ref instead of upstream (a release tag, say)" };
            var noFetch = new Option<bool>("--no-fetch") { Description = "Do not fetch the upstream remote first" };
            var abortOnConflict = new Option<bool>("--abort-on-conflict") { Description = "Abort instead of leaving the rebase to resolve" };
            var sync = new Command("sync", "Only rebase the checked-out stack onto upstream, folding in fixups (rebaseline is the whole step)") { syncOnto, noFetch, abortOnConflict };
            sync.SetAction(parse => Guard(error, () => SyncCommand.Run(Context(parse), new SyncOptions(
                parse.GetValue(syncOnto), !parse.GetValue(noFetch), parse.GetValue(abortOnConflict)))));

            var rebaselineOnto = new Option<string?>("--onto") { Description = "Rebase onto this ref instead of upstream's branch (an upstream release tag, say)" };
            var resume = new Option<bool>("--continue") { Description = "Resume a rebaseline: after resolving a conflict or fixing a change, or to publish one" };
            var rebased = new Option<bool>("--rebased") { Description = "Start from a stack you already rebased onto upstream (with jj): check, test and stage it" };
            var abort = new Option<bool>("--abort") { Description = "Give up a rebaseline and put the stack branch back where it was" };
            var showStatus = new Option<bool>("--status") { Description = "Show where a rebaseline in progress stands, and its staged build's CI result" };
            var stage = new Option<string?>("--stage") { Description = "Push the verified stack to staging/<name> (a version such as v0.1.1) for CI to build" };
            var publishIt = new Option<bool>("--push") { Description = "Publish once the staged build is green: tag the old head, move the remote's stack branch, regenerate the change branches" };
            var skipCiCheck = new Option<bool>("--skip-ci-check") { Description = "Publish without a green staged build (only after checking CI yourself)" };
            var noTest = new Option<bool>("--no-test") { Description = $"Do not run {RebaselineCommand.DefaultTestProject}" };
            var rebaselineRemote = new Option<string>("--remote") { Description = "Default: origin", DefaultValueFactory = _ => "origin" };
            var rebaselineBranch = new Option<string>("--branch") { Description = "The stack branch (default: master)", DefaultValueFactory = _ => "master" };
            var rebaseline = new Command("rebaseline", "Move the stack onto a newer upstream, deliberately: rebase, verify, stage for CI, and publish only with --push") {
                rebaselineOnto, resume, rebased, abort, showStatus, stage, publishIt, skipCiCheck, noTest, rebaselineRemote, rebaselineBranch,
            };
            rebaseline.SetAction(parse => Guard(error, () => RebaselineCommand.Run(Context(parse), new RebaselineOptions(
                Onto: parse.GetValue(rebaselineOnto),
                Continue: parse.GetValue(resume),
                Rebased: parse.GetValue(rebased),
                Abort: parse.GetValue(abort),
                Status: parse.GetValue(showStatus),
                Stage: parse.GetValue(stage),
                Push: parse.GetValue(publishIt),
                SkipCiCheck: parse.GetValue(skipCiCheck),
                Test: !parse.GetValue(noTest),
                Remote: parse.GetValue(rebaselineRemote)!,
                Branch: parse.GetValue(rebaselineBranch)!))));

            var branchesOnto = new Option<string?>("--onto") { Description = "Build the branches from this ref instead of upstream" };
            var push = new Option<string?>("--push") { Description = "Force-push them to this remote, and delete its branches of retired changes" };
            var branches = new Command("branches", "Regenerate lake/change/NNNN-name: upstream plus one change and what it needs") { head, branchesOnto, push };
            branches.SetAction(parse => Guard(error, () => BranchesCommand.Run(Context(parse), parse.GetValue(head)!, parse.GetValue(branchesOnto), parse.GetValue(push))));

            var stack = new Option<string>("--onto") { Description = "The stack branch (default: master)", DefaultValueFactory = _ => "master" };
            var apply = new Option<bool>("--apply") { Description = "Move them (without it, only show what would move)" };
            var all = new Option<bool>("--all") { Description = "Every local branch but the stack and the generated ones" };
            var names = new Argument<string[]>("branch") { Arity = ArgumentArity.ZeroOrMore, Description = "Branches to move" };
            var rewire = new Command("rewire", "Move other branches' own commits onto the current stack") { stack, apply, all, names };
            rewire.SetAction(parse => Guard(error, () => RewireCommand.Run(Context(parse), parse.GetValue(stack)!, parse.GetValue(apply), parse.GetValue(all), parse.GetValue(names) ?? [])));

            var from = new Option<string>("--from") { Description = "The remote branch's current head (the old stack)", Required = true };
            var to = new Option<string>("--to") { Description = "The rebased stack to move it to", Required = true };
            var remote = new Option<string>("--remote") { Description = "Default: origin", DefaultValueFactory = _ => "origin" };
            var branch = new Option<string>("--branch") { Description = "Default: master", DefaultValueFactory = _ => "master" };
            var promote = new Command("promote", "Tag the old stack head and move the remote's stack branch to the new one") { from, to, remote, branch };
            promote.SetAction(parse => Guard(error, () => PromoteCommand.Run(Context(parse), new PromoteOptions(
                parse.GetValue(from)!, parse.GetValue(to)!, parse.GetValue(remote)!, parse.GetValue(branch)!))));

            var verifyList = new Option<bool>("--list") { Description = "Only print the code changes as a JSON array (a CI matrix)" };
            var testProject = new Option<string>("--test-project") { Description = $"Default: {VerifyCommand.DefaultTestProject}", DefaultValueFactory = _ => VerifyCommand.DefaultTestProject };
            var changeNumbers = new Argument<string[]>("change") { Arity = ArgumentArity.ZeroOrMore, Description = "Changes to verify (NNNN); all code changes when none" };
            var verify = new Command("verify", "Prove each code change alone: its branch passes the suite, and fails without its library code") { head, verifyList, testProject, changeNumbers };
            verify.SetAction(parse => Guard(error, () => VerifyCommand.Run(Context(parse), parse.GetValue(head)!, parse.GetValue(changeNumbers) ?? [], parse.GetValue(verifyList), parse.GetValue(testProject)!)));

            var title = new Option<string>("--title") { Description = "The pull request's title", Required = true };
            var prBase = new Option<string>("--base") { Description = "The pull request's base commit", Required = true };
            var checkPr = new Command("check-pr", "Check a pull request into the stack: its title names the change, and a new change brings its record") { title, prBase, head };
            checkPr.SetAction(parse => Guard(error, () => CheckPrCommand.Run(Context(parse), parse.GetValue(title)!, parse.GetValue(prBase)!, parse.GetValue(head)!)));

            var root = new RootCommand("Keeps Lake.Parquet's change stack on top of upstream (docs/maintaining.md)") {
                repo, upstream, check, checkPr, status, verify, rebaseline, sync, branches, rewire, promote,
            };
            return root.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        }

        /// <summary>Turns the failures a user can act on into a message and exit code 2, not a stack trace.</summary>
        static int Guard(TextWriter error, Func<int> command) {
            try {
                return command();
            } catch(Exception e) when(e is StackException or GitException or RecordException) {
                error.WriteLine("error: " + e.Message);
                return ExitCodes.Error;
            }
        }
    }
}
