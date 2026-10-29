using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>Many Zstd files read at once, over streams whose reads complete asynchronously (continuations change threads).</summary>
public class ConcurrentZstdTest {

    sealed class YieldingStream(byte[] bytes) : MemoryStream(bytes, false) {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Yield();
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task Parallel_reads_decompress_correctly() {
        var field = new DataField<long>("v");
        long[] values = Enumerable.Range(0, 5_000).Select(i => (long)i * 7919 % 100_003).ToArray();
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, new ParquetOptions { CompressionMethod = CompressionMethod.Zstd })) {
            for(int g = 0; g < 40; g++) {
                using ParquetRowGroupWriter rg = w.CreateRowGroup();
                await rg.WriteAsync<long>(field, values);
            }
        }
        byte[] file = ms.ToArray();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () => {
            await using ParquetReader reader = await ParquetReader.CreateAsync(new YieldingStream(file));
            DataField f = reader.Schema.GetDataFields()[0];
            foreach(ParquetRowGroupReader rg in reader.RowGroups) {
                long[] read = new long[values.Length];
                await rg.ReadRawAsync<long>(f, read, null, null);
                Assert.Equal(values, read);
            }
        })));
    }
}
