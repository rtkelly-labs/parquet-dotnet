---
title: "RLE runs of four-byte values keep their top byte"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0076 RLE runs of four-byte values keep their top byte

## What and why

- **Problem:** `RleBitpackedHybridEncoder.WriteRle4` wrote a run's value with its top byte from
  `(value >> 32) & 0xFF`. C# masks an `int` shift count to five bits, so that is `value >> 0`: the low byte again.
  Every RLE run of values 25 to 32 bits wide was written wrong: dictionary indexes of 2^24 (16,777,216) and more. A
  column with that many distinct dictionary values read back other values. Found by the coverage tests of
  `lake(0074)`.
- **Change:** the top byte is `(value >> 24) & 0xFF`.

## Evaluation

- **Tests** (`Encodings/RleWideValuesTest`, 3): runs of values at bit widths 25, 31 and 32, including `int.MaxValue`,
  negative values and mixed bytes, round-trip through the encoder and decoder.
- **Without the change:** all three fail (`[16777216, …]` decodes as `[0, …]`).
- **Suite:** all pass on net8.0 and net10.0.

## Upstream

Upstream has the same line. A person may propose it from `lake/change/0076-rle-four-byte-values`.

## Compatibility

Files the writer wrote with such indexes were corrupt; they now hold the right values.
