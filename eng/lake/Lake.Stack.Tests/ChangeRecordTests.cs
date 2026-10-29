namespace Lake.Stack.Tests;

public class ChangeRecordTests {

    [Fact]
    public void Reads_every_field() {
        ChangeRecord record = ChangeRecord.Parse("docs/changes/0017-physical-values.md", """
            ---
            title: "Physical values (`DataField.PhysicalClrType`) # not a comment"
            kind: code
            status: proposed-upstream   # fork-only | proposed-upstream | upstreamed | superseded
            depends_on: [0002, 0008]
            upstream: https://github.com/aloneguid/parquet-dotnet/pull/1
            upstream_unit: "14"
            ---

            # body
            """);

        Assert.Equal("0017", record.Number);
        Assert.Equal("0017-physical-values", record.Name);
        Assert.Equal("lake/change/0017-physical-values", record.BranchName);
        Assert.Equal("Physical values (`DataField.PhysicalClrType`) # not a comment", record.Title);
        Assert.Equal(ChangeKind.Code, record.Kind);
        Assert.Equal(ChangeStatus.ProposedUpstream, record.Status);
        Assert.Equal(["0002", "0008"], record.DependsOn);
        Assert.Equal("https://github.com/aloneguid/parquet-dotnet/pull/1", record.Upstream);
    }

    [Fact]
    public void An_empty_dependency_list_is_none() {
        ChangeRecord record = ChangeRecord.Parse("docs/changes/0002-x.md", TestRepo.Record("X"));
        Assert.Empty(record.DependsOn);
    }

    [Theory]
    [InlineData("patch", "fork-only", "", "kind is 'patch'")]
    [InlineData("code", "done", "", "status is 'done'")]
    [InlineData("code", "fork-only", "owner: me", "unknown field 'owner'")]
    [InlineData("code", "fork-only", "depends_on: 0002", "not a [NNNN, ...] list")]
    [InlineData("code", "fork-only", "depends_on: [2]", "'2', not a four-digit change number")]
    [InlineData("code", "fork-only", "depends_on: [0005]", "depends_on names the change itself")]
    public void Rejects_a_bad_field(string kind, string status, string extra, string problem) {
        string text = $"---\ntitle: \"T\"\nkind: {kind}\nstatus: {status}\n{extra}\n---\n";
        RecordException e = Assert.Throws<RecordException>(() => ChangeRecord.Parse("docs/changes/0005-x.md", text));
        Assert.Contains(e.Problems, p => p.Contains(problem));
    }

    [Fact]
    public void Rejects_a_file_without_frontmatter() {
        RecordException e = Assert.Throws<RecordException>(() => ChangeRecord.Parse("docs/changes/0002-x.md", "# no frontmatter"));
        Assert.Contains(e.Problems, p => p.Contains("no frontmatter"));
    }

    [Theory]
    [InlineData("docs/changes/0002-name.md", "0002")]
    [InlineData("docs/changes/README.md", null)]
    [InlineData("docs/changes/upstreaming.md", null)]
    [InlineData("docs/other/0002-name.md", null)]
    [InlineData("docs/changes/002-name.md", null)]
    public void Knows_which_files_are_records(string path, string? number) =>
        Assert.Equal(number, ChangeRecord.NumberOf(path));
}
