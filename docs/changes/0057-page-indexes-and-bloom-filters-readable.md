---
title: "Page indexes and bloom filters are readable"
kind: code
status: fork-only
depends_on: [0015, 0017, 0024, 0052]
evidence: failing-test
---

# 0057 Page indexes and bloom filters are readable

## What and why

- **Problem:** Parquet files carry two structures for skipping data that the library could not read. The page index
  (a `ColumnIndex` and an `OffsetIndex` per column chunk, between the row groups and the footer) gives each data page's
  bounds, null count and location, so a reader can skip pages; parquet-mr, parquet-rs, parquet-cpp, Polars and
  DataFusion write it, and DuckDB, Spark and Trino use it. A split block bloom filter (per column chunk) answers "may
  this value be here?" for high-cardinality columns that bounds cannot prune; parquet-mr, parquet-rs, parquet-cpp and
  DuckDB write them. The library kept only their offsets: the thrift classes were generated, but nothing read them,
  and Lake.NET's point lookups and page skipping had nothing to call.
- **Change:** `ParquetRowGroupReader` gains
  - `ReadColumnIndexAsync(field)` and `ReadOffsetIndexAsync(field)`, returning the generated `ColumnIndex` and
    `OffsetIndex` (null when the file has none for that column);
  - `ReadBloomFilterAsync(field)`, returning a `Parquet.Data.BloomFilter` (null when there is none) whose
    `MightContain` overloads take INT32, INT64, FLOAT, DOUBLE, string and raw-byte values and hash their PLAIN
    encoding with xxHash64, seed 0, as `BloomFilter.md` specifies; `Hash` and `MightContainHash` expose the hash for
    callers that hash once and probe many chunks. A bloom filter is read with or without `bloom_filter_length` (older
    parquet-mr omits it); anything but an uncompressed split block xxHash64 filter is refused.
  - Encrypted columns' structures are decrypted with the column's key: column and offset indexes and bloom filter
    headers and bitsets are AES-GCM modules with their own module types (6 to 9) in the AAD, under either algorithm
    (`ColumnDecryptor.Module`).
  - xxHash64 is implemented in `Utils/XxHash64.cs` (about 50 lines) rather than taking System.IO.Hashing as a
    dependency.

## Evaluation

- **Fixtures** (`src/Parquet.Test/data/indexes`, with a README): eight files from apache/parquet-testing (parquet-mr's
  and parquet-rs's bloom filter files, Impala's `alltypes_tiny_pages`, null pages, truncated binary bounds, NaN counts
  and float orders, a raw xxHash64 bloom filter, an encrypted file with bloom filters), and three written by
  `generate.py` with DuckDB 2.0 (bloom filters with lengths), Polars 2.1 (page index) and pyarrow 26 (both).
- **Tests** (`Reader/PageIndexAndBloomFilterTest`, 35):
  - xxHash64 against the reference implementation at 15 lengths across its branches (0 to 100 bytes);
  - parquet-mr's raw filter contains the four strings inserted into it;
  - every bloom filter in every fixture contains every value of its chunk (no false negatives), and reports at most
    10% of 2,000 absent values present;
  - every page index: page locations start at row 0 and increase, a data page header starts at each offset and the
    page is the size the index says; null pages and null counts match the values in each page's rows; each page's
    bounds contain its values (exact or widened; truncated for binary; zero as -0.0/+0.0; FLOAT16 numerically);
  - the encrypted file's bloom filters and page indexes decrypt and check the same way.
  Without the change the tests do not compile (the methods do not exist).
- **Suite:** 939 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

Upstream has the thrift classes but no reading. A person may propose it from
`lake/change/0057-page-indexes-and-bloom-filters-readable`.

## Compatibility

New API only.
