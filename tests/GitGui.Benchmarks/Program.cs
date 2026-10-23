using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using GitGui.Engine.Cli;

#if NET10_0_OR_GREATER
if (args.FirstOrDefault() == "--describe-read-jobs")
{
    RepositoryReadBenchmarks.DescribeResolvedJobs(); return;
}
#endif
if (args.FirstOrDefault() == "--prepare")
{
    await LargeRepositoryBenchmark.Prepare(args[1], args.Length > 2 ? int.Parse(args[2]) : 100_000,
        args.Length > 3 ? int.Parse(args[3]) : 50_000); return;
}
if (args.FirstOrDefault() == "--measure")
{
    await LargeRepositoryBenchmark.Measure(args[1], args[2]); return;
}
BenchmarkSwitcher.FromAssembly(typeof(StatusParserBenchmark).Assembly).Run(args);

[MemoryDiagnoser]
public class StatusParserBenchmark
{
    private readonly byte[] data = Encoding.UTF8.GetBytes("# branch.head main\0" +
        string.Concat(Enumerable.Range(0, 50_000).Select(i => $"? directory/file-{i}.txt\0")));

    [Benchmark]
    public int ParseFiftyThousandFiles() => PorcelainStatusParser.Parse(data).Files.Count;
}
