using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ZeroMapper;

BenchmarkSwitcher.FromAssembly(typeof(FlatMapBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class FlatMapBenchmarks
{
    private readonly Input source = new() { Id = 42, Name = "Ada" };
    [Benchmark(Baseline = true)] public Output Manual() => new() { Id = source.Id, Name = source.Name };
    [Benchmark] public Output Generated() => Maps.Map(source);
}

public sealed class Input { public int Id { get; set; } public string Name { get; set; } = ""; }
public sealed class Output { public int Id { get; set; } public string Name { get; set; } = ""; }
[Mapper] public static partial class Maps { public static partial Output Map(Input source); }
