namespace Lake.Stack.Commands;

/// <summary>Fails unless every commit above upstream follows the stack's rules (CI runs this).</summary>
public static class CheckCommand {

    public static int Run(StackContext context, string head) {
        StackModel model = StackModel.Load(context.Git, head, context.Upstream);
        IReadOnlyList<string> problems = model.Problems();
        foreach(StackCommit fixup in model.Commits.Where(c => c.FixupOf != null)) {
            context.Out.WriteLine($"pending: {fixup.Short} {fixup.Subject} (folded in at the next sync)");
        }
        foreach(string problem in problems) {
            context.Error.WriteLine(problem);
        }
        if(problems.Count > 0) {
            context.Error.WriteLine($"{problems.Count} problem(s): the stack does not follow docs/changes/README.md");
            return ExitCodes.Problems;
        }
        context.Out.WriteLine($"ok: {model.Commits.Count} commits on {model.Base[..10]} ({context.Upstream})");
        return ExitCodes.Ok;
    }
}
