using System.Diagnostics;
using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Benchmarks;

/// <summary>
/// One scenario in a loop, for a profiler to attach to (docs/testing.md, "Profiling"):
/// <c>dotnet run -c Release -- profile &lt;scenario&gt; [seconds]</c>. The scenarios are the benchmarks' calls.
/// </summary>
public static class Profile {

    public static readonly string[] Scenarios = [
        "read-int64", "read-double", "read-timestamp", "read-decimal", "read-strings", "read-strings-utf8",
        "write-int64", "write-double", "write-timestamp", "write-decimal", "write-strings"];

    public static async Task<int> RunAsync(string[] args) {
        if(args.Length < 2 || !Scenarios.Contains(args[1])) {
            Console.Error.WriteLine($"usage: profile <{string.Join('|', Scenarios)}> [seconds]");
            return 2;
        }
        string scenario = args[1];
        double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 15;
        Func<Task> step = await PrepareAsync(scenario);
        await step();   // warm up
        var sw = Stopwatch.StartNew();
        long before = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        TimeSpan paused = GC.GetTotalPauseDuration();
        int n = 0;
        while(sw.Elapsed.TotalSeconds < seconds) {
            await step();
            n++;
        }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine($"{scenario}: {n} runs, {sw.Elapsed.TotalMilliseconds / n:F2} ms and {allocated / (double)n / 1e6:F2} MB per run; " +
            $"GC: {GC.CollectionCount(0) - gen0} gen0, {GC.CollectionCount(2) - gen2} gen2, {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds / n:F2} ms paused per run");
        return 0;
    }

    static async Task<Func<Task>> PrepareAsync(string scenario) {
        var longField = new DataField<long>("v");
        var doubleField = new DataField<double?>("v");
        var timestampField = new DateTimeDataField("v", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: true);
        var decimalField = new DecimalDataField("v", 18, 2, isNullable: true);
        var stringField = new DataField<string>("v");
        var sink = new MemoryStream(64 * 1024 * 1024);

        async Task Write(DataField field, Func<ParquetRowGroupWriter, Task> write) {
            sink.SetLength(0);
            await using ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), sink);
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg);
        }

        async Task Read(byte[] file, Func<ParquetRowGroupReader, DataField, Task> read) {
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            await read(rg, reader.Schema.GetDataFields()[0]);
        }

        long[] longs = Data.Longs();
        double?[] doubles = Data.Doubles(0.1);
        DateTime?[] timestamps = Data.Timestamps();
        decimal?[] decimals = Data.Decimals();
        ReadOnlyMemory<char>?[] strings = Data.Strings(500_000).Select(s => s == null ? (ReadOnlyMemory<char>?)null : s.AsMemory()).ToArray();
        int[] definitions = new int[Data.Rows];

        switch(scenario) {
            case "write-int64": return () => Write(longField, rg => rg.WriteAsync<long>(longField, longs));
            case "write-double": return () => Write(doubleField, rg => rg.WriteAsync<double>(doubleField, doubles));
            case "write-timestamp": return () => Write(timestampField, rg => rg.WriteAsync<DateTime>(timestampField, timestamps));
            case "write-decimal": return () => Write(decimalField, rg => rg.WriteAsync<decimal>(decimalField, decimals));
            case "write-strings": return () => Write(stringField, rg => rg.WriteAsync<ReadOnlyMemory<char>>(stringField, strings));
        }

        async Task<byte[]> FileOf(DataField field, Func<ParquetRowGroupWriter, Task> write) {
            await Write(field, write);
            return sink.ToArray();
        }

        switch(scenario) {
            case "read-int64": {
                    byte[] file = await FileOf(longField, rg => rg.WriteAsync<long>(longField, longs));
                    long[] buffer = new long[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadRawAsync<long>(f, buffer, null, null).AsTask());
                }
            case "read-double": {
                    byte[] file = await FileOf(doubleField, rg => rg.WriteAsync<double>(doubleField, doubles));
                    double?[] buffer = new double?[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadAsync<double>(f, new Memory<double?>(buffer)).AsTask());
                }
            case "read-timestamp": {
                    byte[] file = await FileOf(timestampField, rg => rg.WriteAsync<DateTime>(timestampField, timestamps));
                    DateTime?[] buffer = new DateTime?[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadAsync<DateTime>(f, new Memory<DateTime?>(buffer)).AsTask());
                }
            case "read-decimal": {
                    byte[] file = await FileOf(decimalField, rg => rg.WriteAsync<decimal>(decimalField, decimals));
                    decimal?[] buffer = new decimal?[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadAsync<decimal>(f, new Memory<decimal?>(buffer)).AsTask());
                }
            case "read-strings": {
                    byte[] file = await FileOf(stringField, rg => rg.WriteAsync<ReadOnlyMemory<char>>(stringField, strings));
                    var buffer = new ReadOnlyMemory<char>[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadRawAsync<ReadOnlyMemory<char>>(f, buffer, definitions, null).AsTask());
                }
            case "read-strings-utf8": {
                    byte[] file = await FileOf(stringField, rg => rg.WriteAsync<ReadOnlyMemory<char>>(stringField, strings));
                    var buffer = new ReadOnlyMemory<byte>[Data.Rows];
                    return () => Read(file, (rg, f) => rg.ReadRawAsync<ReadOnlyMemory<byte>>(f, buffer, definitions, null).AsTask());
                }
        }
        throw new ArgumentException(scenario);
    }
}
