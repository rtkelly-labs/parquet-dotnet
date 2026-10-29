using System.Text.RegularExpressions;

namespace Lake.Stack;

public enum ChangeKind {
    /// <summary>Only adds the fork's own files (packaging, CI, docs).</summary>
    Addition,
    /// <summary>Changes upstream's code; gets a lake/change/ branch.</summary>
    Code,
}

public enum ChangeStatus {
    ForkOnly,
    ProposedUpstream,
    Upstreamed,
    Superseded,
}

/// <summary>
/// One record in docs/changes/, <c>NNNN-name.md</c>, with YAML frontmatter (docs/changes/README.md,
/// "Record format"). Only the flat subset of YAML the records use is read: <c>key: value</c>, a quoted
/// string, or a <c>[a, b]</c> list, with an optional <c># comment</c>.
/// </summary>
public sealed partial record ChangeRecord(
    string Number,
    string Path,
    string Title,
    ChangeKind Kind,
    ChangeStatus Status,
    IReadOnlyList<string> DependsOn,
    string? Upstream) {

    public const string Directory = "docs/changes";

    static readonly HashSet<string> KnownKeys = ["title", "kind", "status", "depends_on", "upstream", "upstream_unit"];

    /// <summary><c>NNNN-name</c>, which is also the change branch's name under lake/change/.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string BranchName => "lake/change/" + Name;

    [GeneratedRegex(@"^(?<number>\d{4})-[a-z0-9][a-z0-9.-]*\.md$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex NumberPattern();

    /// <summary>The record number a path under docs/changes/ names, or null for any other file.</summary>
    public static string? NumberOf(string path) {
        if(!path.StartsWith(Directory + "/", StringComparison.Ordinal)) {
            return null;
        }
        Match match = FileNamePattern().Match(path[(Directory.Length + 1)..]);
        return match.Success ? match.Groups["number"].Value : null;
    }

    public static ChangeRecord Parse(string path, string text) {
        string number = NumberOf(path) ?? throw new RecordException(path, ["the file name is not NNNN-name.md"]);
        var problems = new List<string>();
        Dictionary<string, string> fields = ReadFrontmatter(text, problems);

        foreach(string key in fields.Keys.Where(k => !KnownKeys.Contains(k))) {
            problems.Add($"unknown field '{key}' (known: {string.Join(", ", KnownKeys)})");
        }

        string title = fields.GetValueOrDefault("title", "");
        if(title.Length == 0) {
            problems.Add("no title");
        }

        ChangeKind kind = ChangeKind.Code;
        switch(fields.GetValueOrDefault("kind")) {
            case "addition": kind = ChangeKind.Addition; break;
            case "code": kind = ChangeKind.Code; break;
            case var other: problems.Add($"kind is '{other}', not addition or code"); break;
        }

        ChangeStatus status = ChangeStatus.ForkOnly;
        switch(fields.GetValueOrDefault("status")) {
            case "fork-only": status = ChangeStatus.ForkOnly; break;
            case "proposed-upstream": status = ChangeStatus.ProposedUpstream; break;
            case "upstreamed": status = ChangeStatus.Upstreamed; break;
            case "superseded": status = ChangeStatus.Superseded; break;
            case var other:
                problems.Add($"status is '{other}', not fork-only, proposed-upstream, upstreamed or superseded");
                break;
        }

        var dependsOn = new List<string>();
        if(fields.TryGetValue("depends_on", out string? list)) {
            if(!list.StartsWith('[') || !list.EndsWith(']')) {
                problems.Add("depends_on is not a [NNNN, ...] list");
            } else {
                foreach(string item in list[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                    if(NumberPattern().IsMatch(item)) {
                        dependsOn.Add(item);
                    } else {
                        problems.Add($"depends_on names '{item}', not a four-digit change number");
                    }
                }
            }
        }
        if(dependsOn.Contains(number)) {
            problems.Add("depends_on names the change itself");
        }

        if(problems.Count > 0) {
            throw new RecordException(path, problems);
        }
        return new ChangeRecord(number, path, title, kind, status, dependsOn, fields.GetValueOrDefault("upstream"));
    }

    static Dictionary<string, string> ReadFrontmatter(string text, List<string> problems) {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        if(lines.Length == 0 || lines[0] != "---") {
            problems.Add("no frontmatter: the file must start with a '---' line");
            return fields;
        }
        int end = Array.IndexOf(lines, "---", 1);
        if(end < 0) {
            problems.Add("the frontmatter has no closing '---' line");
            return fields;
        }
        for(int i = 1; i < end; i++) {
            string line = lines[i];
            if(line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) {
                continue;
            }
            int colon = line.IndexOf(':');
            if(colon <= 0) {
                problems.Add($"frontmatter line {i + 1} is not 'key: value'");
                continue;
            }
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if(value.StartsWith('"')) {
                int close = value.IndexOf('"', 1);
                if(close < 0) {
                    problems.Add($"'{key}' has an unterminated quoted string");
                    continue;
                }
                value = value[1..close];
            } else {
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if(comment >= 0) {
                    value = value[..comment].TrimEnd();
                }
            }
            if(!fields.TryAdd(key, value)) {
                problems.Add($"'{key}' appears twice");
            }
        }
        return fields;
    }
}

public sealed class RecordException(string path, IReadOnlyList<string> problems)
    : Exception($"{path}: {string.Join("; ", problems)}") {
    public string Path { get; } = path;
    public IReadOnlyList<string> Problems { get; } = problems;
}
