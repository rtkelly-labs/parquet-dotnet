using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Extensions;
using Parquet.Extensions.Streaming;
using Xunit;

namespace Parquet.Test.Coverage;

/// <summary>
/// The internal stream wrappers: the write meter ParquetWriter puts around an unseekable output, and the read-only
/// substreams a column reader reads a page through.
/// </summary>
public class StreamWrappersTest {

    sealed class Unseekable(Stream inner) : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override bool CanTimeout => true;
        public override int ReadTimeout { get; set; } = 11;
        public override int WriteTimeout { get; set; } = 22;
        public override long Length => inner.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public bool Disposed { get; private set; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task Metered_stream_counts_every_kind_of_write() {
        var inner = new MemoryStream();
        var metered = new MeteredWriteStream(new Unseekable(inner));
        metered.Write([1, 2, 3], 0, 3);
        metered.Write(new ReadOnlySpan<byte>([4, 5]));
        metered.WriteByte(6);
        await metered.WriteAsync(new byte[] { 7, 8 }, 0, 2, CancellationToken.None);
        await metered.WriteAsync(new ReadOnlyMemory<byte>([9]));
        metered.Flush();
        await metered.FlushAsync(CancellationToken.None);
        Assert.Equal(9, metered.Position);   // the bytes written: the output cannot say where it is
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, inner.ToArray());
        Assert.Equal(9, metered.Length);
        Assert.Equal((false, false, true, true), (metered.CanRead, metered.CanSeek, metered.CanWrite, metered.CanTimeout));
        Assert.Equal((11, 22), (metered.ReadTimeout, metered.WriteTimeout));
        metered.ReadTimeout = 33;
        metered.WriteTimeout = 44;
        Assert.Equal((33, 44), (metered.ReadTimeout, metered.WriteTimeout));
        Assert.Throws<NotSupportedException>(() => metered.Position = 0);   // passed to the output, which cannot seek
    }

    [Fact]
    public async Task Metered_stream_does_not_read_or_seek() {
        var metered = new MeteredWriteStream(new MemoryStream());
        byte[] buffer = new byte[4];
        Assert.Throws<NotImplementedException>(() => metered.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotImplementedException>(() => metered.SetLength(1));
        Assert.Throws<NotImplementedException>(() => metered.Read(buffer, 0, 4));
        Assert.Throws<NotImplementedException>(() => metered.Read(buffer.AsSpan()));
        Assert.Throws<NotImplementedException>(() => metered.ReadByte());
        Assert.Throws<NotImplementedException>(() => metered.CopyTo(new MemoryStream(), 16));
        await Assert.ThrowsAsync<NotImplementedException>(() => metered.ReadAsync(buffer, 0, 4, CancellationToken.None));
        await Assert.ThrowsAsync<NotImplementedException>(async () => await metered.ReadAsync(buffer.AsMemory()));
        await Assert.ThrowsAsync<NotImplementedException>(() => metered.CopyToAsync(new MemoryStream(), 16, CancellationToken.None));
    }

    [Fact]
    public async Task Metered_stream_disposes_and_closes_its_output() {
        var a = new Unseekable(new MemoryStream());
        new MeteredWriteStream(a).Dispose();
        Assert.True(a.Disposed);
        var b = new Unseekable(new MemoryStream());
        await new MeteredWriteStream(b).DisposeAsync();
        Assert.True(b.Disposed);
        var c = new Unseekable(new MemoryStream());
        new MeteredWriteStream(c).Close();
        Assert.True(c.Disposed);
    }

    [Fact]
    public async Task Span_substream_reads_seeks_and_refuses_writes() {
        Memory<byte> source = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        ReadSpanSubStream s = source.Sub(2, 6);    // 2..7
        Assert.Equal((true, true, false, 6L), (s.CanRead, s.CanSeek, s.CanWrite, s.Length));
        Assert.Equal(2, s.ReadByte());
        byte[] buffer = new byte[10];
        Assert.Equal(3, s.Read(buffer, 0, 3));
        Assert.Equal(new byte[] { 3, 4, 5 }, buffer[..3]);
        Assert.Equal(4, s.Seek(0, SeekOrigin.Current));
        Assert.Equal(1, s.Seek(1, SeekOrigin.Begin));
        Assert.Equal(5, s.Seek(-1, SeekOrigin.End));
        Assert.Equal(1, await s.ReadAsync(buffer, 0, 5, CancellationToken.None));   // only one left
        Assert.Equal(7, buffer[0]);
        Assert.Equal(0, s.Read(buffer, 0, 5));
        Assert.Equal(-1, s.ReadByte());
        s.Position = 3;
        Assert.Equal(5, s.ReadByte());
        s.Flush();

        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = 7);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = -1);
        Assert.Throws<IOException>(() => s.Seek(7, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentException>(() => s.Seek(0, (SeekOrigin)9));
        Assert.Throws<ArgumentNullException>(() => s.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(buffer, 8, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(buffer, -1, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(1));
        Assert.Throws<NotSupportedException>(() => s.Write(buffer, 0, 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ReadAsync(buffer, 0, 1, new CancellationToken(true)));
        s.Close();
    }

    [Fact]
    public void Stream_substream_reads_its_window_of_the_base_stream() {
        var baseStream = new MemoryStream(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        ReadStreamSubStream s = baseStream.Sub(3, 4);  // 3..6
        Assert.Equal((true, true, false, 4L), (s.CanRead, s.CanSeek, s.CanWrite, s.Length));
        byte[] buffer = new byte[10];
        Assert.Equal(2, s.Read(buffer, 0, 2));
        Assert.Equal(new byte[] { 3, 4 }, buffer[..2]);
        baseStream.Position = 0;                       // the base moving does not move the window
        Assert.Equal(2, s.Read(buffer, 0, 9));         // two left
        Assert.Equal(new byte[] { 5, 6 }, buffer[..2]);
        Assert.Equal(0, s.Read(buffer, 0, 1));
        Assert.Equal(1, s.Seek(1, SeekOrigin.Begin));
        Assert.Equal(3, s.Seek(2, SeekOrigin.Current));
        Assert.Equal(2, s.Seek(-2, SeekOrigin.End));
        s.Position = 0;
        Assert.Equal(3, s.ReadByte());
        s.Flush();

        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = 5);
        Assert.Throws<IOException>(() => s.Seek(5, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentException>(() => s.Seek(0, (SeekOrigin)9));
        Assert.Throws<ArgumentNullException>(() => s.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(buffer, 9, 2));
        Assert.Throws<NotSupportedException>(() => s.Write(buffer, 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(1));
    }

    [Fact]
    public void Stream_substream_refuses_what_it_cannot_window() {
        Assert.Throws<ArgumentNullException>(() => new ReadStreamSubStream(null!, 0, 1));
        Assert.Throws<ArgumentException>(() => new ReadStreamSubStream(new Unseekable(new MemoryStream()), 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadStreamSubStream(new MemoryStream(), -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadStreamSubStream(new MemoryStream(), 0, -1));
    }
}
