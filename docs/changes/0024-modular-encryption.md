---
title: "Modular encryption"
kind: code
status: fork-only
depends_on: [0005, 0006, 0008, 0011, 0019, 0020, 0023]
upstream_unit: "20"
---

# 0024 Modular encryption

A feature: unit 20 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `344675a` without the unit 6 lines (propose unit 6 first). Fixtures in `data/encryption/`.
- **Change:** AES_GCM_V1 and AES_GCM_CTR_V1; encrypted (`PARE`) or signed plaintext footers; footer key for all
  columns or separate column keys; key metadata and a key retriever; AAD prefixes stored or supplied. BCL crypto only.
- **Verified:** interop both ways with parquet-cpp in all five modes (fixtures written by ParquetSharp read here;
  files written here read by ParquetSharp in `ParquetCppInteropTest`); DuckDB 1.5.5 reads GCM files in full scans.
- **Review — security-sensitive, the unit that most needs an expert read:**
  - nonces: 12 random bytes per module from `RandomNumberGenerator` (check no reuse path);
  - AAD construction (module type, row group, column and page ordinals as little-endian int16) against
    parquet-format `Encryption.md`;
  - AES-CTR is implemented over AES-ECB (`AesModules.Ctr`): counter block = nonce ‖ 32-bit big-endian counter from 1;
  - plaintext-footer signature check uses `CryptographicOperations.FixedTimeEquals`;
  - error messages must not leak key material (they do not include keys);
  - which column metadata is encrypted vs stripped follows parquet-cpp's `ColumnChunkMetaDataBuilder::WriteTo`.
  - Not implemented: column index / offset index and bloom filter modules (Parquet.Net writes neither), appending to
    encrypted files (refused), key rotation.

## Evaluation

- **Without the change:** `EncryptionTest` does not compile.
- **With it:** 712 passed, net8.0 and net10.0 (the whole suite, when the unit was verified on upstream `658e1ca` with 6 applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0024-modular-encryption` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
