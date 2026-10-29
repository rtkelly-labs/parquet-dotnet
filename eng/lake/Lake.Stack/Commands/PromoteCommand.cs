namespace Lake.Stack.Commands;

public sealed record PromoteOptions(
    string From,
    string To,
    string Remote = "origin",
    string Branch = "master",
    DateTimeOffset? Now = null);

/// <summary>
/// Moves the stack branch on the remote from one stack head to its rebased successor: refuses a
/// successor that breaks the stack's rules, tags the old head <c>lake/stack/&lt;date&gt;-&lt;sha&gt;</c>
/// so it stays reachable, force-pushes with a lease on the old head (so a commit merged meanwhile is
/// never lost), and deletes <c>lake/next</c>.
/// </summary>
public static class PromoteCommand {

    public const string TagPrefix = "lake/stack/";
    public const string NextBranch = "lake/next";

    public static int Run(StackContext context, PromoteOptions options) {
        Git git = context.Git;
        string from = git.Resolve(options.From);
        string to = git.Resolve(options.To);
        if(from == to) {
            context.Out.WriteLine($"{options.Branch} is already at {to[..10]}");
            return ExitCodes.Ok;
        }

        IReadOnlyList<string> problems = StackModel.Load(git, to, context.Upstream).Problems();
        if(problems.Count > 0) {
            foreach(string problem in problems) {
                context.Error.WriteLine(problem);
            }
            context.Error.WriteLine($"not promoting {to[..10]}: it breaks the stack's rules");
            return ExitCodes.Problems;
        }

        string tag = TagFor(from, options.Now ?? DateTimeOffset.UtcNow);
        git.Run("tag", tag, from);
        git.Run("push", "--quiet", options.Remote, $"refs/tags/{tag}");

        GitResult push = git.TryRun("push", "--quiet", $"--force-with-lease=refs/heads/{options.Branch}:{from}", options.Remote, $"{to}:refs/heads/{options.Branch}");
        if(push.ExitCode != 0) {
            context.Error.WriteLine($"{options.Remote}/{options.Branch} is no longer at {from[..10]} (something was merged meanwhile): nothing moved; the next sync includes it");
            context.Error.WriteLine(push.Error.Trim());
            return ExitCodes.Problems;
        }
        git.TryRun("push", "--quiet", options.Remote, "--delete", NextBranch);
        context.Out.WriteLine($"{options.Remote}/{options.Branch}: {from[..10]} (tagged {tag}) -> {to[..10]}");
        return ExitCodes.Ok;
    }

    public static string TagFor(string head, DateTimeOffset now) => $"{TagPrefix}{now.UtcDateTime:yyyyMMdd-HHmmss}-{head[..12]}";
}
