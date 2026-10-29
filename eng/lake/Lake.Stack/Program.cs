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
            var abortOnConflict = new Option<bool>("--abort-on-conflict") { Description = "Abort instead of leaving the rebase to resolve (CI)" };
            var report = new Option<string?>("--report") { Description = "Also write the Markdown report to this file" };
            var outputs = new Option<string?>("--outputs") { Description = "Append changed/before/after/upstream/conflict as key=value lines (GitHub's $GITHUB_OUTPUT)" };
            var publish = new Option<string?>("--publish") { Description = "Force-push a changed stack to lake/next on this remote, for its build" };
            var sync = new Command("sync", "Rebase the checked-out stack onto upstream, folding in fixups") { syncOnto, noFetch, abortOnConflict, report, outputs, publish };
            sync.SetAction(parse => Guard(error, () => SyncCommand.Run(Context(parse), new SyncOptions(
                parse.GetValue(syncOnto), !parse.GetValue(noFetch), parse.GetValue(abortOnConflict), parse.GetValue(report), parse.GetValue(outputs), parse.GetValue(publish)))));

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

            var title = new Option<string>("--title") { Description = "The pull request's title", Required = true };
            var prBase = new Option<string>("--base") { Description = "The pull request's base commit", Required = true };
            var checkPr = new Command("check-pr", "Check a pull request into the stack: its title names the change, and a new change brings its record") { title, prBase, head };
            checkPr.SetAction(parse => Guard(error, () => CheckPrCommand.Run(Context(parse), parse.GetValue(title)!, parse.GetValue(prBase)!, parse.GetValue(head)!)));

            var root = new RootCommand("Keeps Lake.Parquet's change stack on top of upstream (docs/maintaining.md)") {
                repo, upstream, check, checkPr, status, sync, branches, rewire, promote,
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
