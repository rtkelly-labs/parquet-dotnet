# Page index and bloom filter fixtures

Files other Parquet implementations wrote with a page index (column index and offset index) or bloom filters, for
reading those structures. None of them was written by this library.

## From apache/parquet-testing (Apache License 2.0)

Copied unchanged from <https://github.com/apache/parquet-testing> (`data/`); its README describes each in more detail.

| File | Writer | What it has |
|---|---|---|
| `alltypes_tiny_pages.parquet` | parquet-mr 1.12 (Impala's test data) | 13 columns of small dictionary-encoded pages, a column index on 12 and an offset index on all 13 |
| `data_index_bloom_encoding_stats.parquet` | parquet-mr 1.13 | an optional STRING column with a bloom filter, column and offset index, encoding stats (no `bloom_filter_length`) |
| `data_index_bloom_encoding_with_length.parquet` | parquet-rs 49 | the same, with `bloom_filter_length` set |
| `int32_with_null_pages.parquet` | parquet-mr 1.13 | an optional INT32 column whose pages are sometimes all null (`null_pages` in the column index) |
| `binary_truncated_min_max.parquet` | parquet-rs 55 | binary columns with exact, partly and fully truncated bounds (`is_min/max_value_exact`) |
| `floating_orders_nan_count.parquet` | parquet-mr 1.18 | FLOAT, DOUBLE and FLOAT16 under IEEE 754 total and type-defined orders, `nan_count` in statistics and column index |
| `bloom_filter.xxhash.bin` | parquet-mr | not a Parquet file: one bloom filter (thrift header and bitset, xxHash64) with "hello", "parquet", "bloom" and "filter" inserted |
| `encrypt_columns_and_footer_bloom_filter.parquet.encrypted` | parquet-mr | encrypted footer and columns with bloom filters on `double_field` and `float_field` (footer key `0123456789012345`, `double_field` `1234567890123450`, `float_field` `1234567890123451`) |

## Generated here

`generate.py` rewrites these (pyarrow 26, DuckDB 2.0 and Polars 2.1 wrote the committed ones):

| File | Writer | What it has |
|---|---|---|
| `duckdb_bloom.parquet` | DuckDB 2.0 | bloom filters with `bloom_filter_length` on dictionary-encoded columns (`k`, `small`, `day`), no page index |
| `polars_page_index.parquet` | Polars 2.1 | a page index over small pages, no bloom filters |
| `pyarrow_index_bloom.parquet` | parquet-cpp (pyarrow 26) | both: a page index over 250-row pages with null pages, bloom filters on `id` and `k` |
