namespace Lake.Stack.Commands;

/// <summary>One published version of a history: its record commit, the commit published, and when.</summary>
public sealed record HistoryVersion(string Record, string Published, string Date, string Subject);

/// <summary>
/// Keeps every published version of the stack and of each change branch, which publishing otherwise replaces:
/// <c>lake/history/stack</c> and <c>lake/history/NNNN-name</c> get one commit per publication, with the published
/// commit's tree, the previous record as first parent and the published commit as second. The history only ever
/// fast-forwards, so it needs no force-push and no tags, and every version stays reachable.
/// <c>git log --first-parent lake/history/NNNN-name</c> lists the versions; <c>git range-diff A...B</c> between two
/// published commits shows how a change moved. <see cref="BranchesCommand"/> and <see cref="PromoteCommand"/>
/// record as they publish (Lake.NET docs/lake-parquet/maintaining.md, "History of published changes").
/// </summary>
public static class HistoryCommand {

    public const string Prefix = "lake/history/";
    public const string Stack = "stack";
    const string RecordPrefix = "lake-history: ";

    /// <summary>The commit a record published: its last parent.</summary>
    static string PublishedBy(Git git, string record) =>
        git.Run("log", "-1", "--format=%P", record).Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

    /// <summary>
    /// Records <paramref name="published"/> as the newest version of <paramref name="name"/>, after the remote's
    /// history when <paramref name="remote"/> is given (and pushes it there). Returns the record, or null when it
    /// already is the newest version.
    /// </summary>
    public static string? Record(Git git, string name, string published, string? remote) {
        string branch = "refs/heads/" + Prefix + name;
        if(remote != null) {
            // the remote's history first, so versions recorded from another clone are kept
            git.TryRun("fetch", "--quiet", remote, $"+{branch}:{branch}");
        }
        published = git.Resolve(published);
        string? previous = git.TryResolve(branch);
        if(previous != null && PublishedBy(git, previous) == published) {
            return null;
        }

        string subject = git.Run("log", "-1", "--format=%s", published);
        List<string> args = ["commit-tree", published + "^{tree}", "-m", $"{RecordPrefix}{name} at {published[..12]}\n\n{subject}"];
        if(previous != null) {
            args.AddRange(["-p", previous]);
        }
        args.AddRange(["-p", published]);
        string record = git.Run(args, null);
        git.Run("update-ref", branch, record, previous ?? new string('0', 40));
        if(remote != null) {
            git.Run("push", "--quiet", remote, $"{branch}:{branch}");   // a fast-forward: never forced
        }
        return record;
    }

    /// <summary>The recorded versions of <paramref name="name"/>, newest first.</summary>
    public static IReadOnlyList<HistoryVersion> Versions(Git git, string name) {
        string branch = "refs/heads/" + Prefix + name;
        if(git.TryResolve(branch) == null) {
            return [];
        }
        // record, date and parents, then the subject; the walk ends where the records do (the first record's first
        // parent is the first version published, whose own history is not part of this one)
        var versions = new List<HistoryVersion>();
        foreach(string line in git.Lines("log", "--first-parent", "--format=%H %cI %P%x09%s", branch)) {
            string[] parts = line.Split('\t', 2);
            if(parts.Length < 2 || !parts[1].StartsWith(RecordPrefix, StringComparison.Ordinal)) {
                break;
            }
            string[] f = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            versions.Add(new HistoryVersion(f[0], f[^1], f[1], git.Run("log", "-1", "--format=%s", f[^1])));
        }
        return versions;
    }

    /// <summary><c>history record</c>: the remote's stack branch and every change branch, as they are now.</summary>
    public static int RecordAll(StackContext context, string remote, string branch) {
        Git git = context.Git;
        git.Run("fetch", "--quiet", remote, branch);
        int recorded = 0;
        if(Record(git, Stack, $"refs/remotes/{remote}/{branch}", remote) != null) {
            recorded++;
        }
        foreach(string change in git.Lines("for-each-ref", "--format=%(refname:short)", "refs/heads/" + BranchesCommand.Prefix)) {
            if(Record(git, change[BranchesCommand.Prefix.Length..], change, remote) != null) {
                recorded++;
            }
        }
        context.Out.WriteLine($"{recorded} new version(s) recorded under {Prefix}");
        return ExitCodes.Ok;
    }

    /// <summary><c>history &lt;name&gt;</c>: the versions of the stack or of one change, newest first.</summary>
    public static int Show(StackContext context, string name) {
        IReadOnlyList<HistoryVersion> versions = Versions(context.Git, name);
        if(versions.Count == 0) {
            context.Error.WriteLine($"no history for '{name}' (lake/history/{name})");
            return ExitCodes.Problems;
        }
        foreach(HistoryVersion v in versions) {
            context.Out.WriteLine($"{v.Date}  {v.Published[..12]}  {v.Subject}");
        }
        if(versions.Count > 1) {
            context.Out.WriteLine($"how the latest differs from the one before: git range-diff {versions[1].Published[..12]}...{versions[0].Published[..12]}");
        }
        return ExitCodes.Ok;
    }
}
