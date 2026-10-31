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
        e.GetProperty("sha256").GetString()!))
    .ToList();
if (entries.Count == 0)
{
    Console.Error.WriteLine($"{args[1]} names no members: refusing to report a strip of nothing.");
    return 1;
}

var problems = new List<string>();
int removed = 0;
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
    foreach (Entry entry in file)
    {
        List<MemberDeclarationSyntax> matches = tree.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m.Parent is TypeDeclarationSyntax type && type.Identifier.Text == entry.Type)
            .Where(m => Name(m) == entry.Member && Signature(m) == entry.Parameters)
            .ToList();
        string id = $"{entry.File}: {entry.Type}.{entry.Member}({entry.Parameters})";
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
        doomed.Add(matches[0]);
    }
    if (!printHashes && doomed.Count > 0)
    {
        File.WriteAllText(path, tree.RemoveNodes(doomed, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
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
    Console.WriteLine($"Left out {removed} member(s) named in {args[1]} under {root}.");
}
return 0;

// The member's syntax, whitespace and comments normalised away, so formatting changes do not count as edits.
static string Hash(MemberDeclarationSyntax member) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(member.NormalizeWhitespace().ToString())))[..16];

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

sealed record Entry(string File, string Type, string Member, string Parameters, string Sha256);
