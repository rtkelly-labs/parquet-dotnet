// Fails unless a built Lake.Parquet assembly is entirely renamed: its name is Lake.Parquet and every
// namespace it defines is Lake.Parquet or under it.
//
//   dotnet run eng/lake/VerifyLakeAssembly.cs -- <path to Lake.Parquet.dll> [...]
//
// RenameNamespace.cs already refuses to leave a namespace or using directive behind. This checks
// the output instead of the source, so a build that quietly fell back to upstream's names (the
// MSBuild hooks not applied, a stale copy) fails before anything is packed.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

const string Root = "Lake.Parquet";

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: VerifyLakeAssembly.cs <assembly> [...]");
    return 2;
}

int failures = 0;
foreach (string path in args)
{
    using var pe = new PEReader(File.OpenRead(path));
    MetadataReader md = pe.GetMetadataReader();
    string name = md.GetString(md.GetAssemblyDefinition().Name);
    List<string> namespaces = md.TypeDefinitions
        .Select(h => md.GetString(md.GetTypeDefinition(h).Namespace))
        .Where(n => n.Length > 0)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();
    List<string> stray = namespaces
        .Where(n => n != Root && !n.StartsWith(Root + ".", StringComparison.Ordinal))
        .Where(n => !n.StartsWith("System", StringComparison.Ordinal) && !n.StartsWith("Microsoft", StringComparison.Ordinal))
        .ToList();

    if (name != Root || stray.Count > 0 || namespaces.Count == 0)
    {
        failures++;
        Console.Error.WriteLine(
            $"{path}: assembly '{name}', {namespaces.Count} namespace(s), not renamed: [{string.Join(", ", stray)}]"
        );
        continue;
    }

    Console.WriteLine($"{path}: {name}, {namespaces.Count} namespace(s), all under {Root}");
}

return failures == 0 ? 0 : 1;
