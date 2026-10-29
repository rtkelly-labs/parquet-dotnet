using System.Text;

namespace Lake.Stack.Commands;

public sealed record SyncOptions(
    string? Onto = null,
    bool Fetch = true,
    bool AbortOnConflict = false,
    string? ReportPath = null,
    string? OutputsPath = null,
    string? PublishTo = null);

public sealed record SyncResult(
    string Before,
    string After,
    string OldBase,
    string NewBase,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> CodeGone,
    SyncConflict? Conflict) {

    public bool Changed => Conflict == null && Before != After;
}

public sealed record SyncConflict(string? Commit, string? Subject, IReadOnlyList<string> Files, string Message);

/// <summary>
/// Rebases the checked-out stack onto upstream: upstream's commits stay as they are, the stack's are
/// replayed on top, <c>fixup!</c> commits are folded into their change, and changes upstream already
/// has are dropped. Reports what moved, what upstream took, and the range-diff.
/// </summary>
public static class SyncCommand {

    static readonly Dictionary<string, string> NonInteractive = new() {
        // Interactive only so that --autosquash applies; the todo list and messages are taken as they are.
        ["GIT_SEQUENCE_EDITOR"] = ":",
        ["GIT_EDITOR"] = ":",
    };

    public static int Run(StackContext context, SyncOptions options) {
        SyncResult result = Sync(context, options);
        if(result.Changed && options.PublishTo != null) {
            // Where the build runs before the stack branch is moved (PromoteCommand).
            context.Git.Run("push", "--quiet", "--force", options.PublishTo, $"{result.After}:refs/heads/{PromoteCommand.NextBranch}");
        }
        string report = Report(context, result, options.Onto ?? context.Upstream);
        context.Out.Write(report);
        if(options.ReportPath != null) {
            File.WriteAllText(options.ReportPath, report);
        }
        if(options.OutputsPath != null) {
            File.AppendAllText(options.OutputsPath,
                $"changed={(result.Changed ? "true" : "false")}\nbefore={result.Before}\nafter={result.After}\nupstream={result.NewBase}\nconflict={(result.Conflict != null ? "true" : "false")}\n");
        }
        return result.Conflict == null ? ExitCodes.Ok : ExitCodes.Problems;
    }

    public static SyncResult Sync(StackContext context, SyncOptions options) {
        Git git = context.Git;
        string onto = options.Onto ?? context.Upstream;
        if(Directory.Exists(git.GitPath("rebase-merge")) || Directory.Exists(git.GitPath("rebase-apply"))) {
            throw new StackException("a rebase is already in progress: finish it (git rebase --continue) or abort it first");
        }
        if(!git.IsClean()) {
            throw new StackException("the working tree has changes: commit or stash them first");
        }
        if(options.Fetch) {
            context.FetchRemoteOf(onto);
        }

        string ontoSha = git.Resolve(onto);
        string before = git.Resolve("HEAD");
        StackModel old = StackModel.Load(git, before, ontoSha);

        // rerere records every conflict resolution, so a conflict resolved once resolves itself next time.
        git.Run("config", "rerere.enabled", "true");
        git.Run("config", "rerere.autoUpdate", "true");
        GitResult rebase = git.TryRun(["rebase", "--interactive", "--autosquash", "--empty=drop", ontoSha], NonInteractive);
        if(rebase.ExitCode != 0) {
            SyncConflict conflict = DescribeConflict(git, rebase);
            if(options.AbortOnConflict) {
                git.TryRun("rebase", "--abort");
            }
            return new SyncResult(before, before, old.Base, ontoSha, [], [], conflict);
        }

        string after = git.Resolve("HEAD");
        StackModel now = StackModel.Load(git, after, ontoSha);
        var stillThere = now.Changes.Select(c => c.Number!).ToHashSet();
        List<string> dropped = old.Changes.Select(c => c.Number!).Where(n => !stillThere.Contains(n)).ToList();
        List<string> codeGone = now.Changes
            .Where(c => c.OnlyRecords && now.Records.TryGetValue(c.Number!, out ChangeRecord? r) && r.Kind == ChangeKind.Code)
            .Select(c => c.Number!)
            .ToList();
        return new SyncResult(before, after, old.Base, now.Base, dropped, codeGone, null);
    }

    static SyncConflict DescribeConflict(Git git, GitResult rebase) {
        IReadOnlyList<string> files = git.Lines("diff", "--name-only", "--diff-filter=U");
        string? commit = null;
        string stopped = git.GitPath("rebase-merge/stopped-sha");
        string done = git.GitPath("rebase-merge/done");
        if(File.Exists(stopped)) {
            commit = File.ReadAllText(stopped).Trim();
        } else if(File.Exists(done)) {
            // The last line of the done list is the pick that stopped: "pick <sha> <subject>".
            string? last = File.ReadAllLines(done).LastOrDefault(l => l.StartsWith("pick ", StringComparison.Ordinal) || l.StartsWith("fixup ", StringComparison.Ordinal));
            commit = last?.Split(' ', 3)[1];
        }
        string? subject = commit != null ? git.TryRun("log", "-1", "--format=%s", commit).Output.Trim() : null;
        return new SyncConflict(commit, subject, files, (rebase.Error + rebase.Output).Trim());
    }

    public static string Report(StackContext context, SyncResult result, string onto) {
        var report = new StringBuilder();
        if(result.Conflict is SyncConflict conflict) {
            report.AppendLine($"## The rebase onto `{onto}` stopped");
            report.AppendLine();
            report.AppendLine(conflict.Commit != null
                ? $"At {conflict.Commit[..10]} {conflict.Subject}."
                : "Before any commit could be replayed.");
            if(conflict.Files.Count > 0) {
                report.AppendLine();
                report.AppendLine("Conflicting files:");
                foreach(string file in conflict.Files) {
                    report.AppendLine($"- `{file}`");
                }
            }
            report.AppendLine();
            report.AppendLine("Resolve it in that change's own commit, keeping both intents, then `git rebase --continue` (docs/maintaining.md, \"By hand: when the sync stops\").");
            report.AppendLine();
            report.AppendLine("```");
            report.AppendLine(conflict.Message);
            report.AppendLine("```");
            return report.ToString();
        }

        Git git = context.Git;
        report.AppendLine($"## Rebased onto `{onto}`");
        report.AppendLine();
        int newUpstream = int.Parse(git.Run("rev-list", "--count", $"{result.OldBase}..{result.NewBase}"));
        report.AppendLine($"Upstream: {result.OldBase[..10]} -> {result.NewBase[..10]}, {newUpstream} new upstream commit(s).");
        report.AppendLine(result.Changed
            ? $"Stack: {result.Before[..10]} -> {result.After[..10]}."
            : "Stack: unchanged (already on upstream, nothing to fold in).");
        if(result.Dropped.Count > 0) {
            report.AppendLine($"Dropped, because upstream has the whole commit: {string.Join(", ", result.Dropped)}. Their records went with them.");
        }
        if(result.CodeGone.Count > 0) {
            report.AppendLine($"Only the record is left, because upstream has the code: {string.Join(", ", result.CodeGone)}. Retire them.");
        }
        if(result.Changed) {
            report.AppendLine();
            report.AppendLine("```");
            report.AppendLine(git.TryRun("range-diff", "--no-color", $"{result.OldBase}..{result.Before}", $"{result.NewBase}..{result.After}").Output.TrimEnd());
            report.AppendLine("```");
        }
        return report.ToString();
    }
}
