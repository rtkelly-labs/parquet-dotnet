# Files other writers wrote, embedded in the native AOT suite

| File | Source | Writer |
|---|---|---|
| `lake_metadata_features.parquet`, `lake_encodings_other_writers.parquet`, `lake_encodings_plain_twin.parquet`, `lake_geometry.parquet` | written for this suite with pyarrow 26 | parquet-cpp |
| `lake_decimal_38_9.parquet` | written for this suite with pyarrow 26: decimal128(38,9), values that fit `decimal` and one beyond it | parquet-cpp |
| `int96_timestamp_order.parquet`, `floating_orders_nan_count.parquet` | apache/parquet-testing `data/` (Apache License 2.0), unchanged | parquet-mr |
| `shredded_variant_case_001.parquet` | apache/parquet-testing `shredded_variant/case-001.parquet` (Apache License 2.0), unchanged | parquet-mr |
| `geography_points.parquet` | apache/parquet-testing `data/geospatial/geography-points.parquet` (Apache License 2.0), unchanged | DataFusion |
