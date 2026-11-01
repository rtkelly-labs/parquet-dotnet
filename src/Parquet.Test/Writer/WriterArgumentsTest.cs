using System;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Writer;

/// <summary>A writer without an output stream is refused with ArgumentNullException, not a NullReferenceException.</summary>
public class WriterArgumentsTest {

    [Fact]
    public async Task A_null_output_is_refused_by_name() {
        ArgumentNullException e = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ParquetWriter.CreateAsync(new ParquetSchema(new DataField<int>("id")), null!));
        Assert.Equal("output", e.ParamName);
    }
}
