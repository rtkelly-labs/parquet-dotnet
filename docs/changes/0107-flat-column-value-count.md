---
title: "A column that is not repeated has no more values than its row group has rows"
kind: code
status: fork-only
depends_on: [0100, 0103, 0106]
---

# 0107 A column that is not repeated has no more values than its row group has rows

The rest of finding F5 of [0100](0100-malformed-file-tests.md) (allocation amplification from untrusted footer
counts), for the columns where the format gives a bound.

## What and why

- **Problem:** `ParquetRowGroupReader` sizes a column's value, definition-level and repetition-level buffers by the
  column chunk's `num_values` before reading a page. 0103 rejects counts that cannot be allocated at all, but a
  plausible lie still allocates it: `num_values = 2^28` makes a 4 KB file allocate 1–3 GB (and fail with
  `OutOfMemoryException` or not, depending on the machine). Run-length encoding lets a few bytes hold millions of
  values, so the file's size gives no bound.
- **Change:** a column that is not repeated has exactly one value (or null) per row, so its `num_values` cannot exceed
  the row group's `num_rows`; more is a `ParquetException`, checked in `GetColumnMetaData` before anything is allocated.
  Fewer is allowed (an empty chunk, 0030).
- **Review:** repeated columns (lists, maps) have no such bound: their value count is the number of leaf entries.
  Bounding them needs buffers that grow with the pages instead of one allocation sized by the footer, which changes
  `ReadRawColumnDataAsync`'s allocation model and is left for a change of its own. The one remaining known case,
  `v2/v2_snappy.parquet|meta:values=2^28` (a list column), stays `any`.

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0, two runs each): known findings **9 → 1**. The 8
  `meta:values=2^28` cases of flat columns now fail with `ParquetException` before allocating; the class runs in about
  5 s instead of 20–30 s, because nothing allocates gigabytes any more. No case that read cleanly before fails now.
- **The whole suite:** net10.0 and net8.0 each 2,828 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** one comparison per column chunk.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0107-flat-column-value-count` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). It sits next to 0103's
check of the same count.

## Compatibility

A file whose non-repeated column claims more values than its row group has rows throws `ParquetException`; such a
file breaks the format, and reading it before allocated by the claim. Valid files read as before.
