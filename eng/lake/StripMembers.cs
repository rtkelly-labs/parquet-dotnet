#:package Microsoft.CodeAnalysis.CSharp@4.14.0

// Leaves members Lake.Parquet does not need out of a copy of the source tree, for building Lake.Parquet.
//
//   dotnet run eng/lake/StripMembers.cs -- <directory> <manifest>            remove them
//   dotnet run eng/lake/StripMembers.cs -- <directory> <manifest> --hashes   print each member's current hash
//
// Run it on a throwaway copy, before RenameNamespace.cs, never on the checkout: upstream's files stay as upstream
// wrote them (README, "What Lake.Parquet leaves out"). The manifest (eng/lake/strip-members.json) names each member
// by file, type, name and parameter types, with a hash of its syntax. A member whose syntax no longer matches its
// hash (upstream changed it), or that no longer exists, fails the run rather than being removed blindly: review it,
// then update its hash with --hashes. Whole files are left out by Lake.Parquet.targets instead.
//
// An entry with a "branch" leaves the member in and removes one branch of it: an if statement whose condition is that
// text (an else-if is spliced out of its chain; a leading if gives way to its else), or the switch section whose labels
// are that text ("case 0:"); "else:<condition>" removes only the else clause of the if with that condition. The hash is
// still the whole member's, so a change anywhere in it stops the run.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: StripMembers.cs <directory> <manifest> [--hashes]");
    return 2;
}

string root = args[0];
bool printHashes = args.Contains("--hashes");
// read by hand: file-based apps are trimmed, which rules out reflection-based deserialisation
using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(args[1]));
List<Entry> entries = manifest.RootElement.EnumerateArray()
    .Select(e => new Entry(
        e.GetProperty("file").GetString()!,
        e.GetProperty("type").GetString()!,
        e.GetProperty("member").GetString()!,
        e.GetProperty("parameters").GetString()!,
        e.GetProperty("sha256").GetString()!,
        e.TryGetProperty("branch", out JsonElement branch) ? branch.GetString() : null))
    .ToList();
if (entries.Count == 0)
{
    Console.Error.WriteLine($"{args[1]} names no members: refusing to report a strip of nothing.");
    return 1;
}

var problems = new List<string>();
foreach (IGrouping<string, Entry> twice in entries.GroupBy(e => $"{e.File}: {e.Type}.{e.Member}({e.Parameters}) {e.Branch}").Where(g => g.Count() > 1))
    problems.Add($"{twice.Key.TrimEnd()}: named {twice.Count()} times: keep one entry");
int removed = 0, removedBranches = 0;
foreach (IGrouping<string, Entry> file in entries.GroupBy(e => e.File))
{
    string path = Path.Combine(root, file.Key);
    if (!File.Exists(path))
    {
        problems.Add($"{file.Key}: no such file (upstream moved or deleted it): update the manifest");
        continue;
    }
    SyntaxNode tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path).GetRoot();
    var doomed = new List<MemberDeclarationSyntax>();
    var doomedBranches = new List<SyntaxNode>();
    foreach (Entry entry in file)
    {
        List<MemberDeclarationSyntax> matches = tree.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m.Parent is TypeDeclarationSyntax type && type.Identifier.Text == entry.Type)
            .Where(m => Name(m) == entry.Member && Signature(m) == entry.Parameters)
            .ToList();
        string id = $"{entry.File}: {entry.Type}.{entry.Member}({entry.Parameters}){(entry.Branch == null ? "" : $" branch '{entry.Branch}'")}";
        if (matches.Count != 1)
        {
            problems.Add($"{id}: {(matches.Count == 0 ? "not found (upstream removed or renamed it)" : "matches more than one member")}: update the manifest");
            continue;
        }
        string hash = Hash(matches[0]);
        if (printHashes)
        {
            Console.WriteLine($"{id}: {hash}");
            continue;
        }
        if (hash != entry.Sha256)
        {
            problems.Add($"{id}: upstream changed it (hash {hash}, manifest {entry.Sha256}): check it is still unneeded, then update its hash");
            continue;
        }
        if (entry.Branch == null)
        {
            doomed.Add(matches[0]);
            continue;
        }
        List<SyntaxNode> branches = Branches(matches[0], entry.Branch).ToList();
        if (branches.Count != 1)
        {
            problems.Add($"{id}: {(branches.Count == 0 ? "no such branch" : "matches more than one branch")}: update the manifest");
            continue;
        }
        doomedBranches.Add(branches[0]);
    }
    if (!printHashes && doomedBranches.Count > 0)
    {
        var branchAnnotation = new SyntaxAnnotation("lake-strip-branch");
        tree = tree.ReplaceNodes(doomedBranches, (_, n) => n.WithAdditionalAnnotations(branchAnnotation));
        while (tree.GetAnnotatedNodes(branchAnnotation).FirstOrDefault() is SyntaxNode next)
            tree = RemoveBranch(tree, next);
        removedBranches += doomedBranches.Count;
        // members to remove were found in the tree before; find them again in the new one
        doomed = doomed.Select(d => tree.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Single(m => m.Parent is TypeDeclarationSyntax t && d.Parent is TypeDeclarationSyntax dt && t.Identifier.Text == dt.Identifier.Text
                && Name(m) == Name(d) && Signature(m) == Signature(d))).ToList();
        File.WriteAllText(path, tree.ToFullString());
    }
    if (!printHashes && doomed.Count > 0)
    {
        // A member whose leading trivia holds preprocessor directives (and the disabled code between them, such as an
        // #if NET7_0_OR_GREATER block before it) keeps that trivia; any other member goes with its comments.
        static bool HasDirectives(SyntaxNode m) => m.GetLeadingTrivia().Any(t => t.IsDirective || t.IsKind(SyntaxKind.DisabledTextTrivia));
        var annotation = new SyntaxAnnotation("lake-strip");
        tree = tree.ReplaceNodes(doomed, (_, m) => m.WithAdditionalAnnotations(annotation));
        // one at a time: trivia a removed member keeps moves to the next token, which may start another doomed member
        while (tree.GetAnnotatedNodes(annotation).FirstOrDefault() is SyntaxNode next)
        {
            if (HasDirectives(next))
            {
                // the directives stay; the member's own documentation goes with it
                tree = tree.ReplaceNode(next, next.WithLeadingTrivia(next.GetLeadingTrivia().Where(t =>
                    !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))));
                next = tree.GetAnnotatedNodes(annotation).First();
            }
            tree = tree.RemoveNode(next, HasDirectives(next)
                ? SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives
                : SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        }
        File.WriteAllText(path, tree.ToFullString());
        removed += doomed.Count;
    }
}

if (problems.Count > 0)
{
    problems.ForEach(p => Console.Error.WriteLine(p));
    return 1;
}
if (!printHashes)
{
    Console.WriteLine($"Left out {removed} member(s) and {removedBranches} branch(es) named in {args[1]} under {root}.");
}
return 0;

// The if statements whose condition, and the switch sections whose labels, are the branch text, whitespace normalised.
static IEnumerable<SyntaxNode> Branches(MemberDeclarationSyntax member, string branch)
{
    if (branch.StartsWith("else:", StringComparison.Ordinal))
    {
        string condition = SyntaxFactory.ParseExpression(branch["else:".Length..]).NormalizeWhitespace().ToString();
        foreach (IfStatementSyntax ifs in member.DescendantNodes().OfType<IfStatementSyntax>())
            if (ifs.Else != null && ifs.Condition.NormalizeWhitespace().ToString() == condition)
                yield return ifs.Else;
        yield break;
    }
    string want = SyntaxFactory.ParseExpression(branch).NormalizeWhitespace().ToString();
    foreach (SyntaxNode node in member.DescendantNodes())
    {
        if (node is IfStatementSyntax ifs && ifs.Condition.NormalizeWhitespace().ToString() == want)
            yield return node;
        else if (node is SwitchSectionSyntax section
            && string.Join(" ", section.Labels.Select(l => l.NormalizeWhitespace().ToString())) == branch.Trim())
            yield return node;
    }
}

// An else-if leaves its chain (the if before it takes its else); a leading if gives way to its else, or goes; a switch
// section goes.
static SyntaxNode RemoveBranch(SyntaxNode tree, SyntaxNode branch)
{
    switch (branch)
    {
        case ElseClauseSyntax elseClause when elseClause.Parent is IfStatementSyntax owner:
            return tree.ReplaceNode(owner, owner.WithElse(null));
        case SwitchSectionSyntax:
            return tree.RemoveNode(branch, SyntaxRemoveOptions.KeepNoTrivia)!;
        case IfStatementSyntax ifs when ifs.Parent is ElseClauseSyntax elseClause && elseClause.Parent is IfStatementSyntax previous:
            return tree.ReplaceNode(previous, previous.WithElse(ifs.Else));
        case IfStatementSyntax ifs when ifs.Else != null:
            return tree.ReplaceNode(ifs, ifs.Else.Statement.WithTriviaFrom(ifs));
        default:
            return tree.RemoveNode(branch, SyntaxRemoveOptions.KeepNoTrivia)!;
    }
}

// The member's syntax, whitespace and comments normalised away, so formatting changes do not count as edits.
static string Hash(MemberDeclarationSyntax member) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(member.NormalizeWhitespace().ToString().ReplaceLineEndings("\n"))))[..16];

static string? Name(MemberDeclarationSyntax member) => member switch
{
    MethodDeclarationSyntax m => m.Identifier.Text,
    PropertyDeclarationSyntax p => p.Identifier.Text,
    ConstructorDeclarationSyntax c => c.Identifier.Text,
    _ => null,
};

static string Signature(MemberDeclarationSyntax member) => member switch
{
    BaseMethodDeclarationSyntax m => string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "")),
    _ => "",
};

sealed record Entry(string File, string Type, string Member, string Parameters, string Sha256, string? Branch);
