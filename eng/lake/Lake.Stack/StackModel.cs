using System.Text.RegularExpressions;

namespace Lake.Stack;

/// <summary>One commit above upstream.</summary>
public sealed record StackCommit(string Sha, string Subject, IReadOnlyList<string> Files) {

    public string Short => Sha[..10];

    /// <summary>NNNN for <c>lake(NNNN): title</c>; null otherwise.</summary>
    public string? Number => Subjects.ChangeNumber(Subject);

    /// <summary>NNNN for <c>fixup! lake(NNNN): title</c> (or squash!/amend!); null otherwise.</summary>
    public string? FixupOf => Subjects.FixupNumber(Subject);

    /// <summary>True when every file it touches is a change record: the code is gone (upstream has it).</summary>
    public bool OnlyRecords => Files.Count > 0 && Files.All(f => f.StartsWith(ChangeRecord.Directory + "/", StringComparison.Ordinal));
}

public static partial class Subjects {

    [GeneratedRegex(@"^lake\((?<number>\d{4})\): \S")]
    private static partial Regex ChangePattern();

    [GeneratedRegex(@"^(?:fixup|squash|amend)! lake\((?<number>\d{4})\): \S")]
    private static partial Regex FixupPattern();

    public static string? ChangeNumber(string subject) {
        Match match = ChangePattern().Match(subject);
        return match.Success ? match.Groups["number"].Value : null;
    }

    public static string? FixupNumber(string subject) {
        Match match = FixupPattern().Match(subject);
        return match.Success ? match.Groups["number"].Value : null;
    }
}

/// <summary>
/// The stack as it is at one commit: where it leaves upstream, its commits oldest first, and the
/// change records in its tree.
/// </summary>
public sealed class StackModel {

    public required string Head { get; init; }
    public required string Upstream { get; init; }
    public required string Base { get; init; }
    public required IReadOnlyList<StackCommit> Commits { get; init; }
    public required IReadOnlyList<string> MergeCommits { get; init; }
    public required IReadOnlyDictionary<string, ChangeRecord> Records { get; init; }
    public required IReadOnlyList<RecordException> RecordProblems { get; init; }

    /// <summary>Upstream's files at <see cref="Base"/>, read only when needed.</summary>
    public required Lazy<IReadOnlySet<string>> UpstreamFiles { get; init; }

    public IEnumerable<StackCommit> Changes => Commits.Where(c => c.Number != null);

    public StackCommit? CommitOf(string number) => Commits.FirstOrDefault(c => c.Number == number);

    /// <summary>Position of each change in the stack, for ordering.</summary>
    public int IndexOf(string number) {
        for(int i = 0; i < Commits.Count; i++) {
            if(Commits[i].Number == number) {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A change and everything its depends_on needs, transitively, in stack order.</summary>
    public IReadOnlyList<StackCommit> WithDependencies(string number) {
        var wanted = new HashSet<string> { number };
        var todo = new Queue<string>([number]);
        while(todo.TryDequeue(out string? next)) {
            if(!Records.TryGetValue(next, out ChangeRecord? record)) {
                continue;
            }
            foreach(string dependency in record.DependsOn) {
                if(wanted.Add(dependency)) {
                    todo.Enqueue(dependency);
                }
            }
        }
        return Commits.Where(c => c.Number != null && wanted.Contains(c.Number)).ToList();
    }

    public static StackModel Load(Git git, string head, string upstream) {
        string headSha = git.Resolve(head);
        string upstreamSha = git.Resolve(upstream);
        string @base = git.MergeBase(headSha, upstreamSha);

        var commits = new List<StackCommit>();
        // \x1e starts a commit, \x1f separates its hash from its subject; --name-only lists its files.
        string log = git.Run("log", "--reverse", "--no-merges", "--name-only", "--format=%x1e%H%x1f%s", $"{@base}..{headSha}");
        foreach(string entry in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)) {
            string[] lines = entry.Split('\n');
            string[] header = lines[0].Split('\x1f', 2);
            List<string> files = lines.Skip(1).Where(l => l.Length > 0).ToList();
            commits.Add(new StackCommit(header[0], header[1], files));
        }

        var records = new Dictionary<string, ChangeRecord>();
        var problems = new List<RecordException>();
        foreach(string path in git.Lines("ls-tree", "--name-only", headSha, ChangeRecord.Directory + "/")) {
            if(ChangeRecord.NumberOf(path) == null) {
                continue;
            }
            try {
                ChangeRecord record = ChangeRecord.Parse(path, git.Run("show", $"{headSha}:{path}"));
                if(!records.TryAdd(record.Number, record)) {
                    problems.Add(new RecordException(path, [$"{records[record.Number].Path} has the same number"]));
                }
            } catch(RecordException e) {
                problems.Add(e);
            }
        }

        return new StackModel {
            Head = headSha,
            Upstream = upstreamSha,
            Base = @base,
            Commits = commits,
            MergeCommits = git.Lines("rev-list", "--merges", $"{@base}..{headSha}"),
            Records = records,
            RecordProblems = problems,
            UpstreamFiles = new(() => git.Lines("ls-tree", "-r", "--name-only", @base).ToHashSet(StringComparer.Ordinal)),
        };
    }

    /// <summary>
    /// Every way the stack breaks docs/changes/README.md. Pending fixups are not problems: the next sync
    /// folds them in.
    /// </summary>
    public IReadOnlyList<string> Problems() {
        var problems = new List<string>();
        foreach(string merge in MergeCommits) {
            problems.Add($"{merge[..10]} is a merge commit above upstream: the stack is linear (rebase it)");
        }
        foreach(RecordException e in RecordProblems) {
            problems.Add(e.Message);
        }

        var seen = new HashSet<string>();
        foreach(StackCommit commit in Commits) {
            if(commit.FixupOf is string target) {
                if(CommitOf(target) == null) {
                    problems.Add($"{commit.Short} '{commit.Subject}' fixes lake({target}), which is not in the stack");
                }
                continue;
            }
            if(commit.Number is not string number) {
                problems.Add($"{commit.Short} '{commit.Subject}' is not a change: subjects are 'lake(NNNN): title' or 'fixup! lake(NNNN): title'");
                continue;
            }
            if(!seen.Add(number)) {
                problems.Add($"lake({number}) has more than one commit: squash them into one");
            }
            if(!Records.TryGetValue(number, out ChangeRecord? record)) {
                if(!RecordProblems.Any(p => ChangeRecord.NumberOf(p.Path) == number)) {
                    problems.Add($"lake({number}) has no record {ChangeRecord.Directory}/{number}-name.md");
                }
                continue;
            }
            if(!commit.Files.Contains(record.Path)) {
                problems.Add($"lake({number}): its record {record.Path} must be added or changed in its own commit");
            }
            if(record.Kind == ChangeKind.Addition && commit.Files.Any(IsUpstreamFile)) {
                problems.Add($"lake({number}) is an addition but changes upstream's files ({string.Join(", ", commit.Files.Where(IsUpstreamFile))}); make it kind: code, or move those edits to a code change");
            }
            foreach(string dependency in record.DependsOn) {
                int at = IndexOf(dependency);
                if(at < 0) {
                    problems.Add($"lake({number}) depends on lake({dependency}), which is not in the stack");
                } else if(at > IndexOf(number)) {
                    problems.Add($"lake({number}) depends on lake({dependency}), which is above it: a change can only need what is below it");
                }
            }
        }
        foreach(ChangeRecord record in Records.Values.Where(r => !seen.Contains(r.Number))) {
            problems.Add($"{record.Path} has no commit lake({record.Number})");
        }
        return problems;
    }

    /// <summary>A file that upstream also has at the base, as opposed to one only the fork adds.</summary>
    bool IsUpstreamFile(string path) => UpstreamFiles.Value.Contains(path);
}
