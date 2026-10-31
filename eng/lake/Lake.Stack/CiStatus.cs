using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Lake.Stack;

public enum CiState {
    /// <summary>No run of the check for that commit (not pushed, or CI has not picked it up yet).</summary>
    Missing,
    Pending,
    Success,
    Failure,
}

/// <summary>The state of one CI check on one commit of the fork.</summary>
public delegate CiState CiStatus(string sha, string check);

/// <summary>
/// Reads a check's state from GitHub through the gh CLI, for the repository a git remote points at.
/// </summary>
public static partial class GitHubCi {

    public const string Check = "lake-ci";

    public static CiStatus For(Git git, string remote) {
        string slug = Slug(git.Run("remote", "get-url", remote))
            ?? throw new StackException($"cannot tell which GitHub repository '{remote}' is");
        return (sha, check) => Query(slug, sha, check);
    }

    /// <summary>The newest run of the check decides: pending until it completes, then its conclusion.</summary>
    static CiState Query(string slug, string sha, string check) {
        var start = new ProcessStartInfo("gh") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach(string arg in new[] {
            "api", $"repos/{slug}/commits/{sha}/check-runs?check_name={check}",
            "--jq", ".check_runs | sort_by(.started_at) | reverse | .[0] | [.status, .conclusion] | @tsv",
        }) {
            start.ArgumentList.Add(arg);
        }
        Process process;
        try {
            process = Process.Start(start) ?? throw new StackException("gh did not start");
        } catch(System.ComponentModel.Win32Exception) {
            throw new StackException("the gh CLI is needed to read the staged build's CI result (https://cli.github.com), or pass --skip-ci-check after checking it yourself");
        }
        using(process) {
            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if(process.ExitCode != 0) {
                throw new StackException($"gh could not read the checks of {sha[..10]} on {slug}: {error}");
            }
            return Parse(output);
        }
    }

    /// <summary>gh's "status\tconclusion" line (empty when the check never ran) as a state.</summary>
    public static CiState Parse(string line) {
        string[] parts = line.Split('\t');
        return parts[0] switch {
            "" or "null" => CiState.Missing,
            "completed" => parts.Length > 1 && parts[1] == "success" ? CiState.Success : CiState.Failure,
            _ => CiState.Pending,
        };
    }

    [GeneratedRegex(@"(?:github\.com[:/]|/git/)(?<slug>[^/:]+/[^/]+?)(?:\.git)?/?$")]
    private static partial Regex SlugPattern();

    /// <summary>owner/repo from an https, ssh or proxied GitHub remote URL.</summary>
    public static string? Slug(string url) {
        Match match = SlugPattern().Match(url.Trim());
        return match.Success ? match.Groups["slug"].Value : null;
    }
}
