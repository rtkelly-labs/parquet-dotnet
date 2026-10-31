using BenchmarkDotNet.Running;
using Lake.Parquet.Benchmarks;

if(args.Length > 0 && args[0] == "profile")
    return await Profile.RunAsync(args);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;

public partial class Program { }
