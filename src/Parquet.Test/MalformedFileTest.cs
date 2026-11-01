using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Encryption;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;
using Xunit.Abstractions;
using F = System.IO.File;

namespace Parquet.Test;

/// <summary>
/// Hostile input: files written by other engines can be truncated, corrupt or crafted. Reading one must end in
/// success or in an error a caller can catch as "this file is bad" (<see cref="Allowed"/>), within a time limit and a
/// memory budget, and never in a crash, a hang or an unbounded allocation.
/// <para>
/// The corpus is apache/parquet-testing's <c>bad_data/</c> (vendored in hostile/bad_data, see <see cref="HostileDir"/>) plus deterministic mutations of
/// good fixtures: truncation, single-bit flips, an overwritten footer length, and hostile values written into the
/// footer's metadata and into page headers. Every mutation is derived from <see cref="Seed"/> and the source's name, so a
/// case id names exactly the same bytes on every run.
/// </para>
/// <para>
/// Cases that fail today are listed, with what they do instead, in data/malformed/known.tsv; each is a candidate
/// upstream fix (docs/changes/0100-malformed-file-tests.md). A known case passes while it fails the same way, or once
/// it reads cleanly (then remove it from the list); it fails if it starts failing differently. A kind may list
/// alternatives (a|b: the outcome differs between .NET 8 and 10), or be "any": the outcome depends on the machine
/// (an allocation sized by the file that may or may not be served).
/// To regenerate the list, run this class with PARQUET_MALFORMED_KNOWN_OUT=&lt;path&gt;: every case that is not clean
/// is written there.
/// </para>
/// </summary>
public class MalformedFileTest {

    const int Seed = 260026;

    // apache/parquet-testing's bad_data/, beside this file: not under data/, which the Native AOT corpus check decodes,
    // and read from the source tree, so the test project needs no item for it
    static readonly string HostileDir = SourceDir() + "/hostile";
    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(10);
    const string RecordPath = "docs/changes/0100-malformed-file-tests.md";

    readonly ITestOutputHelper _output;

    public MalformedFileTest(ITestOutputHelper output) {
        _output = output;
    }

    static readonly byte[] FooterKey = System.Text.Encoding.ASCII.GetBytes("0123456789012345");
    static readonly byte[] NameKey = System.Text.Encoding.ASCII.GetBytes("1234567890123450");

    /// <summary>
    /// The errors that mean "this file is bad". The library throws these on purpose for corrupt input (format checks,
    /// decryption and signature failures are <see cref="ParquetException"/>; short reads are
    /// <see cref="EndOfStreamException"/>). Anything else is a finding.
    /// </summary>
    static readonly System.Type[] Allowed = [
        typeof(ParquetException),
        typeof(IOException),            // includes EndOfStreamException
        typeof(InvalidDataException),
    ];

    sealed record Source(string Path, Func<ParquetOptions?> Options);

    static ParquetOptions Keys() => new() {
        Decryption = new ParquetDecryptionOptions {
            FooterKey = FooterKey,
            ColumnKeys = new Dictionary<string, byte[]> { ["name"] = NameKey }
        }
    };

    /// <summary>Good fixtures to mutate: nested, dictionary, v2 pages, every codec, the delta and BSS encodings, encryption.</summary>
    static readonly Source[] Sources = [
        new("simplenested.parquet", () => null),                        // struct, uncompressed
        new("map_simple.parquet", () => null),                          // map
        new("malformed/zstd-nested.parquet", () => null),               // zstd, list, nulls, 2 row groups (written by Parquet.Net)
        new("v2/v2_snappy.parquet", () => null),                        // data page v2, snappy, dictionary, list
        new("types/alltypes_dictionary.gzip.parquet", () => null),      // gzip, dictionary, int96
        new("snappy-nation.impala.parquet", () => null),                // snappy, dictionary (impala)
        new("delta_length_byte_array.parquet", () => null),
        new("delta_binary_packed.parquet", () => null),
        new("byte_stream_split_256.parquet", () => null),
        new("encryption/uniform.parquet", Keys),                        // encrypted footer, key supplied
        new("encryption/plaintext_footer.parquet", Keys),               // signed plaintext footer, key supplied
    ];

    /// <summary>Known findings: case id to what it does instead of reading cleanly (exception types, "alloc", "timeout" or "any").</summary>
    static readonly Lazy<Dictionary<string, string>> Known = new(() => F.ReadAllLines("./data/malformed/known.tsv")
        .Where(l => l.Length > 0 && !l.StartsWith("#"))
        .Select(l => l.Split('\t'))
        .ToDictionary(p => p[0], p => p[1]));

    public static IEnumerable<object[]> CleanCases() =>
        AllCases.Value.Keys.Where(k => !Known.Value.ContainsKey(k)).Select(k => new object[] { k });

    public static IEnumerable<object[]> KnownCases() =>
        AllCases.Value.Keys.Where(k => Known.Value.ContainsKey(k)).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(CleanCases))]
    public void Reads_or_fails_cleanly(string id) {
        Outcome o = Run(id);
        _output.WriteLine(o.ToString());
        Assert.True(o.Clean, $"{id} (seed {Seed}): {o}. A new finding: add it to data/malformed/known.tsv and {RecordPath}.");
    }

    [Theory]
    [MemberData(nameof(KnownCases))]
    public void Known_findings_fail_the_same_way(string id) {
        Outcome o = Run(id);
        _output.WriteLine(o.ToString());
        if(o.Clean) {
            _output.WriteLine($"{id} now reads cleanly ({o}): remove it from data/malformed/known.tsv and {RecordPath}.");
            return;
        }
        string known = Known.Value[id];
        Assert.True(known == "any" || known.Split('|').Contains(o.Kind), $"{id} (seed {Seed}): known as {known}, now {o}.");
    }

    [Fact]
    public void Every_known_case_exists() {
        Assert.DoesNotContain(Known.Value.Keys, k => !AllCases.Value.ContainsKey(k));
    }

    /// <summary>The rewriting is faithful: a footer or page header written back unchanged reads back as the original.</summary>
    [Fact]
    public void Identity_rewrites_read_cleanly() {
        foreach(Source s in Sources) {
            Layout l = Layout.Of(F.ReadAllBytes("./data/" + s.Path));
            Assert.Equal("ok", Execute(l.Bytes, s.Options).Kind);
            if(l.Meta == null)
                continue;
            Assert.Equal("ok", Execute(l.WithFooter(l.Bytes.AsSpan(0, l.FooterStart), Clone(l.Meta)), s.Options).Kind);
            Assert.NotEmpty(l.Pages);
            Assert.Equal("ok", Execute(l.WithPageHeader(0, _ => { }), s.Options).Kind);
            Assert.Equal("ok", Execute(l.WithPageHeader(l.Pages.Count - 1, _ => { }), s.Options).Kind);
        }
    }

    static readonly Lazy<Dictionary<string, (byte[] Bytes, Func<ParquetOptions?> Options)>> AllCases = new(BuildCases);

    static Dictionary<string, (byte[], Func<ParquetOptions?>)> BuildCases() {
        var cases = new Dictionary<string, (byte[], Func<ParquetOptions?>)>();

        foreach(string path in Directory.GetFiles(HostileDir + "/bad_data", "*.parquet", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) {
            string name = Path.GetRelativePath(HostileDir, path).Replace('\\', '/');
            cases[name] = (F.ReadAllBytes(path), () => null);
        }

        foreach(Source s in Sources) {
            Layout l = Layout.Of(F.ReadAllBytes("./data/" + s.Path));
            foreach((string mutation, byte[] bytes) in Mutations(l, s.Path))
                cases[$"{s.Path}|{mutation}"] = (bytes, s.Options);
        }
        return cases;
    }

    static IEnumerable<(string, byte[])> Mutations(Layout l, string name) {
        byte[] b = l.Bytes;
        int len = b.Length;
        var rnd = new Random(Seed ^ StableHash(name));

        // truncation: the ends, the footer, page header boundaries, and seeded offsets
        var cuts = new SortedSet<int> { 0, 1, 3, 4, 8, len - 1, len - 4, len - 5, len - 8, len - 9,
            l.FooterStart - 1, l.FooterStart, l.FooterStart + 1, l.FooterStart + l.FooterLength / 2 };
        foreach((int start, int length, _) in l.Pages.Take(6)) {
            cuts.Add(start + 1);
            cuts.Add(start + length);
        }
        for(int i = 0; i < 6; i++)
            cuts.Add(rnd.Next(9, l.FooterStart));
        foreach(int cut in cuts.Where(c => c >= 0 && c < len))
            yield return ($"trunc@{cut}", b[..cut]);

        // single-bit flips: the footer, page headers, anywhere
        var flips = new SortedSet<(int, int)>();
        for(int i = 0; i < 12; i++)
            flips.Add((rnd.Next(l.FooterStart, len - 8), rnd.Next(8)));
        for(int i = 0; i < 12 && l.Pages.Count > 0; i++) {
            (int start, int length, _) = l.Pages[rnd.Next(l.Pages.Count)];
            flips.Add((start + rnd.Next(length), rnd.Next(8)));
        }
        for(int i = 0; i < (l.Pages.Count > 0 ? 8 : 20); i++)
            flips.Add((rnd.Next(4, l.FooterStart), rnd.Next(8)));
        foreach((int pos, int bit) in flips) {
            byte[] m = (byte[])b.Clone();
            m[pos] ^= (byte)(1 << bit);
            yield return ($"flip@{pos}.{bit}", m);
        }

        // the footer length
        foreach(int v in new[] { 0, 1, -1, int.MinValue, int.MaxValue, len, len - 7, len - 8, l.FooterLength - 1, l.FooterLength + 1, l.FooterLength * 2 }.Distinct()) {
            byte[] m = (byte[])b.Clone();
            BitConverter.GetBytes(v).CopyTo(m, len - 8);
            yield return ($"footerlen={v}", m);
        }

        if(l.Meta == null)
            yield break;

        // hostile values in the footer
        foreach((string edit, Action<FileMetaData> apply) in MetaEdits()) {
            FileMetaData meta = Clone(l.Meta);
            apply(meta);
            yield return ($"meta:{edit}", l.WithFooter(b.AsSpan(0, l.FooterStart), meta));
        }

        // hostile values in page headers: the first pages, the last one, and two seeded ones
        var pages = new SortedSet<int> { 0, 1, 2, l.Pages.Count - 1, rnd.Next(l.Pages.Count), rnd.Next(l.Pages.Count) };
        foreach(int p in pages.Where(p => p < l.Pages.Count)) {
            foreach((string edit, Func<PageHeader, bool> apply) in PageEdits()) {
                bool applies = true;
                byte[] m = l.WithPageHeader(p, ph => applies = apply(ph));
                if(applies)
                    yield return ($"page{p}:{edit}", m);
            }
        }
    }

    static IEnumerable<(string, Action<FileMetaData>)> MetaEdits() {
        IEnumerable<ColumnMetaData> Cols(FileMetaData m) => m.RowGroups.SelectMany(g => g.Columns).Select(c => c.MetaData!);
        IEnumerable<SchemaElement> Leaves(FileMetaData m) => m.Schema.Skip(1).Where(e => e.NumChildren is null or 0);
        Statistics Stats(ColumnMetaData c) => c.Statistics ??= new Statistics();

        yield return ("rows=max", m => m.NumRows = long.MaxValue);
        yield return ("rg.rows=max", m => m.RowGroups.ForEach(g => g.NumRows = long.MaxValue));
        yield return ("rg.rows=-1", m => m.RowGroups.ForEach(g => g.NumRows = -1));
        yield return ("values=intmax", m => Cols(m).ToList().ForEach(c => c.NumValues = int.MaxValue));
        yield return ("values=2^28", m => Cols(m).ToList().ForEach(c => c.NumValues = 1 << 28));
        yield return ("values=longmax", m => Cols(m).ToList().ForEach(c => c.NumValues = long.MaxValue));
        yield return ("values=-1", m => Cols(m).ToList().ForEach(c => c.NumValues = -1));
        yield return ("values=0", m => Cols(m).ToList().ForEach(c => c.NumValues = 0));
        yield return ("values+1", m => Cols(m).ToList().ForEach(c => c.NumValues += 1));
        yield return ("dataoffset=-1", m => Cols(m).ToList().ForEach(c => { c.DataPageOffset = -1; c.DictionaryPageOffset = null; }));
        yield return ("dataoffset=eof", m => Cols(m).ToList().ForEach(c => { c.DataPageOffset = 1L << 40; c.DictionaryPageOffset = null; }));
        yield return ("dataoffset=footer", m => Cols(m).ToList().ForEach(c => { c.DataPageOffset = 4; c.DictionaryPageOffset = null; }));
        yield return ("dictoffset=-1", m => Cols(m).ToList().ForEach(c => c.DictionaryPageOffset = -1));
        yield return ("dictoffset=eof", m => Cols(m).ToList().ForEach(c => c.DictionaryPageOffset = 1L << 40));
        yield return ("compsize=longmax", m => Cols(m).ToList().ForEach(c => c.TotalCompressedSize = long.MaxValue));
        yield return ("compsize=-1", m => Cols(m).ToList().ForEach(c => c.TotalCompressedSize = -1));
        yield return ("nullcount=longmax", m => Cols(m).ToList().ForEach(c => Stats(c).NullCount = long.MaxValue));
        yield return ("nullcount=-1", m => Cols(m).ToList().ForEach(c => Stats(c).NullCount = -1));
        yield return ("nullcount=values+1", m => Cols(m).ToList().ForEach(c => Stats(c).NullCount = c.NumValues + 1));
        yield return ("codec=99", m => Cols(m).ToList().ForEach(c => c.Codec = (CompressionCodec)99));
        yield return ("coltype=99", m => Cols(m).ToList().ForEach(c => c.Type = (Meta.Type)99));
        yield return ("leaftype=99", m => Leaves(m).ToList().ForEach(e => e.Type = (Meta.Type)99));
        yield return ("leaftype=flba", m => Leaves(m).ToList().ForEach(e => e.Type = Meta.Type.FIXED_LEN_BYTE_ARRAY));
        yield return ("leaftype=int32", m => Leaves(m).ToList().ForEach(e => e.Type = Meta.Type.INT32));
        yield return ("leaftype=bytearray", m => Leaves(m).ToList().ForEach(e => e.Type = Meta.Type.BYTE_ARRAY));
        yield return ("typelength=-1", m => Leaves(m).ToList().ForEach(e => e.TypeLength = -1));
        yield return ("typelength=intmax", m => Leaves(m).ToList().ForEach(e => e.TypeLength = int.MaxValue));
        yield return ("flba.typelength=0", m => Leaves(m).ToList().ForEach(e => { e.Type = Meta.Type.FIXED_LEN_BYTE_ARRAY; e.TypeLength = 0; }));
        yield return ("children=intmax", m => m.Schema[0].NumChildren = int.MaxValue);
        yield return ("children=-1", m => m.Schema[0].NumChildren = -1);
        yield return ("children=0", m => m.Schema[0].NumChildren = 0);
        yield return ("children+1", m => m.Schema[0].NumChildren += 1);
        yield return ("children.self", m => m.Schema[^1].NumChildren = 1);
        yield return ("schema.droplast", m => m.Schema.RemoveAt(m.Schema.Count - 1));
        yield return ("schema.empty", m => m.Schema.Clear());
        yield return ("leafrep=repeated", m => Leaves(m).ToList().ForEach(e => e.RepetitionType = FieldRepetitionType.REPEATED));
        yield return ("leafrep=required", m => Leaves(m).ToList().ForEach(e => e.RepetitionType = FieldRepetitionType.REQUIRED));
        yield return ("decimal.scale>precision", m => Leaves(m).ToList().ForEach(e => { e.ConvertedType = ConvertedType.DECIMAL; e.Precision = 2; e.Scale = 40; }));
        yield return ("columns.droplast", m => m.RowGroups.ForEach(g => g.Columns.RemoveAt(g.Columns.Count - 1)));
        yield return ("columns.empty", m => m.RowGroups.ForEach(g => g.Columns.Clear()));
        yield return ("columns.metadata=null", m => m.RowGroups.ForEach(g => g.Columns.ForEach(c => c.MetaData = null)));
        yield return ("path=missing", m => Cols(m).ToList().ForEach(c => c.PathInSchema = ["no", "such", "column"]));
        yield return ("path=empty", m => Cols(m).ToList().ForEach(c => c.PathInSchema = []));
        yield return ("stats.min=junk", m => Cols(m).ToList().ForEach(c => { Stats(c).MinValue = [1]; Stats(c).MaxValue = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]; Stats(c).Min = [1]; Stats(c).Max = [2]; }));
    }

    /// <summary>Each edit returns false when it does not apply to that page.</summary>
    static IEnumerable<(string, Func<PageHeader, bool>)> PageEdits() {
        yield return ("compressed=-1", ph => { ph.CompressedPageSize = -1; return true; });
        yield return ("compressed=0", ph => { ph.CompressedPageSize = 0; return true; });
        yield return ("compressed=intmax", ph => { ph.CompressedPageSize = int.MaxValue; return true; });
        yield return ("uncompressed=-1", ph => { ph.UncompressedPageSize = -1; return true; });
        yield return ("uncompressed=0", ph => { ph.UncompressedPageSize = 0; return true; });
        yield return ("uncompressed=intmax", ph => { ph.UncompressedPageSize = int.MaxValue; return true; });
        yield return ("uncompressed=2^28", ph => { ph.UncompressedPageSize = 1 << 28; return true; });
        yield return ("type=99", ph => { ph.Type = (PageType)99; return true; });
        yield return ("type=index", ph => { ph.Type = PageType.INDEX_PAGE; return true; });
        yield return ("values=intmax", ph => SetValues(ph, int.MaxValue));
        yield return ("values=-1", ph => SetValues(ph, -1));
        yield return ("values=0", ph => SetValues(ph, 0));
        yield return ("encoding=99", ph => SetEncoding(ph, (Meta.Encoding)99));
        yield return ("encoding=delta", ph => SetEncoding(ph, Meta.Encoding.DELTA_BINARY_PACKED));
        yield return ("encoding=dict", ph => SetEncoding(ph, Meta.Encoding.RLE_DICTIONARY));
        yield return ("encoding=bss", ph => SetEncoding(ph, Meta.Encoding.BYTE_STREAM_SPLIT));
        yield return ("subheader=null", ph => { ph.DataPageHeader = null; ph.DataPageHeaderV2 = null; ph.DictionaryPageHeader = null; return true; });
        yield return ("v2.deflen=intmax", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.DefinitionLevelsByteLength = int.MaxValue));
        yield return ("v2.deflen=-1", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.DefinitionLevelsByteLength = -1));
        yield return ("v2.replen=intmax", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.RepetitionLevelsByteLength = int.MaxValue));
        yield return ("v2.replen=-1", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.RepetitionLevelsByteLength = -1));
        yield return ("v2.nulls=intmax", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.NumNulls = int.MaxValue));
        yield return ("v2.nulls=-1", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.NumNulls = -1));
        yield return ("v2.rows=-1", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.NumRows = -1));
        yield return ("v2.compressed=false", ph => ph.DataPageHeaderV2 is { } v && Set(() => v.IsCompressed = false));
    }

    static bool Set(Action a) {
        a();
        return true;
    }

    static bool SetValues(PageHeader ph, int n) {
        if(ph.DataPageHeader != null)
            ph.DataPageHeader.NumValues = n;
        else if(ph.DataPageHeaderV2 != null)
            ph.DataPageHeaderV2.NumValues = n;
        else if(ph.DictionaryPageHeader != null)
            ph.DictionaryPageHeader.NumValues = n;
        else
            return false;
        return true;
    }

    static bool SetEncoding(PageHeader ph, Meta.Encoding e) {
        if(ph.DataPageHeader != null)
            ph.DataPageHeader.Encoding = e;
        else if(ph.DataPageHeaderV2 != null)
            ph.DataPageHeaderV2.Encoding = e;
        else if(ph.DictionaryPageHeader != null)
            ph.DictionaryPageHeader.Encoding = e;
        else
            return false;
        return true;
    }

    // ---- running a case ----

    /// <param name="Kind">"ok", "allowed" (an <see cref="Allowed"/> error), "alloc", "timeout", or the exception type.</param>
    /// <param name="Frame">The innermost library frame the exception came from, for the record.</param>
    sealed record Outcome(string Kind, string Detail, string Frame, long Allocated, long Budget) {
        public bool Clean => Kind is "ok" or "allowed";
        public override string ToString() => $"{Kind} {Detail} (allocated {Allocated:N0} bytes, budget {Budget:N0})";
    }

    static readonly object KnownOutLock = new();

    static Outcome Run(string id) {
        (byte[] bytes, Func<ParquetOptions?> options) = AllCases.Value[id];
        Outcome o = Execute(bytes, options);
        string? path = Environment.GetEnvironmentVariable("PARQUET_MALFORMED_KNOWN_OUT");
        if(!o.Clean && !string.IsNullOrEmpty(path)) {
            lock(KnownOutLock)
                F.AppendAllText(path, $"{id}\t{o.Kind}\t{o.Frame}\n");
        }
        return o;
    }

    /// <summary>
    /// Opens the file from memory and reads every column of every row group, on a thread of its own so that a hang
    /// can be timed out and the bytes it allocates counted (the read completes synchronously over a MemoryStream).
    /// </summary>
    static Outcome Execute(byte[] bytes, Func<ParquetOptions?> options) {
        long budget = 64L * bytes.Length + (64L << 20);
        Exception? error = null;
        long allocated = 0;
        var thread = new Thread(() => {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try {
                ReadAllAsync(bytes, options()).GetAwaiter().GetResult();
            } catch(Exception e) {
                error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }) { IsBackground = true, Name = "malformed-file-case" };
        thread.Start();
        if(!thread.Join(CaseTimeout))
            return new Outcome("timeout", $"no result after {CaseTimeout.TotalSeconds}s", "", 0, budget);
        if(allocated > budget || error is OutOfMemoryException) {
            // give a huge allocation back before the next case, so that one case's garbage cannot fail another
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        if(error == null)
            return new Outcome(allocated > budget ? "alloc" : "ok", "", "", allocated, budget);

        // the CI build renames the library's namespace to Lake.Parquet; record names as the source has them
        string type = Unrename(error.GetType().FullName!);
        string frame = TopFrame(error);
        string detail = $"{type}: {error.Message.Split('\n')[0]} at {frame}";
        string kind = !Allowed.Any(t => t.IsInstanceOfType(error)) ? type : allocated > budget ? "alloc" : "allowed";
        return new Outcome(kind, detail, frame, allocated, budget);
    }

    static async Task ReadAllAsync(byte[] bytes, ParquetOptions? options) {
        using var ms = new MemoryStream(bytes, false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms, options);
        DataField[] fields = reader.Schema.GetDataFields();
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(g);
            foreach(DataField f in fields) {
                await (Task)ReadColumnMethod.MakeGenericMethod(f.ClrType).Invoke(null, [rg, f])!;
            }
        }
    }

    // The typed raw read of a column, for its CLR type. Not ParquetRowGroupReader.ReadRawColumnDataBaseAsync, which
    // makes the same call: Lake.Parquet leaves that member out of the package (lake(0045)), and this class runs on both builds.
    static readonly MethodInfo ReadColumnMethod =
        typeof(MalformedFileTest).GetMethod(nameof(ReadColumnAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    static async Task ReadColumnAsync<T>(ParquetRowGroupReader rg, DataField f) where T : struct {
        using Data.RawColumnData<T> data = await rg.ReadRawColumnDataAsync<T>(f);
    }

    static string Unrename(string name) => name.StartsWith("Lake.Parquet.", StringComparison.Ordinal) ? name[5..] : name;

    /// <summary>The innermost library frame of an exception's stack: where to look.</summary>
    static string TopFrame(Exception e) {
        foreach(string line in (e.StackTrace ?? "").Split('\n')) {
            string t = line.Trim();
            if(!t.StartsWith("at "))
                continue;
            t = Unrename(t[3..]);
            if(!t.StartsWith("Parquet.", StringComparison.Ordinal) || t.StartsWith("Parquet.Test.", StringComparison.Ordinal))
                continue;
            int paren = t.IndexOf('(');
            string method = paren > 0 ? t[..paren] : t;
            int lineNo = t.LastIndexOf(":line ", StringComparison.Ordinal);
            return lineNo > 0 ? $"{method}:{t[(lineNo + 6)..]}" : method;
        }
        return "(no library frame)";
    }

    // ---- the file's structure ----

    static FileMetaData Clone(FileMetaData meta) {
        using var ms = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(ms));
        ms.Position = 0;
        return FileMetaData.Read(new ThriftCompactProtocolReader(ms));
    }

    static int StableHash(string s) {
        unchecked {
            int h = (int)2166136261;
            foreach(char c in s)
                h = (h ^ c) * 16777619;
            return h;
        }
    }

    sealed class Layout {
        public byte[] Bytes = [];
        public int FooterStart;
        public int FooterLength;
        /// <summary>The footer, when it is plaintext and the columns are not encrypted (so it can be rewritten).</summary>
        public FileMetaData? Meta;
        /// <summary>Every page header, in file order: where it starts, how long it is, and the header.</summary>
        public List<(int Start, int Length, PageHeader Header)> Pages = [];

        public static Layout Of(byte[] b) {
            var l = new Layout { Bytes = b, FooterLength = BitConverter.ToInt32(b, b.Length - 8) };
            l.FooterStart = b.Length - 8 - l.FooterLength;
            if(b[^1] != (byte)'1')
                return l;   // PARE: encrypted footer
            FileMetaData meta = FileMetaData.Read(new ThriftCompactProtocolReader(new MemoryStream(b, l.FooterStart, l.FooterLength)));
            if(meta.EncryptionAlgorithm != null)
                return l;   // signed plaintext footer, encrypted page headers
            l.Meta = meta;
            foreach(ColumnChunk cc in meta.RowGroups.SelectMany(g => g.Columns)) {
                ColumnMetaData cmd = cc.MetaData!;
                long pos = cmd.DictionaryPageOffset is > 0 ? cmd.DictionaryPageOffset.Value : cmd.DataPageOffset;
                long values = 0;
                while(values < cmd.NumValues) {
                    var ms = new MemoryStream(b, (int)pos, l.FooterStart - (int)pos);
                    PageHeader ph = PageHeader.Read(new ThriftCompactProtocolReader(ms));
                    l.Pages.Add(((int)pos, (int)ms.Position, ph));
                    values += ph.DataPageHeader?.NumValues ?? ph.DataPageHeaderV2?.NumValues ?? 0;
                    pos += ms.Position + ph.CompressedPageSize;
                }
            }
            return l;
        }

        public byte[] WithFooter(ReadOnlySpan<byte> body, FileMetaData meta) {
            using var ms = new MemoryStream();
            ms.Write(body);
            long start = ms.Position;
            meta.Write(new ThriftCompactProtocolWriter(ms));
            ms.Write(BitConverter.GetBytes((int)(ms.Position - start)));
            ms.Write("PAR1"u8);
            return ms.ToArray();
        }

        /// <summary>
        /// Rewrites one page header. When its length changes, everything after it moves, and the footer's offsets
        /// and sizes are moved with it, so that only the edited fields differ.
        /// </summary>
        public byte[] WithPageHeader(int page, Action<PageHeader> edit) {
            (int start, int length, PageHeader original) = Pages[page];
            PageHeader ph = ReadHeader(WriteHeader(original));
            edit(ph);
            byte[] header = WriteHeader(ph);
            int delta = header.Length - length;

            byte[] body = [.. Bytes.AsSpan(0, start), .. header, .. Bytes.AsSpan(start + length, FooterStart - start - length)];
            FileMetaData meta = Clone(Meta!);
            foreach(RowGroup g in meta.RowGroups) {
                foreach(ColumnChunk cc in g.Columns) {
                    ColumnMetaData c = cc.MetaData!;
                    long first = c.DictionaryPageOffset is > 0 ? c.DictionaryPageOffset.Value : c.DataPageOffset;
                    if(start >= first && start < first + c.TotalCompressedSize) {
                        // both totals include the page headers
                        c.TotalCompressedSize += delta;
                        c.TotalUncompressedSize += delta;
                    }
                    c.DataPageOffset = Shift(c.DataPageOffset);
                    c.DictionaryPageOffset = ShiftN(c.DictionaryPageOffset);
                    c.IndexPageOffset = ShiftN(c.IndexPageOffset);
                    c.BloomFilterOffset = ShiftN(c.BloomFilterOffset);
                    cc.FileOffset = Shift(cc.FileOffset);
                    cc.ColumnIndexOffset = ShiftN(cc.ColumnIndexOffset);
                    cc.OffsetIndexOffset = ShiftN(cc.OffsetIndexOffset);
                }
                g.FileOffset = ShiftN(g.FileOffset);
            }
            return WithFooter(body, meta);

            long Shift(long o) => o > start ? o + delta : o;
            long? ShiftN(long? o) => o is long v ? Shift(v) : null;
        }

        static byte[] WriteHeader(PageHeader ph) {
            using var ms = new MemoryStream();
            ph.Write(new ThriftCompactProtocolWriter(ms));
            return ms.ToArray();
        }

        static PageHeader ReadHeader(byte[] b) => PageHeader.Read(new ThriftCompactProtocolReader(new MemoryStream(b)));
    }
}
