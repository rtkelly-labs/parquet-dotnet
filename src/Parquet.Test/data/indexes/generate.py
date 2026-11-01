# Regenerates the synthetic fixtures in this folder (the rest come from apache/parquet-testing, see README.md).
# Needs: pip install pyarrow duckdb polars   (pyarrow 26, duckdb 2.0, polars 2.1 wrote the committed files)
import duckdb, polars as pl, pyarrow as pa, pyarrow.parquet as pq

n = 3000
# DuckDB: bloom filters (with bloom_filter_length) on its dictionary-encoded columns; no page index
duckdb.connect().execute(f"""COPY (SELECT i::BIGINT AS id, 'key-' || (i % 500) AS k, (i % 37)::INTEGER AS small,
    DATE '2020-01-01' + (i % 1000)::INTEGER AS day FROM range({n}) t(i))
    TO 'duckdb_bloom.parquet' (FORMAT parquet, ROW_GROUP_SIZE 1000, COMPRESSION zstd)""")
# Polars: a page index (column and offset indexes) over small pages; no bloom filters
pl.DataFrame({"id": range(n), "k": [f"key-{i % 500}" for i in range(n)], "d": [i * 0.5 for i in range(n)]}) \
    .write_parquet("polars_page_index.parquet", row_group_size=1000, data_page_size=1024, statistics=True)
# parquet-cpp (pyarrow): both, over small pages, with nulls (null pages in the column index) and sorted ids
t = pa.table({"id": pa.array(range(n), pa.int64()),
              "k": pa.array([f"key-{i % 500}" for i in range(n)]),
              "o": pa.array([None if (i // 250) % 3 == 0 else i for i in range(n)], pa.int32())})
pq.write_table(t, "pyarrow_index_bloom.parquet", write_page_index=True, max_rows_per_page=250, row_group_size=1000,
               bloom_filter_options={"k": {"ndv": 500, "fpp": 0.01}, "id": True})
