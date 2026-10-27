# Fork changes: a review guide for upstreaming

This fork, published as **Lake.Parquet** ([lake/README.md](lake/README.md); maintenance in [lake/maintaining.md](lake/maintaining.md)), on branch `ccr-87fadcf9-bxi9wr`, diverges from upstream `master` at `658e1ca`. Every change on it was
machine-written. Upstream does not accept LLM-written issues, pull requests or comments (`docs/README.md`), so anything
proposed upstream has to be reviewed, understood and rewritten (or at least re-owned) by a person first. This page
splits the fork into **review units**: each small enough to check on its own and, once you are satisfied, to propose
as one hand-written PR.

The fork's commits do not map one-to-one onto units. Some commits bundle two unrelated changes, and two commits fix
an earlier fork commit. The **Commits** line of each unit says which parts of which commits it consists of, and
**Not upstream as is** notes what to squash or leave out.

## How to review a unit

Every unit also exists as a **verified patch**, on its own and as an ordered series, in [`upstream/`](upstream/):
see [Verified patches](#verified-patches) below. Start from those rather than from the fork's commits.

1. Read the problem statement and reproduce it on upstream `master` (`git checkout 658e1ca`): the unit's test, or the
   repro given, should fail there. A unit whose problem you cannot reproduce should not go upstream.
2. Read the diff: `git diff 658e1ca ccr-87fadcf9-bxi9wr -- <files>` shows the fork's final state of the unit's files,
   which may include other units' changes in the same file. `git show <commit> -- <files>` shows one commit's part.
3. Check the points under **Review** (the places I am least sure of, or that are judgement calls for upstream).
4. Run the fork suite: `dotnet test src/Parquet.Test` (772 passed, 3 skipped on each of net8.0 and net10.0 at the branch
   head on Linux, with `pyarrow` installed for one Arrow interop test).

What has **not** been verified, for every unit:

- **Windows and macOS** locally: the fork's own Actions workflow is not enabled, and everything here ran on Linux.
  Iceberg.NET's CI runs this suite on Linux, Windows and macOS (job `parquet-net-fork`, from the submodule); check its
  latest run for those platforms.
- **Each unit alone on upstream `master`.** The units were developed one on top of another. Applying one by itself
  may need small adjustments (a shared file, a test helper).
- **Upstream's conventions.** Code style follows the surrounding code as far as I could tell, but upstream's
  preferences on API shape, naming, tests and release notes are yours to judge.

## Suggested order

Small, self-contained bug fixes first: each has a clear repro, a small diff and no API change, so they are the easiest
to get accepted and they build trust for the larger features.

| # | Unit | Kind | Size | Depends on |
|---|---|---|---|---|
| 1 | Timestamp statistics in the column's unit | bug | tiny | |
| 2 | Write `column_orders` | bug | tiny | |
| 3 | Required maps written `OPTIONAL` | bug | tiny | |
| 4 | `List<T?>` deserialization drops nulls and empty lists | bug | small | |
| 5 | `DATA_PAGE_V2` with repetition levels | bug | small | |
| 6 | `dictionary_page_offset` never written | bug | tiny | |
| 7 | UUID statistics byte order and ordering | bug | small | |
| 8 | Statistics in each type's sort order (with 2) | bug | medium | (7) |
| 8b | `BYTE_STREAM_SPLIT` columns get no statistics | bug | tiny | |
| 9 | Statistics decoded as the column's type | bug | small | |
| 10 | Zstd decompression without double copies | perf | small | |
| 10b | Byte-array and text values: one array per page | perf | small | |
| 10c | Tests on net8.0 too (one test did not compile there) | tests | tiny | |
| 11 | Field IDs on every field, read back | feature | small | |
| 12 | `FIXED_LEN_BYTE_ARRAY(n)` byte arrays | feature | small | |
| 13 | `ParquetOptions.WriteStatistics` | feature | tiny | |
| 14 | Physical values (`DataField.PhysicalClrType`) | feature | small | |
| 15 | Legacy list shapes (rules 2 and 3) and name-matched deserialization | feature + behaviour change | medium | |
| 16 | Typed statistics API (sort order, raw and physical bounds) | feature | medium | 9 |
| 17 | `ParquetReader.Columns`, `FooterLength` | feature | small | 16 |
| 18 | `RawDataField` | feature | small | 14 |
| 19 | `ParquetOptions.ValidateHeaderMagic` | feature | tiny | |
| 20 | Modular encryption | feature | large | 6 |
| 21 | Interop tests against parquet-cpp | tests | medium | |

---

## 1. Timestamp statistics in the column's unit

- **Commits:** part of `7ecfa00` (`ParquetPlainEncoder.TryEncode(DateTime…)`, `TimestampStatisticsTest`).
- **Problem:** min/max of an `INT64` timestamp column are always encoded as milliseconds, whatever the column's unit.
  A `TIMESTAMP(MICROS)` file has bounds 1000× too small, so engines that prune on statistics skip row groups that
  match. Repro: write `DateTimeDataField(... unit: Micros)` with 1970-01-01T00:00:02, then in DuckDB
  `SELECT count(*) FROM 'f.parquet' WHERE ts > TIMESTAMP '1970-01-01 00:00:01'` returns 0 (1 after the fix).
- **Change:** the statistics bytes are produced by the same `Encode(ReadOnlySpan<DateTime>…)` the values go through.
- **Review:** `INT96` and `INT32` (date) paths unchanged; check `TIMESTAMP(NANOS)` too (the test covers millis,
  micros and nanos).

## 2. Write `column_orders`

- **Commits:** `792c737`.
- **Problem:** the footer has no `column_orders`. parquet-cpp (pyarrow, DuckDB) and parquet-mr then trust min/max of
  unsigned-order columns (strings, binary, fixed) only from writers they recognise, so every string bound written by
  Parquet.Net is ignored for pruning. Repro: `pyarrow.parquet.ParquetFile(f).metadata.row_group(0).column(i).statistics.has_min_max`
  is `False` for a string column.
- **Change:** one `TYPE_ORDER` per leaf in `ThriftFooter`'s constructor for new files.
- **Review:** this claims the statistics follow the type-defined order, which is only true once unit 8 is in (strings
  were compared by UTF-16 code unit, unsigned ints signed). Propose it **with or after unit 8**, or readers will
  start trusting bounds that are occasionally wrong. Appending to an existing file keeps that file's orders.
  The test uses ParquetSharp (unit 21); without that dependency, assert on `FileMetaData.ColumnOrders` instead.

## 3. Required maps written `OPTIONAL`

- **Commits:** part of `02925d8` (`SchemaEncoder.Encode(MapField…)`, one test in `RawDataFieldTest`).
- **Problem:** `new MapField(..., isNullable: false)` is encoded `OPTIONAL` while its levels are computed as required:
  the levels written disagree with the file's own schema.
- **Change:** repetition from `mapField.IsNullable`, as lists and structs already do.
- **Review:** the test uses `RawDataField` (unit 18); rewrite it with ordinary `DataField`s for an upstream PR.

## 4. `List<T?>` deserialization drops nulls and empty lists

- **Commits:** part of `ff03dcc` (`FieldAssemblerCompiler.cs`, `NullableListElementTest`).
- **Problem:** `ParquetSerializer` reads `[1, null, 2]` back as `[1, 2]` and `[]` as `null`, for `List<int?>`,
  `List<string?>` and `int?[]`, including files it wrote itself (the levels on disk are right).
- **Change:** the element exists from the repeated level (`ElementLevel`), and an element below the item's maximum
  definition level is added as `default` (null).
- **Review:** the expression-tree compiler is the most delicate code touched. Check non-nullable lists, arrays and
  nested lists of structs still assemble (the whole serializer suite passes). Check the untyped (`Dictionary`) path.

## 5. `DATA_PAGE_V2` with repetition levels

- **Commits:** `bfd86da`.
- **Problem:** reading a v2 page of a repeated column throws `NotImplementedException`; compressed v2 pages were also
  decompressed to the whole page's size rather than the values section's.
- **Change:** levels come from the byte lengths in the header (v2 has no length prefix), present values are counted
  from the definition levels (`NumNulls` counts empty and null lists too), only the values section is decompressed.
- **Review:** test files `data/v2/*.parquet` were written by parquet-cpp (ParquetSharp 25.0.1.1); upstream may prefer
  fixtures from `apache/parquet-testing`.

## 6. `dictionary_page_offset` never written

- **Commits:** part of `344675a` (`DataColumnWriter.cs`, the two `chunk.MetaData!.…PageOffset = _stream.Position` lines).
- **Problem:** the writer never sets `dictionary_page_offset` and points `data_page_offset` at the dictionary page.
- **Change:** each set to the position of its page.
- **Review:** no dedicated test exists for this alone: add one asserting both offsets. Readers tolerate the old
  layout, so the motivation upstream is spec conformance (and encryption, unit 20, which needs it).

## 7. UUID statistics byte order and ordering

- **Commits:** part of `7ecfa00` (`TryEncode(Guid)`: `ToByteArray(bigEndian: true)`) and part of `f12c75b`
  (`StatsCompute.Compute(ReadOnlySpan<Guid>…)` compares big-endian bytes).
- **Problem:** UUID bounds were written in .NET's mixed-endian `Guid` layout while the values use RFC 4122 big-endian
  bytes, and min/max were chosen with `Guid.CompareTo`, which is not byte order either.
- **Change:** bounds written and chosen in big-endian byte order, and read back (`TryDecode`, UUID part of `35bd5c1`)
  as the `Guid`s they are, so a round trip gives the values written.
- **Review:** `UuidStatisticsTest` and `RawDataFieldTest.Strings_compare_by_code_point_and_uuids_by_their_bytes`.

## 8. Statistics in each type's sort order

- **Commits:** parts of `f12c75b` (`StatsCompute.cs`, the `ParquetPlainEncoder.Encode` branches for `int`, `long`,
  `ReadOnlyMemory<byte>`) and `229f9d7` (booleans).
- **Problem:** min/max not in the order the format defines: INT32/INT64 annotated unsigned but written as their bits
  compared signed; strings compared by UTF-16 code unit (U+E000–U+FFFF sort after supplementary characters, unlike
  UTF-8 byte order); float bounds could be NaN, and zero bounds were not -0.0/+0.0; byte arrays (binary, fixed) and
  booleans got no bounds at all.
- **Change:** unsigned comparison where the annotation says unsigned; a code-point comparison for strings;
  NaN skipped and zeros signed for floats; byte-wise bounds for byte arrays that sort unsigned (none for byte-array
  decimals and INTERVAL, none above 4 KB as parquet-cpp); `false`/`true` for booleans.
- **Also (`a28da59`), required once `column_orders` says TYPE_ORDER (unit 2):** `nan_count` on every floating point
  column, even when zero (parquet.thrift: "The nan_count field must be set for floating point types"); the
  deprecated `min`/`max`, which their readers compare signed, written only for columns whose order is signed, as
  parquet-cpp does (strings, byte arrays and unsigned integers get `min_value`/`max_value` only).
- **Review:** the 4 KB cut-off drops bounds rather than truncating them (parquet-cpp truncates with a rounded-up max);
  truncation would keep pruning for long values. `CompareCodePoints` is the classic UTF-16 fix-up; check it.

## 8b. `BYTE_STREAM_SPLIT` columns get no statistics

- **Commits:** part of `a28da59` (`DataColumnWriter.cs`, one line).
- **Problem:** a column written with `EncodingHint.ByteSplitStream` gets no min/max at all: that branch never computes
  statistics, so engines cannot prune on it.
- **Change:** the plain encoder (which computes them) also runs into `Stream.Null`; writes there are no-ops, so the
  added cost is the min/max pass.
- **Review:** a dedicated statistics entry point (without the encoder dispatch) would be cleaner; this is the minimal
  version.

## 9. Statistics decoded as the column's type

- **Commits:** part of `35bd5c1` (`ParquetPlainEncoder.TryDecode`).
- **Problem:** `DataColumnStatistics.MinValue/MaxValue` of decimals stored as INT32/INT64 came back as raw unscaled
  integers (the decimal branch was unreachable after the INT32/INT64 branches), unsigned ints as their signed bits
  (a `uint64` max of 2^64−1 as −1), UUIDs (FLBA) as nothing. The UUID part moved to unit 7's patch, since it
  only round-trips with unit 7's byte order.
- **Review — behaviour change:** callers that worked around the old types (e.g. divided by the scale themselves) will
  see different values. Upstream may want this called out in release notes.

## 10. Zstd decompression without double copies

- **Commits:** `cbcc43d` **and** the `Compressor.cs` part of `12b2856`, squashed: `cbcc43d` alone has a race (the
  per-thread decompressor was taken before an `await`, so concurrent reads could share one; it showed up as "Data
  corruption detected" in parallel ranged reads). **Never propose `cbcc43d` without the fix.**
- **Problem:** decompression copied the compressed page into a new array, let ZstdSharp allocate the output, then
  copied that again.
- **Measured on its own** (patch on upstream, 1M rows of string + binary + int64 in 4 row groups, best of 7):
  allocation per Zstd read 208 → 137 MB; time unchanged (252 → 250 ms). The read time gain seen in the fork comes
  from unit 10b. Snappy files are unaffected.
- **Review:** `ConcurrentZstdTest` is a smoke test only, it did not reproduce the race on the unfixed code. Judge
  whether a `[ThreadStatic]` cache is acceptable upstream at all (simplest alternative: a new `Decompressor` per page,
  keeping the single-copy part). The frame-header size fallback covers frames without a content size.

## 10b. Byte-array and text values: one array per page

- **Commits:** `003c77f`.
- **Problem:** PLAIN decoding of BYTE_ARRAY / FIXED_LEN_BYTE_ARRAY allocated a `byte[]` per value, and text a
  `char[]` per value. Reading 1M UUIDs or strings spent as much time allocating as decoding.
- **Change:** each page is copied once into one array, and every value is a slice of it.
- **Measured on its own** (same benchmark as unit 10): read 252 → 119 ms (Zstd) and 241 → 114 ms (Snappy), allocation
  208 → 170 MB.
- **Review — trade-off:** a value the caller keeps alive keeps its whole page's array alive (one `ReadOnlyMemory`
  slice pins the page). For callers that keep a few values out of large pages that is more memory than before; upstream
  may prefer it behind an option. The char buffer is sized at one char per byte, which UTF-8 never exceeds.

## 10c. Tests on net8.0 too

- **Commits:** `c093dc0`.
- **Problem:** the library targets net8.0 and net10.0 but its tests ran on net10.0 only. One upstream test
  (`BigDecimalTest`) does not compile for net8.0: under C# 14, `bytes.Reverse()` binds to the in-place `Span`
  overload, which returns `void`.
- **Change:** `Enumerable.Reverse(bytes)`; the test project targets both. Both pass (772 each at the branch head).

## 11. Field IDs on every field, read back

- **Commits:** `927bf99`.
- **Problem:** `DataField.FieldId` was written but never read (a file written with `FieldId = 7` reads back −1), and
  structs, lists and maps had none.
- **Change:** `FieldId` moved from `DataField` to `Field`; decoded for every field; written on list, map and struct
  groups.
- **Review — compatibility:** source-compatible (`DataField` inherits it). Binary compatibility of moving a property
  to a base class should hold (member references resolve through base types) but I have not tested an assembly
  compiled against the old version. Check `Field.Equals` does not need to consider it.

## 12. `FIXED_LEN_BYTE_ARRAY(n)` byte arrays

- **Commits:** part of `54efd9a` (`DataField.FixedLength`, `SchemaEncoder`, `EncodeFixed`, `FixedLengthByteArrayTest`).
- **Review:** API naming (`FixedLength` on `DataField`, `int?`). A value of the wrong length throws `ParquetException`.

## 13. `ParquetOptions.WriteStatistics`

- **Commits:** part of `54efd9a` (`ParquetOptions`, `DataColumnWriter`, `WriteStatisticsOptionTest` in
  `TimestampStatisticsTest.cs`).
- **Review:** the footer's `Statistics` object is set to null explicitly (it is pre-created otherwise).

## 14. Physical values (`DataField.PhysicalClrType`)

- **Commits:** part of `7ecfa00` (`DataField.PhysicalClrType`, `IsCompatibleWith`, `WritingColumn.Validate`,
  `PhysicalValuesTest`).
- **Problem:** `ReadRawAsync<T>`/`WriteAsync<T>` require the logical CLR type: `TIMESTAMP(NANOS)` cannot be read as
  `long` nanoseconds (and `DateTime` loses the last two digits), dates not as `int` days, decimals not unscaled.
- **Review:** `PhysicalClrType` re-encodes the schema element when the field is not attached to a file
  (`SchemaEncoder.Encode(this, new ParquetOptions())`) on every call; cache it if upstream minds. Check that a
  physical write keeps the logical annotation and the statistics stay right (`PhysicalValuesTest`).

## 15. Legacy list shapes and name-matched deserialization

- **Commits:** parts of `ff03dcc` (`SchemaEncoder.TryBuildList`/`TryBuildStruct`, `ParquetSerializer.MakeForReading`
  and `LogicalPaths`, `LegacyListTest`, `data/legacy/*.parquet`).
- **Problem:** the backward-compatibility rules 2 and 3 of LogicalTypes.md (a repeated group with several fields, or
  one field and named `array` or `<list>_tuple`, *is* the element) were not applied; and when a file's list group
  names differ from the class's (`array`, `bag`), `ParquetSerializer` found no column and silently left the property
  null.
- **Review — behaviour change:** for such files the decoded schema changes from `List<int>` (the inner field as element)
  to `List<struct { item }>` (the spec's reading). Code that relied on the old shape breaks; it was wrong per the
  spec, but upstream must agree. The name fallback in `MakeForReading` is a second, separable PR.

## 16. Typed statistics API

- **Commits:** the rest of `35bd5c1` (`DataColumnStatistics` members, `ColumnSortOrder`, `ColumnStatisticsRules`,
  `ParquetRowGroupReader.ReadColumnStatistics`, `StatisticsSortOrderTest`).
- **Change:** `SortOrder`, `HasMinMax`, `MinBytes`/`MaxBytes`, `PhysicalMin`/`PhysicalMax`, `IsMinExact`/`IsMaxExact`,
  `NanCount`, `IsLegacyMinMax`; deprecated `min`/`max` only for signed numeric columns; nothing for unknown orders.
- **Review — behaviour change:** `INT96` (and INTERVAL) statistics no longer decode (`MinValue` null): their order is
  undefined, but someone may read them today. The API surface is a design discussion for upstream.

## 17. `ParquetReader.Columns`, `FooterLength`

- **Commits:** `da1574b`. A `ColumnDescriptor` per leaf, straight from the thrift schema.
- **Review:** the name `ColumnDescriptor` matches ParquetSharp's; check it does not clash with anything upstream.

## 18. `RawDataField`

- **Commits:** the `RawDataField` parts of `f12c75b`, `02925d8` and `326b529` (squash: `326b529` fixes a bug in
  `f12c75b`, where byte-array raw fields became repeated because `typeof(byte[])` is discovered as an array).
- **Change:** a column declared exactly as stored (physical type, logical and converted type, length), read and
  written as physical values; text columns also as strings.
- **Review:** the most "table-format specific" unit; upstream may prefer extending `DataField` instead. It is what
  makes Parquet.Net usable by Iceberg/DuckLake without CLR-type mapping.

## 19. `ParquetOptions.ValidateHeaderMagic`

- **Commits:** the `ParquetActor`/`ParquetOptions`/`ParquetReader` parts of `12b2856`, with
  `Header_magic_check_can_be_skipped`.
- **Review:** small and optional: it saves a request over ranged storage (parquet-cpp does not read the head).

## 20. Modular encryption

- **Commits:** `344675a` without the unit 6 lines (propose unit 6 first). Fixtures in `data/encryption/`.
- **Change:** AES_GCM_V1 and AES_GCM_CTR_V1; encrypted (`PARE`) or signed plaintext footers; footer key for all
  columns or separate column keys; key metadata and a key retriever; AAD prefixes stored or supplied. BCL crypto only.
- **Verified:** interop both ways with parquet-cpp in all five modes (fixtures written by ParquetSharp read here;
  files written here read by ParquetSharp in `ParquetCppInteropTest`); DuckDB 1.5.5 reads GCM files in full scans.
- **Review — security-sensitive, the unit that most needs an expert read:**
  - nonces: 12 random bytes per module from `RandomNumberGenerator` (check no reuse path);
  - AAD construction (module type, row group, column and page ordinals as little-endian int16) against
    parquet-format `Encryption.md`;
  - AES-CTR is implemented over AES-ECB (`AesModules.Ctr`): counter block = nonce ‖ 32-bit big-endian counter from 1;
  - plaintext-footer signature check uses `CryptographicOperations.FixedTimeEquals`;
  - error messages must not leak key material (they do not include keys);
  - which column metadata is encrypted vs stripped follows parquet-cpp's `ColumnChunkMetaDataBuilder::WriteTo`.
  - Not implemented: column index / offset index and bloom filter modules (Parquet.Net writes neither), appending to
    encrypted files (refused), key rotation.

## 21. Interop tests against parquet-cpp

- **Commits:** `ed5dd6f` (`ParquetCppInteropTest`, ParquetSharp as a test-only package reference), later additions in
  `792c737`.
- **Review:** upstream may not want a native test dependency. The alternative is committed fixture files written by
  parquet-cpp (as units 5, 15 and 20 already do) plus pyarrow checks in the existing Python interop tests.

## Verified patches

Every unit exists as a patch in [`upstream/`](upstream/), in two forms:

- **`upstream/NN-*.patch`, one per unit**, for proposing a unit on its own. Most apply to upstream `master`
  (`658e1ca`); those that need another unit first say so under **Applies to**. For each, the unit's tests were run
  **without** the library change (bug units: they fail; feature units: they do not compile, the API being new) and
  the whole suite **with** it (it must pass), on Linux, .NET 10. All are `git diff --binary`: `git apply` them.
- **`upstream/series/NN-*.patch`, the same units in order**, each applying on top of the previous ones with no
  conflicts, as if upstream merged them in this order. The overlaps between units are resolved there (listed below).
  Applied in order to a clean `master`, the series gives a library identical to the fork's apart from comment
  wording and one equivalent rewrite (the fixed-length branch of `TryDecode`), and its suite passes: **792 passed,
  3 skipped on net8.0 and on net10.0** (pyarrow and pandas installed).

| Patch | Unit | Applies to | Tests without the change | Suite with it |
|---|---|---|---|---|
| `01-timestamp-statistics-unit` | 1 | master | `TimestampStatisticsTest` fails | pass |
| `03-required-map-repetition` | 3 | master | `RequiredMapTest` fails | pass |
| `04-nullable-list-elements` | 4 | master | `NullableListElementTest` fails | pass |
| `05-data-page-v2-repetition` | 5 | master | `DataPageV2Test` fails (binary fixtures included) | pass |
| `06-dictionary-page-offset` | 6 | master | `PageOffsetTest` (new) fails | pass |
| `07-uuid-statistics` | 7 | master | 3 of 3 in `UuidStatisticsTest` fail | 699 passed |
| `08-statistics-sort-order-and-column-orders` | 8 + 2 | master | 8 of 8 in `StatisticsSortOrderTest` fail | 704 passed |
| `08b-byte-stream-split-statistics` | 8b | master | 2 of 2 in `ByteStreamSplitStatisticsTest` fail | 698 passed |
| `09-statistics-decoded-as-column-type` | 9 | master | 7 of 7 in `StatisticsDecodingTest` fail | 703 passed |
| `10-zstd-single-copy` | 10 | master | none: perf (`ConcurrentZstdTest` guards the thread-static cache; 5 runs of 5) | 697 passed |
| `10b-one-array-per-page` | 10b | master | none: perf (`PageValuesTest` guards against values aliasing reused buffers) | 697 passed |
| `10c-tests-on-net8` | 10c | master | does not compile on net8.0 | pass on both |
| `11-field-ids` | 11 | master | `FieldIdTest` does not compile | 699 passed |
| `12-fixed-length-byte-arrays` | 12 | master | `FixedLengthByteArrayTest` does not compile | 699 passed |
| `13-write-statistics-option` | 13 | master | `WriteStatisticsOptionTest` does not compile | 698 passed |
| `14-physical-values` | 14 | 1 + 8 | `PhysicalValuesTest` does not compile | 716 passed |
| `15-legacy-list-shapes` | 15 | 4 | 4 of 6 in `LegacyListTest` fail (the other 2 are shapes that already worked) | 705 passed |
| `16-typed-statistics-api` | 16 | 7 + 8 + 9 | `TypedStatisticsTest` does not compile | 725 passed |
| `17-reader-columns-footer-length` | 17 | master | `ColumnDescriptorTest` does not compile | 697 passed |
| `17b-column-sort-order` | 17b | 16 + 17 (made on all units but 21) | `ColumnDescriptor.SortOrder` does not compile | 788 passed, net8.0 and net10.0 |
| `18-raw-data-field` | 18 | 1, 3, 7, 8, 9, 12, 14, 16 | `RawDataFieldTest` does not compile | 750 passed |
| `19-validate-header-magic` | 19 | master | `Header_magic_check_can_be_skipped` does not compile | 697 passed |
| `20-modular-encryption` | 20 | 6 | `EncryptionTest` does not compile | 712 passed, net8.0 and net10.0 |
| `21-parquet-cpp-interop-tests` | 21 | everything else | tests only | 792 passed, net8.0 and net10.0 |

Differences from the fork, so each unit stands alone:

- Tests use ordinary `DataField`s where the fork's used `RawDataField` (unit 18) or ParquetSharp (unit 21), and
  files written in the test rather than another unit's fixtures (unit 17).
- Unit 8 adds `ColumnStatisticsRules` with an **internal** `ColumnSortOrder` and `NanCount`; unit 16 makes them public.
- The unsigned INT32/INT64 comparison (`ComputeUnsigned`) is in unit 14, not 8: only physical values (14) or
  `RawDataField` (18) can write unsigned values as `int`/`long`. Unit 14 adds a test for it (it failed without).
- The UUID statistics decode is in unit 7 (it only round-trips with 7's byte order), not 9.
- Decoding `fixed[n]` bounds as bytes is in unit 12 (where such columns become writable), not 16. Without it, unit 12
  with unit 8 writes the bounds but `GetStatistics` returns none.
- `ColumnDescriptor.SortOrder` is its own unit 17b: it needs 16's public enum, and 17 does not otherwise.
- Unit 16's test is `TypedStatisticsTest` (the fork named it `StatisticsSortOrderTest`, which unit 8's test is now).

Overlaps resolved in the series (what a rebase will meet if the units are merged in another order):

- **3 and 11** edit the same line of the map element: keep both (`RepetitionType` from `IsNullable`, and `FieldId`).
- **7, 8 and 12** in `ParquetPlainEncoder`: the byte-array write branch takes 12's `EncodeFixed` and 8's bounds;
  `TryDecode` keeps 7's UUID branch before 12's `fixed[n]` one. Unit 12's test file-size bound grows from 200 to
  260 bytes once unit 8 writes bounds (the fork's test also checks them then).
- **13 and 19** add a property at the same place in `ParquetOptions`: keep both.
- **20 with 10, 17 and 19**: the reader, actor, footer and options take both sides (encryption around the footer
  read, the footer length, the head check).
- **15 and 16** both add `data/legacy/three_level.parquet` (identical).

## Performance of the units, measured on their own

On upstream `master` against `master` plus the unit, Linux, .NET 10, Release, best of 7 runs after 2 warm-ups,
1M values, no compression for writes:

| Unit | What | Upstream | With the unit |
|---|---|---|---|
| 8 | write, byte-array column (now gets bounds) | 58 ms | 68 ms |
| 8 | write, string / double (NaN counted) / long column | 84 / 5 / 4 ms | 84 / 5 / 4 ms |
| 8b | write, double column, BYTE_STREAM_SPLIT (now gets bounds) | 12 ms | 14–16 ms |
| 10 | read, string + binary + int64, Zstd (4 row groups) | 252 ms, 208 MB allocated | 250 ms, 137 MB |
| 10b | read, the same file, Zstd / Snappy | 252 / 241 ms | 119 / 114 ms |
| 20 | (fork, 2M rows) write plain / GCM, read plain / GCM | | 477 / 482 ms, 781 / 921 ms |

Units 8 and 8b cost only where they add statistics that were missing; the comparison changes (code points, NaN)
are within noise. The feature units (11 to 19) add nothing to paths that do not use them.

Not verified: Windows and macOS for the patches themselves (the fork as a whole runs on both in Iceberg.NET's CI).
