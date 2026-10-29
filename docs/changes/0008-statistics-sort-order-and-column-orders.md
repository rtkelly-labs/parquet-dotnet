---
title: "Statistics in each type's sort order, and `column_orders`"
kind: code
status: fork-only
depends_on: []
upstream_unit: "8 + 2"
---

# 0008 Statistics in each type's sort order, and `column_orders`

A bug fix: unit 8 and 2 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## Unit 8: Statistics in each type's sort order

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

## Unit 2: Write `column_orders`

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

## Evaluation

- **Without the change:** 8 of 8 in `StatisticsSortOrderTest` fail.
- **With it:** 704 passed (the whole suite, when the unit was verified on upstream `658e1ca`).
- **Performance**, write, byte-array column (now gets bounds): upstream 58 ms, with the change 68 ms.
- **Performance**, write, string / double (NaN counted) / long column: upstream 84 / 5 / 4 ms, with the change 84 / 5 / 4 ms.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0008-statistics-sort-order-and-column-orders` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

New files declare `column_orders` (TYPE_ORDER), so other readers start using string and binary bounds; deprecated `min`/`max` are no longer written for unsigned-order columns.
