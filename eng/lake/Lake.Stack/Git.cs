using System.Diagnostics;

namespace Lake.Stack;

/// <summary>
/// Runs git in one repository. Arguments go to git as a list, never through a shell, so refs and paths
/// need no quoting.
/// </summary>
public sealed class Git(string directory) {

    public string Directory { get; } = Path.GetFullPath(directory);

    public GitResult TryRun(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null) {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach(string arg in args) {
            start.ArgumentList.Add(arg);
        }
        // Never wait for a password prompt, and keep git's messages in one language.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        if(environment != null) {
            foreach((string key, string value) in environment) {
                start.Environment[key] = value;
            }
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new GitResult(process.ExitCode, output.Result, error.Result);
    }

    public GitResult TryRun(params string[] args) => TryRun((IReadOnlyList<string>)args);

    /// <summary>Runs git and returns its output without the trailing newline; throws if it fails.</summary>
    public string Run(params string[] args) => Run(args, null);

    public string Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment) {
        GitResult result = TryRun(args, environment);
        if(result.ExitCode != 0) {
            throw new GitException(args, result);
        }
        return result.Output.TrimEnd('\n');
    }

    public IReadOnlyList<string> Lines(params string[] args) =>
        Run(args).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public bool Succeeds(params string[] args) => TryRun(args).ExitCode == 0;

    /// <summary>The commit a revision names, or null when it names none.</summary>
    public string? TryResolve(string revision) {
        GitResult result = TryRun("rev-parse", "--verify", "--quiet", revision + "^{commit}");
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    public string Resolve(string revision) =>
        TryResolve(revision) ?? throw new StackException($"'{revision}' does not name a commit");

    public string MergeBase(string a, string b) => Run("merge-base", a, b);

    public bool IsAncestor(string ancestor, string descendant) =>
        Succeeds("merge-base", "--is-ancestor", ancestor, descendant);

    /// <summary>The checked-out branch, or the commit when HEAD is detached.</summary>
    public string CurrentBranchOrCommit() {
        GitResult branch = TryRun("symbolic-ref", "--quiet", "--short", "HEAD");
        return branch.ExitCode == 0 ? branch.Output.Trim() : Resolve("HEAD");
    }

    /// <summary>True when tracked files have no staged or unstaged changes.</summary>
    public bool IsClean() => Run("status", "--porcelain", "--untracked-files=no").Length == 0;

    public bool HasRemote(string name) => Lines("remote").Contains(name);

    /// <summary>The path git uses for a file under its own directory (worktree-aware).</summary>
    public string GitPath(string name) => Path.Combine(Directory, Run("rev-parse", "--git-path", name));
}

public sealed record GitResult(int ExitCode, string Output, string Error);

public sealed class GitException(IReadOnlyList<string> args, GitResult result)
    : Exception($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Error.Trim()}") {
    public GitResult Result { get; } = result;
}

/// <summary>A problem the user has to fix; reported as a message, without a stack trace.</summary>
public sealed class StackException(string message) : Exception(message);
