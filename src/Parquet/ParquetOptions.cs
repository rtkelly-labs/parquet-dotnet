using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;

namespace Parquet;

/// <summary>
/// Parquet options
/// </summary>
public class ParquetOptions {

    /// <summary>
    /// Compression method to use when writing, defaults to <see cref="CompressionMethod.Snappy"/>
    /// </summary>
    public CompressionMethod CompressionMethod { get; set; } = CompressionMethod.Snappy;

    /// <summary>
    /// Compression level to use when writing, defaults to <see cref="CompressionLevel.SmallestSize"/>. The actual
    /// physical compression level varies based on the compression algorithm.
    /// </summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// When true byte arrays will be treated as UTF-8 strings when reading files.
    /// </summary>
    public bool TreatByteArrayAsString { get; set; } = false;

    /// <summary>
    /// When true, big integers are always treated as dates when reading files.
    /// </summary>
    public bool TreatBigIntegersAsDates { get; set; } = true;

    /// <summary>
    /// When set to true, parquet dates will be deserialized as <see cref="DateOnly"/>, otherwise as
    /// <see cref="DateTime"/> with missing time part.
    /// </summary>
    public bool UseDateOnlyTypeForDates { get; set; } = false;

    /// <summary>
    /// Specifies hints to the writers about which encoding to use for specific columns. To get column path use
    /// <see cref="Field.Path"/> on <see cref="DataField"/>.
    /// </summary>
    public readonly IDictionary<string, EncodingHint> ColumnEncodingHints = new Dictionary<string, EncodingHint>();

    internal EncodingHint GetEncodingHint(DataField df) =>
        ColumnEncodingHints.TryGetValue(df.Path.ToString(), out EncodingHint hint) ? hint : EncodingHint.Default;

    /// <summary>
    /// Dictionary uniqueness threshold, which is a value from 0 (no unique values) to 1 (all values are unique)
    /// indicating when dictionary encoding is applied. Uniqueness factor needs to be less or equal than this threshold.
    /// </summary>
    public double DictionaryEncodingThreshold { get; set; } = 0.8;

    /// <summary>
    /// Number of values to sample before attempting full dictionary encoding.
    /// When the column has more values than this limit, a quick uniqueness check is performed
    /// on the first <c>DictionaryEncodingSampleSize</c> values. If the sample exceeds
    /// <see cref="DictionaryEncodingThreshold"/>, dictionary encoding is skipped entirely,
    /// avoiding the expensive full-data scan.
    /// Set to 0 to disable sampling and always scan the full column.
    /// Default is 0 (disabled - full column scan, preserving pre-existing behavior).
    /// </summary>
    public int DictionaryEncodingSampleSize { get; set; } = 0;

    /// <summary>
    /// When set, the writer splits each column chunk into data pages of about this many values (levels, nulls
    /// included): a page ends at the first row boundary at or after the limit, so a row is never split across
    /// pages. Smaller pages bound the memory a reader and writer need per page. The chunk's statistics still cover
    /// every value; pages of a chunk that has several carry none of their own. Unset (the default), each chunk is
    /// one data page, as before.
    /// </summary>
    public int? MaxDataPageValues { get; set; }

    /// <summary>
    /// This option is passed to the <see cref="Microsoft.IO.RecyclableMemoryStreamManager"/>, which keeps a pool of
    /// streams in memory for reuse. By default when this option is unset, the RecyclableStreamManager will keep an
    /// unbounded amount of memory, which is "indistinguishable from a memory leak" per their documentation. This does
    /// not restrict the size of the pool, but just allows the garbage collector to free unused memory over this limit.
    /// You may want to adjust this smaller to reduce max memory usage, or larger to reduce garbage collection
    /// frequency. Defaults to 16MB.
    /// </summary>
    public int MaximumSmallPoolFreeBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// This option is passed to the <see cref="Microsoft.IO.RecyclableMemoryStreamManager"/>, which keeps a pool of
    /// streams in memory for reuse. By default when this option is unset, the RecyclableStreamManager will keep an
    /// unbounded amount of memory, which is "indistinguishable from a memory leak" per their documentation. This does
    /// not restrict the size of the pool, but just allows the garbage collector to free unused memory over this limit.
    /// You may want to adjust this smaller to reduce max memory usage, or larger to reduce garbage collection
    /// frequency. Defaults to 64MB.
    /// </summary>
    public int MaximumLargePoolFreeBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// When true, decimals will be read and written as <see cref="BigDecimal"/> instead of <see cref="decimal"/>. This
    /// is required if you are working with truly large decimals.
    /// </summary>
    public bool UseBigDecimal { get; set; } = false;

    /// <summary>
    /// Write min/max/null-count statistics in column chunk metadata and data page headers (the default). Files whose
    /// statistics are never used for pruning (position delete files, for instance) can leave them out.
    /// </summary>
    public bool WriteStatistics { get; set; } = true;

    /// <summary>
    /// When true, the writer writes a page index for every column chunk (parquet-format PageIndex.md): an offset index
    /// (where each data page starts, its size and its first row) and, for columns with a defined sort order, a column
    /// index (each page's minimum, maximum and null count), between the last row group and the footer. Readers such as
    /// DuckDB, Spark and Trino use it to skip pages. Off by default: it changes the files written.
    /// </summary>
    public bool WritePageIndex { get; set; } = false;

    /// <summary>
    /// Columns to write a split block bloom filter for (parquet-format BloomFilter.md), keyed by column path as in
    /// <see cref="ColumnEncodingHints"/>, with the false positive probability to size each chunk's filter for (0.01 is
    /// common). Bloom filters suit high-cardinality columns looked up by equality, which bounds cannot prune; they are
    /// not defined for BOOLEAN columns. Empty by default.
    /// </summary>
    public Dictionary<string, double> BloomFilters { get; } = new();

    /// <summary>
    /// Check the magic bytes at the start of a file as well as at its end (default). Readers over ranged storage turn
    /// this off to save a request for the first four bytes; the footer and its trailing magic are always checked.
    /// </summary>
    public bool ValidateHeaderMagic { get; set; } = true;

    /// <summary>
    /// Write files with Parquet modular encryption (AES-GCM) using these keys. Not supported when appending.
    /// </summary>
    public Encryption.ParquetEncryptionOptions? Encryption { get; set; }

    /// <summary>Keys for reading files with Parquet modular encryption.</summary>
    public Encryption.ParquetDecryptionOptions? Decryption { get; set; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    internal Type DecimalType => UseBigDecimal ? typeof(BigDecimal) : typeof(decimal);

    /// <summary>
    /// The Default Precision value used when not explicitly defined; this is the value used prior to parquet-dotnet
    /// v3.9.
    /// </summary>
    public const int DefaultPrecision = 38;

    /// <summary>
    /// The Default Scale value used when not explicitly defined; this is the value used prior to parquet-dotnet v3.9.
    /// </summary>
    public const int DefaultScale = 18;

    /// <summary>
    /// If true, will attemp to use hardware accelerated math.
    /// </summary>
    public static bool UseHardwareAcceleration { get; set; } = true;

    #region [ Serializer specific ]

    /// <summary>
    /// When set to true, appends to file by creating a new row group. Only applicable when using
    /// <see cref="ParquetSerializer"/>. Be careul not to create a lot of tiny row groups as this affects reading
    /// performance in all Parquet readers, not just this library.
    /// </summary>
    public bool Append { get; set; } = false;

    /// <summary>
    /// Default size of row groups if not specified
    /// </summary>
    public const int DefaultRowGroupSize = 1_000_000;

    /// <summary>
    /// Custom row group size, if different from <see cref="DefaultRowGroupSize"/> used by
    /// <see cref="ParquetSerializer"/>.
    /// </summary>
    public int? RowGroupSize { get; set; }

    /// <summary>
    /// Gets or sets a value that indicates whether a property's name uses a case-insensitive comparison during
    /// deserialization. The default value is false. Full credits to
    /// https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.propertynamecaseinsensitive?view=net-8.0#system-text-json-jsonserializeroptions-propertynamecaseinsensitive
    /// </summary>
    public bool PropertyNameCaseInsensitive { get; set; } = false;

    /// <summary>
    /// When using untyped serialisation, prefers using <see cref="System.String"/> when reading string values, instead
    /// of <see cref="System.ReadOnlyMemory{Char}"/>.
    /// </summary>
    public static bool PreferUntypedString { get; set; } = true;

    /// <summary>
    /// When using untyped serialisation, prefers using <see cref="byte"/> array when reading byte array values, instead
    /// of <see cref="System.ReadOnlyMemory{Byte}"/>.
    /// </summary>
    public static bool PreferUntypedByteArray { get; set; } = true;

    #endregion
}
