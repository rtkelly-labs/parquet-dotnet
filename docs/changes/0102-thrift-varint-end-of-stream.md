---
title: "Thrift varints fail at end of stream instead of hanging"
kind: code
status: fork-only
depends_on: [0100, 0101]
---

# 0102 Thrift varints fail at end of stream instead of hanging

A fix for finding F1 of [0100](0100-malformed-file-tests.md), the one hang it found.

## What and why

- **Problem:** `ThriftCompactProtocolReader.ReadVarInt32` and `ReadVarInt64` read `(byte)_inputStream.ReadByte()`. At
  the end of the stream `ReadByte` returns -1, which becomes `0xFF`: a continuation byte. The loop has no length limit,
  so a varint cut off by the end of a footer or page header loops for ever. Example:
  `encryption/plaintext_footer.parquet|footerlen=1`, where the one-byte footer is the last byte of the signature.
- **Change:** the varint loops read through `ReadVarIntByte`, which throws `EndOfStreamException` at the end of the
  stream, and `ThriftProtocolException` when a varint runs past 5 bytes (32-bit) or 10 bytes (64-bit), the most the
  compact protocol allows (Apache Thrift's own readers have the same limits).
- **The same at a field or list header:** `ReadNextField` and `ReadListHeader` read their header byte the same way, and
  `SkipField` skips an unknown struct by calling `ReadNextField` until it returns the stop byte. At the end of the
  stream the header became `0xFF` (field delta 15, type `0xF`), which is not a stop, so skipping an unknown struct
  cut off by the end of the stream looped for ever: a one-byte footer `0xAC` (field 10 of `FileMetaData`, which does
  not exist, as a struct) passes the footer length and magic checks and hangs. Both now read through
  `ReadHeaderByte`, which throws `EndOfStreamException` at the end of the stream.
- **Test:** `ThriftEndOfStreamTest`: the `0xAC` footer fails with an `IOException` within 10 s (it hangs without the
  header check), and a list header cut off fails with `EndOfStreamException`. The malformed-file case was skipped as a known hang (`Known_hangs`); it is now an ordinary case that must fail cleanly, and
  the skipped theory is removed (a theory with no data fails in xUnit, and no hang is left). `KnownCases` no longer
  filters out `timeout` entries.
- **Review:** `ReadBool` and `ReadByte` still turn -1 into `0xFF`; neither is read in a loop that only a stop ends, so
  they are left as upstream wrote them. `SkipField`'s struct loop also does not skip the values of the fields it
  passes over (it only reads their headers); that is upstream's behaviour for unknown struct fields and is left alone
  here, since it ends either way now.

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0): known findings **698 → 684**; no case is skipped
  any more. The hang now ends in `EndOfStreamException`. The other 13 cases that left the list are footers that end
  inside a field header (the 11 `footerlen=0` cases, `delta_binary_packed.parquet|flip@72878.4` and
  `delta_length_byte_array.parquet|flip@3047.6`): they threw `ThriftProtocolException` from `SkipField` (F3), on the
  `0xFF` read past the end, and now end in `EndOfStreamException`. No other outcome changed (the over-long varint limit
  turned no clean case into a finding); the frames of the remaining 51 `SkipField` entries moved.
- **The whole suite:** net10.0 and net8.0 each 2,816 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** one comparison per varint byte, in footer and page header parsing only. No performance claim is
  made: no BenchmarkDotNet comparison against upstream was run for this change.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0102-thrift-varint-end-of-stream` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). The reader is upstream's
unchanged, so the fix applies to upstream as is; a reproduction there is any footer or page header cut off in the
middle of a varint (`encryption/plaintext_footer.parquet` with its footer length set to 1).

## Compatibility

A truncated footer or page header now throws instead of hanging; an over-long varint (which no valid file has) throws
`ThriftProtocolException`. Valid files read as before.
