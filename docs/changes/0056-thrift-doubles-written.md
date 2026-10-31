---
title: "Thrift double fields are written: geospatial bounding boxes survive a footer rewrite"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0056 Thrift double fields are written: geospatial bounding boxes survive a footer rewrite

## What and why

- **Problem:** the thrift code generator (`scripts/thriftgen.cs`) had no `double` case in the field writer it
  generates, and `ThriftCompactProtocolWriter` had no method to write one. The only doubles in the format are
  `BoundingBox`'s (`xmin`, `xmax`, `ymin`, `ymax` required; `zmin` to `mmax` optional), so the generated
  `BoundingBox.Write` wrote an empty struct. Reading worked (`ReadDouble` existed). Any footer the library writes back
  with geospatial statistics in it (an append to a file parquet-cpp, DuckDB or GDAL wrote with a GEOMETRY or
  GEOGRAPHY column) lost the bounding box: the required fields were missing, and the library itself read them back as
  0. Found by the native AOT suite reading and rewriting a pyarrow geometry file.
- **Change:** `ThriftCompactProtocolWriter.WriteDoubleField` writes the field header with the compact protocol's
  `Double` type and the value's 8 IEEE 754 bytes, little-endian (the inverse of `ReadDouble`); the generator emits it
  for `double` fields, required and optional; `Meta/Parquet.cs` is regenerated with it. The generator was first run
  on the unchanged source and reproduced `Parquet.cs` exactly, so the regenerated file differs only in
  `BoundingBox.Write`.

## Evaluation

- **Test:** `Integration/GeospatialStatisticsAppendTest` opens `data/geometry.parquet` (ten WKB points written by
  pyarrow 26, bounding box 0..9, -9..0), appends nothing, which rewrites the footer, and reads the bounding box back:
  without the change it is 0, 0, 0, 0; with it 0, 9, -9, 0.
- **Suite:** 904 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

A bug in upstream's generator (the same switch is in aloneguid/parquet-dotnet's `scripts/thriftgen.cs`). Not yet
proposed: a person proposes it from `lake/change/0056-thrift-doubles-written`.

## Compatibility

Footers written with geospatial statistics now carry their bounding box.
