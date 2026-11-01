using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Xunit;

namespace Parquet.Test;

/// <summary>Thrift headers cut off by the end of the stream (docs/changes/0102-thrift-varint-end-of-stream.md).</summary>
public class ThriftEndOfStreamTest {

    /// <summary>Runs on a background thread of its own, so that a hang fails the test instead of the run.</summary>
    static Exception? RunWithTimeout(Action action) {
        Exception? error = null;
        var thread = new Thread(() => {
            try {
                action();
            } catch(Exception e) {
                error = e;
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "no result after 10s: the reader loops at the end of the stream");
        return error;
    }

    [Fact]
    public void Footer_cut_off_in_an_unknown_struct_fails() {
        // a one-byte footer, 0xAC: field 10 of FileMetaData (unknown), a struct, which is skipped; the stream ends there
        byte[] file = [.. "PAR1"u8, 0xAC, 1, 0, 0, 0, .. "PAR1"u8];
        Exception? error = RunWithTimeout(() => {
            using var ms = new MemoryStream(file, false);
            ParquetReader.CreateAsync(ms).GetAwaiter().GetResult();
        });
        Assert.IsAssignableFrom<IOException>(error);
    }

    [Fact]
    public void List_header_at_end_of_stream_fails() {
        // field 2 of FileMetaData (schema), a list, whose header is cut off
        Exception? error = RunWithTimeout(() => FileMetaData.Read(new ThriftCompactProtocolReader(new MemoryStream([0x29]))));
        Assert.IsType<EndOfStreamException>(error);
    }
}
