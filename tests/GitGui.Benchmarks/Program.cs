using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using GitGui.Engine.Cli;

BenchmarkRunner.Run<StatusParserBenchmark>();

[MemoryDiagnoser]
public class StatusParserBenchmark
{
    private readonly byte[] data = Encoding.UTF8.GetBytes("# branch.head main\0" +
        string.Concat(Enumerable.Range(0, 50_000).Select(i => $"? directory/file-{i}.txt\0")));

    [Benchmark]
    public int ParseFiftyThousandFiles() => PorcelainStatusParser.Parse(data).Files.Count;
}
