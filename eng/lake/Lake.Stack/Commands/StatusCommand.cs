namespace Lake.Stack.Commands;

/// <summary>Lists the stack: each change, its status, how many of upstream's files it touches, its title.</summary>
public static class StatusCommand {

    public static int Run(StackContext context, string head) {
        StackModel model = StackModel.Load(context.Git, head, context.Upstream);
        context.Out.WriteLine($"on {context.Upstream} at {model.Base[..10]}, {model.Commits.Count} commits");
        context.Out.WriteLine($"{"change",-7}{"kind",-9}{"status",-19}{"files",-7}title");
        foreach(StackCommit commit in model.Commits) {
            if(commit.Number is not string number) {
                context.Out.WriteLine($"{"-",-7}{"",-9}{"pending",-19}{commit.Files.Count,-7}{commit.Subject}");
                continue;
            }
            model.Records.TryGetValue(number, out ChangeRecord? record);
            string kind = record?.Kind.ToString().ToLowerInvariant() ?? "?";
            string status = record == null ? "no record" : Kebab(record.Status);
            if(commit.OnlyRecords && record?.Kind == ChangeKind.Code) {
                status += " (code gone)";
            }
            int files = commit.Files.Count(f => !f.StartsWith(ChangeRecord.Directory + "/", StringComparison.Ordinal));
            context.Out.WriteLine($"{number,-7}{kind,-9}{status,-19}{files,-7}{record?.Title ?? commit.Subject}");
        }
        return ExitCodes.Ok;
    }

    public static string Kebab(ChangeStatus status) => status switch {
        ChangeStatus.ForkOnly => "fork-only",
        ChangeStatus.ProposedUpstream => "proposed-upstream",
        ChangeStatus.Upstreamed => "upstreamed",
        ChangeStatus.Superseded => "superseded",
        _ => status.ToString(),
    };
}
