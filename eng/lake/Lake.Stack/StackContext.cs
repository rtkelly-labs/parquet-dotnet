namespace Lake.Stack;

/// <summary>What every command works with: the repository, the upstream ref, and where to write.</summary>
public sealed record StackContext(Git Git, string Upstream, TextWriter Out, TextWriter Error) {

    /// <summary>Fetches the remote an upstream-style ref (<c>remote/branch</c>) comes from, if there is one.</summary>
    public void FetchRemoteOf(string reference) {
        int slash = reference.IndexOf('/');
        if(slash <= 0) {
            return;
        }
        string remote = reference[..slash];
        if(Git.HasRemote(remote)) {
            Git.Run("fetch", "--quiet", "--tags", remote);
        }
    }
}

/// <summary>The exit codes every command uses.</summary>
public static class ExitCodes {
    public const int Ok = 0;
    /// <summary>The command ran and found something to fix (a broken rule, a conflict, a failed branch).</summary>
    public const int Problems = 1;
    /// <summary>The command could not run (bad arguments, a dirty tree, a git failure).</summary>
    public const int Error = 2;
}
