---
title: "Tests: the last reachable paths, AVX-512 included"
kind: addition
status: fork-only
depends_on: [0083]
---

# 0085 Tests: the last reachable paths, AVX-512 included

## What and why

The third round of coverage tests. On the stripped package, 103 lines were still unrun after `lake(0074)` and
`lake(0083)`. This adds `Coverage/Round3Test` (26 cases) for every one of them that a caller can reach. The rest are
listed in this change's pull request as unreachable.

- **AVX-512.** `ParquetPlainEncoder.EncodeHwx(bool)`'s 512-bit loop runs only when `Vector512.IsHardwareAccelerated`.
  .NET leaves it off by default on some AVX-512 CPUs, and `DOTNET_PreferredVectorBitWidth=512` turns it on. The tests
  encode booleans of 1 to 1,031 values with hardware acceleration on and off and require the same bytes, and
  round-trip a 777-value column. Run with the variable on an AVX-512 machine, they cover the vector loop. Without
  it, they cover the 256-bit and scalar paths.
- **Pages other writers may produce, as hand-built one-page files:**
  - an RLE-encoded INT32 page, with its type length as the bit width;
  - a DELTA_LENGTH_BYTE_ARRAY page of DECIMAL(4,2) on BYTE_ARRAY, read through PLAIN to 1.00, -2.50 and 12.34;
  - a DELTA_BINARY_PACKED BOOLEAN page, refused.
- **Footers with schemas the library cannot build:**
  - an implicit list with no element;
  - a physical type the library does not know;
  - a TIME without a unit, read as `TimeOnly`.
- **Encoders:**
  - a decimal too wide for an INT32 column declared with precision 18, refused by both the column encoder and the
    statistics encoder;
  - byte arrays longer than the encoder's 8 KiB buffer;
  - FIXED_LEN_BYTE_ARRAY decode, with and without a type length;
  - unsigned delta encoding in a partial group of eight;
  - RLE runs at width 0.
- **Statistics:** unsigned statistics compared as unsigned, with and without a statistics object; a null-only chunk
  with no physical bounds.
- **Schema:**
  - a field kind the encoder does not know, refused;
  - field equality with a non-field;
  - a map's repeated key and value written as optional;
  - empty custom metadata;
  - a column chunk with an empty path;
  - the write meter disposing its output.

## Evaluation

- Plain suite: 1,266 passed, 3 skipped, on net8.0 and net10.0, and the same with
  `DOTNET_PreferredVectorBitWidth=512`.
- Stripped, renamed build: 1,044 passed, 1 skipped, on net8.0 and net10.0.
- No defect found.

## Upstream

Not applicable.

## Compatibility

Test-only.
