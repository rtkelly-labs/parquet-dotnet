namespace Lake.Stack.Tests;

/// <summary>
/// Three repositories in a temporary directory, laid out like the real thing: <c>upstream</c> (a working
/// repository standing in for aloneguid/parquet-dotnet), <c>origin.git</c> (a bare one standing in for
/// the fork on GitHub) and <c>fork</c> (a clone of origin with an <c>upstream</c> remote), whose
/// <c>master</c> has the stack's bottom commit, <c>lake(0001)</c>.
/// </summary>
public sealed class TestRepo : IDisposable {

    readonly string root = Path.Combine(Path.GetTempPath(), "lake-stack-test-" + Guid.NewGuid().ToString("n")[..12]);

    public Git Upstream { get; }
    public Git Fork { get; }
    public Git Origin { get; }

    public TestRepo() {
        Directory.CreateDirectory(root);
        Upstream = Init("upstream");
        Commit(Upstream, "upstream: initial", ("src/A.cs", Lines("a", 20)), ("src/B.cs", Lines("b", 20)));
        Commit(Upstream, "upstream: second", ("src/C.cs", Lines("c", 5)));

        Run(root, "clone", "--quiet", "--bare", Upstream.Directory, "origin.git");
        Origin = new Git(Path.Combine(root, "origin.git"));
        Run(root, "clone", "--quiet", Origin.Directory, "fork");
        Fork = Configure(new Git(Path.Combine(root, "fork")));
        Fork.Run("remote", "add", "upstream", Upstream.Directory);
        Fork.Run("fetch", "--quiet", "upstream");

        AddChange("0001", "packaging", "Packaging", kind: "addition",
            files: [("README.md", "# fork\n"), ("docs/changes/README.md", "rules\n")]);
        Fork.Run("push", "--quiet", "origin", "master");
    }

    /// <summary>Runs the tool in the fork, as the command line would.</summary>
    public (int Code, string Output, string Error) Tool(params string[] args) {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Run(["--repo", Fork.Directory, .. args], output, error);
        return (code, output.ToString(), error.ToString());
    }

    public StackContext Context() => new(Fork, "upstream/master", new StringWriter(), new StringWriter());

    /// <summary>Commits files (null content deletes one) in a repository; returns the commit.</summary>
    public static string Commit(Git git, string subject, params (string Path, string? Content)[] files) {
        foreach((string path, string? content) in files) {
            string full = Path.Combine(git.Directory, path);
            if(content == null) {
                File.Delete(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        git.Run("add", "--all");
        git.Run("commit", "--quiet", "--allow-empty", "-m", subject);
        return git.Resolve("HEAD");
    }

    /// <summary>Adds one change to the fork: its files and its record, in one commit lake(NNNN).</summary>
    public string AddChange(
        string number,
        string name,
        string title,
        string kind = "code",
        string[]? dependsOn = null,
        params (string Path, string? Content)[] files) =>
        Commit(Fork, $"lake({number}): {title}",
            [($"docs/changes/{number}-{name}.md", Record(title, kind, dependsOn ?? [])), .. files]);

    public static string Record(string title, string kind = "code", string[]? dependsOn = null, string status = "fork-only") =>
        $"""
        ---
        title: "{title}"
        kind: {kind}
        status: {status}   # fork-only | proposed-upstream | upstreamed | superseded
        depends_on: [{string.Join(", ", dependsOn ?? [])}]
        ---

        # {title}
        """;

    /// <summary>Commits to upstream and fetches it into the fork.</summary>
    public string UpstreamCommit(string subject, params (string Path, string? Content)[] files) {
        string sha = Commit(Upstream, subject, files);
        Fork.Run("fetch", "--quiet", "upstream");
        return sha;
    }

    /// <summary><paramref name="count"/> lines "prefix1".."prefixN", so edits to one line merge cleanly.</summary>
    public static string Lines(string prefix, int count, params (int Line, string Text)[] replace) {
        var lines = Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();
        foreach((int line, string text) in replace) {
            lines[line - 1] = text;
        }
        return string.Join('\n', lines) + "\n";
    }

    public string Read(string path) => File.ReadAllText(Path.Combine(Fork.Directory, path));

    Git Init(string name) {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        Run(path, "init", "--quiet", "-b", "master");
        return Configure(new Git(path));
    }

    static Git Configure(Git git) {
        git.Run("config", "user.name", "Test");
        git.Run("config", "user.email", "test@example.com");
        git.Run("config", "commit.gpgsign", "false");
        git.Run("config", "tag.gpgsign", "false");
        return git;
    }

    static void Run(string directory, params string[] args) => new Git(directory).Run(args);

    public void Dispose() {
        // git makes its object files read-only.
        foreach(string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }
}
