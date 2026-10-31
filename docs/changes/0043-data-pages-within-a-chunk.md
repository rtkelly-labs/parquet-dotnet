---
title: "Column chunks split into data pages (`ParquetOptions.MaxDataPageValues`)"
kind: code
status: fork-only
depends_on: [0006, 0008, 0009, 0015, 0016, 0017, 0025, 0041]
---

# 0043 Column chunks split into data pages (`ParquetOptions.MaxDataPageValues`)

## What and why

- **Problem:** the writer puts each column chunk in one data page, however large: a 1M-row string column is one
  26 MB page. Every buffer on the way (encoding, compression, decompression, the reader's page and value arrays)
  is page-sized and fresh, and readers cannot stream a chunk page by page. Other writers default to pages of about
  1 MB.
- **Change:** `ParquetOptions.MaxDataPageValues` (unset by default: one page per chunk, as before). When set, a
  chunk is split into pages of about that many values (levels, nulls included), each ending at the first row start
  at or after the limit, so a row is never split. Dictionary pages are unchanged and shared by the chunk's pages.
  The chunk's statistics still cover every value, computed once; a page of a chunk with several carries no
  statistics of its own (they are optional), since the chunk's would be wrong for it.
- **Two fixes it needed:**
  - DELTA_BINARY_PACKED wrote nothing at all for no values. The header (block size, miniblocks, a total of 0, a
    first value) is required; parquet-cpp fails with "InitHeader EOF" on such a page. A chunk of only nulls could
    not reach it before (an empty chunk falls back to plain), but a page of only nulls can.
  - Statistics computed through `ParquetPlainEncoder.Encode(values, Stream.Null, ...)` encoded every value to
    discard it; that call now computes the statistics alone. This also speeds up byte stream split pages, which
    computed theirs the same way.

## Evaluation

- **Tests:** `DataPageSplitTest`: unset is one page; 1,000 rows at 100 values a page are ten pages, plain, delta and
  byte stream split, with pages entirely of nulls, read back exactly by this library and by parquet-cpp (the delta
  case fails without the header fix); a list column's pages all start a row and its levels and values read back.
  The conformance harness generates the option in half its cases (`lake(0044)`): 10,000 fresh seeds pass in each
  direction, nested shapes, every encoding hint, codec and encryption included.
- **Cost:** 1M strings, 10% null, pages of 65,536 values against one page: write 317 against 315 ms, read as UTF-8
  50 against 53 ms; 1M int64 read 9.2 against 11.0 ms. Same file size.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0043-data-pages-within-a-chunk`.

## Compatibility

None while the option is unset. With it set, files have several data pages per chunk, which every reader handles,
and those pages have no page-level statistics.
