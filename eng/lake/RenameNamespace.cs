#:package Microsoft.CodeAnalysis.CSharp@4.14.0

// Renames the root namespace of a copy of the source tree, for building Lake.Parquet.
//
//   dotnet run eng/lake/RenameNamespace.cs -- <directory> Parquet Lake.Parquet
//
// Run it on a throwaway copy, never on the checkout: the fork keeps upstream's namespaces so that
// merging upstream stays conflict-free (README, "How Lake.Parquet is built").
//
// It rewrites syntax, not text. Three things change:
//   - namespace declarations      namespace Parquet.Schema;   -> namespace Lake.Parquet.Schema;
//   - using directives            using Parquet.Encodings;     -> using Lake.Parquet.Encodings;
//   - global-qualified names      global::Parquet.Schema.X     -> global::Lake.Parquet.Schema.X
// Nothing else needs to: inside `namespace Lake.Parquet.Encodings`, a reference such as
// `Parquet.Schema.DataField` resolves through the enclosing namespace `Lake` to `Lake.Parquet`.
// Anything this misses fails to compile rather than slipping through, and as a second guard the
// tool fails if a namespace or using directive naming the old root survives anywhere, including
// inside `#if` blocks the parser treated as inactive.

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: RenameNamespace.cs <directory> <from> <to>");
    return 2;
}

string root = args[0];
string from = args[1];
string to = args[2];
var leftover = new Regex(
    $@"^\s*(?:global\s+)?(?:namespace\s+|using\s+(?:static\s+)?(?:\w+\s*=\s*)?){Regex.Escape(from)}\b",
    RegexOptions.Multiline
);

int examined = 0;
int changed = 0;
var survivors = new List<string>();
foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
{
    string relative = Path.GetRelativePath(root, path);
    if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
    {
        continue;
    }

    examined++;
    string text = File.ReadAllText(path);
    SyntaxTree tree = CSharpSyntaxTree.ParseText(
        text,
        new CSharpParseOptions(LanguageVersion.Preview),
        path
    );
    SyntaxNode rewritten = new Renamer(from, to).Visit(tree.GetRoot());
    string output = rewritten.ToFullString();
    if (output != text)
    {
        File.WriteAllText(path, output);
        changed++;
    }

    survivors.AddRange(
        leftover.Matches(output).Select(m => $"{relative}: {m.Value.Trim()}")
    );
}

if (examined == 0)
{
    Console.Error.WriteLine($"No .cs files under {root}: refusing to report a rename of nothing.");
    return 1;
}

if (survivors.Count > 0)
{
    Console.Error.WriteLine($"{survivors.Count} reference(s) to '{from}' survived the rename:");
    survivors.ForEach(s => Console.Error.WriteLine($"  {s}"));
    return 1;
}

Console.WriteLine($"Renamed {from} -> {to} in {changed} of {examined} file(s) under {root}.");
return 0;

sealed class Renamer(string from, string to) : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) =>
        base.VisitNamespaceDeclaration(node.WithName(Rename(node.Name)));

    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(
        FileScopedNamespaceDeclarationSyntax node
    ) => base.VisitFileScopedNamespaceDeclaration(node.WithName(Rename(node.Name)));

    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node) =>
        node.NamespaceOrType is NameSyntax name ? node.WithNamespaceOrType(Rename(name)) : node;

    public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) =>
        node.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword) && node.Name.Identifier.Text == from
            ? SyntaxFactory.ParseName($"global::{to}").WithTriviaFrom(node)
            : base.VisitAliasQualifiedName(node);

    private NameSyntax Rename(NameSyntax name)
    {
        string text = name.ToString();
        return text == from || text.StartsWith(from + ".", StringComparison.Ordinal)
            ? SyntaxFactory.ParseName(to + text[from.Length..]).WithTriviaFrom(name)
            : name;
    }
}
