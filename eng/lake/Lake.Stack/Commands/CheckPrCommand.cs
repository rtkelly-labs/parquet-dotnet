namespace Lake.Stack.Commands;

/// <summary>
/// Checks a pull request into the stack. It is squash-merged, so its own commits are not the stack's;
/// its title becomes the one commit and must name the change, and a new change must bring its record.
/// </summary>
public static class CheckPrCommand {

    public static int Run(StackContext context, string title, string @base, string head) {
        IReadOnlyList<string> problems = Problems(context.Git, title, @base, head);
        foreach(string problem in problems) {
            context.Error.WriteLine(problem);
        }
        if(problems.Count == 0) {
            context.Out.WriteLine($"ok: '{title}'");
        }
        return problems.Count == 0 ? ExitCodes.Ok : ExitCodes.Problems;
    }

    public static IReadOnlyList<string> Problems(Git git, string title, string @base, string head) {
        if(Subjects.FixupNumber(title) != null) {
            return [];
        }
        if(Subjects.ChangeNumber(title) is not string number) {
            return ["the title becomes the squash commit: 'lake(NNNN): title' for a change, 'fixup! lake(NNNN): title' for a fix to one"];
        }
        IReadOnlyList<string> changed = git.Lines("diff", "--name-only", git.MergeBase(@base, head), head);
        string? record = changed.FirstOrDefault(f => ChangeRecord.NumberOf(f) == number);
        if(record == null) {
            return [$"lake({number}) needs its record {ChangeRecord.Directory}/{number}-name.md in this pull request"];
        }
        try {
            ChangeRecord.Parse(record, git.Run("show", $"{head}:{record}"));
        } catch(RecordException e) {
            return [e.Message];
        }
        return [];
    }
}
