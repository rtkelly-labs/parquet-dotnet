using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.HighPerformance.Buffers;
using Parquet.Data;
using Parquet.Schema;

namespace Parquet;

/// <summary>
/// Lake.Parquet leaves the test-only helper ParquetRowGroupReader.ReadRawColumnDataAsync, and RawColumnData, out of the
/// package (eng/lake/strip-members.json, Lake.Parquet.targets); its test run compiles RawColumnData into the test
/// assembly instead, and this stands in for the helper, on the public ReadRawAsync. In the plain build the library's
/// own instance method takes precedence and this is not called.
/// </summary>
static class LakeRawColumnDataReader {

    public static async ValueTask<RawColumnData<T>> ReadRawColumnDataAsync<T>(this ParquetRowGroupReader reader, DataField field,
        CancellationToken cancellationToken = default) where T : struct {
        long numValues = reader.GetMetadata(field)!.MetaData!.NumValues;
        IMemoryOwner<T> values = MemoryOwner<T>.Allocate((int)numValues);
        IMemoryOwner<int>? definitionLevels = field.MaxDefinitionLevel > 0 ? MemoryOwner<int>.Allocate((int)numValues) : null;
        IMemoryOwner<int>? repetitionLevels = field.MaxRepetitionLevel > 0 ? MemoryOwner<int>.Allocate((int)numValues) : null;
        try {
            await reader.ReadRawAsync(field, values.Memory, definitionLevels?.Memory, repetitionLevels?.Memory, cancellationToken);
        } catch {
            values.Dispose();
            definitionLevels?.Dispose();
            repetitionLevels?.Dispose();
            throw;
        }
        return new RawColumnData<T>(values, definitionLevels, repetitionLevels);
    }
}
