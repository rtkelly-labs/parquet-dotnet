---
title: "Page indexes and bloom filters are written, when asked for"
kind: code
status: fork-only
depends_on: [0043, 0057]
evidence: failing-test
---

# 0058 Page indexes and bloom filters are written, when asked for

## What and why

- **Problem:** files the library wrote had no page index and no bloom filters, so DuckDB, Spark, Trino, parquet-rs
  and the library's own reader (`lake(0057)`) could neither skip pages nor rule out a row group by value. Lake.NET's
  tables are written by this library and read by those engines.
- **Change:** two options, both off by default (Lake.NET decision 7: what changes the files written is opt-in):
  - `ParquetOptions.WritePageIndex`: the column writer records each data page's position, size (header and data)
    and first row, and its own statistics (bounds and null count, from the same values it encodes, through the same
    PLAIN encoder that produces chunk statistics). After the last row group, before the footer, the writer writes
    each chunk's `OffsetIndex` and, when the column's type has a defined order and every page with values has
    bounds, its `ColumnIndex` (null pages flagged with empty bounds, null counts, `UNORDERED`). Pages split by
    `MaxDataPageValues` start at row boundaries (`lake(0043)`), as the offset index requires.
  - `ParquetOptions.BloomFilters[path] = fpp`: for each chunk of that column, the non-null values are PLAIN-encoded
    in the column's physical type (so timestamps, dates and decimals hash as other readers hash them), each value's
    bytes hashed with xxHash64, and a split block filter sized for the distinct hashes at that false positive
    probability (BloomFilter.md's formula, rounded up to a power of two, 32 bytes to 128 MiB) is filled and written
    with its header after the page indexes; `bloom_filter_length` is set. A BOOLEAN column is refused.
  - Encrypted columns' indexes and filters are encrypted with the column's key, each structure a GCM module with its
    module type in the AAD, so the library's reader and parquet-mr's decrypt them.

## Evaluation

- **Tests** (`Writer/PageIndexAndBloomFilterWriteTest`, 7): a two-row-group file of INT32, nullable INT64 (with whole
  pages of nulls), DOUBLE, dictionary-encoded strings, TIMESTAMP(MICROS), DECIMAL(18,2) and BOOLEAN columns in 100-row
  pages: every page is located (a data page header at each offset, the size the index says, first rows 0, 100, ...);
  each page's bounds and null count are those of its values (strings in byte order, decimals unscaled, null pages
  flagged); every value is in its column's bloom filter and at most 3% of 20,000 absent values are reported at 1%
  asked; a BOOLEAN bloom filter is refused; nothing is written unless asked; and with encryption (encrypted and plaintext
  footer, the footer key and a separate column key) the indexes and filters decrypt and check the same. Without the
  change the tests do not compile.
- **Other readers**, on a file the library wrote (INT32, nullable INT64, DOUBLE, dictionary strings,
  TIMESTAMP(MICROS); two row groups of ten pages):
  - parquet-rs 55 (`ReadOptionsBuilder::with_page_index`, `Sbbf::check`): ten pages per chunk, the column index's
    bounds and null counts per page (null pages as `None`, a zero minimum as `-0.0`), and bloom filter probes true
    for present values and false for absent ones;
  - DuckDB 2.0: `parquet_bloom_probe` excludes both row groups for absent strings, integers and timestamps and
    neither for present ones, and a filtered query returns the right rows;
  - pyarrow 26: `has_column_index`, `has_offset_index`, the bloom filter's offset and length, and all rows read.
- **Suite:** 946 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

A person may propose it after `lake(0057)`, from `lake/change/0058-page-indexes-and-bloom-filters-written`.

## Compatibility

None unless asked: with the options off, files are written as before.
