using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Workbench.Protocol;
using Workbench.Tools;

if (args is ["--semantic-series", var seriesCount, var seriesReport])
{
    if (!int.TryParse(seriesCount, out var count) || count is < 2 or > 50) throw new ArgumentException("Use 2..50 calls.");
    var fixture = new ToolBenchmarks { WarmSemantic = false };
    fixture.Setup();
    var samples = new List<object>();
    try
    {
        using var process = Process.GetCurrentProcess();
        for (var i = 0; i < count; i++)
        {
            var bytes = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            var result = await fixture.SemanticReferences();
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(true) - bytes;
            if (result.IsError) throw new InvalidDataException(result.Content);
            process.Refresh();
            samples.Add(new { sample = i, firstSemanticCall = i == 0, milliseconds = elapsed,
                managedBytes = allocated, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64 });
        }
        await File.WriteAllTextAsync(seriesReport, JsonSerializer.Serialize(new
        {
            description = "First semantic call plus repeats in one fresh process; setup did not warm semantics. OS caches are not cleared. Process-wide allocations and resident memory include Roslyn/runtime state.",
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally { fixture.Cleanup(); }
    return;
}

if (args is ["--allocation-summary", var tracePath, var summaryPath])
{
    // TraceEvent is already a BenchmarkDotNet dependency. Use its EventPipe parser, not a custom trace format.
    var types = new Dictionary<string, (long SampledBytes, long Events)>(StringComparer.Ordinal);
    using var source = new Microsoft.Diagnostics.Tracing.EventPipeEventSource(tracePath);
    source.Clr.GCAllocationTick += data =>
    {
        var name = data.TypeName ?? "unknown";
        types.TryGetValue(name, out var old);
        types[name] = (checked(old.SampledBytes + data.AllocationAmount64), old.Events + 1);
    };
    source.Process();
    await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(new
    {
        description = "Sampled GC allocation ticks, including startup/warmup; not exact byte totals or per-operation allocations.",
        types = types.OrderByDescending(pair => pair.Value.SampledBytes).Take(30)
            .Select(pair => new { type = pair.Key, sampledBytes = pair.Value.SampledBytes, events = pair.Value.Events })
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args is ["--workload", var operation, var countText, var report])
{
    if (!int.TryParse(countText, out var count) || count is < 1 or > 500)
        throw new ArgumentException("Workload iterations must be 1..500.");
    var fixture = new ToolBenchmarks();
    fixture.Setup();
    try
    {
        Func<Task<ToolResult>> run = operation switch
        {
            "read" => fixture.ReadPage, "literal" => fixture.LiteralSearch,
            "regex" => fixture.RegexSearch, "syntax" => fixture.SyntaxOutline,
            "semantic" => fixture.SemanticReferences,
            _ => throw new ArgumentException("Use read, literal, regex, syntax or semantic.")
        };
        for (var warmup = 0; warmup < 3; warmup++) _ = await run();
        var samples = new List<double>();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var generations = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        for (var i = 0; i < count; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = await run();
            samples.Add(watch.Elapsed.TotalMilliseconds);
            if (result.IsError) throw new InvalidDataException("Measured workload failed: " + result.Content);
        }
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        process.Refresh();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            operation, iterations = count, milliseconds = samples,
            managedBytesPerOperation = (double)bytes / count,
            cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - generations[i]).ToArray(),
            process.PeakWorkingSet64,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            description = "Actual tool calls after three warmups; process-wide managed allocation. Not BDN or a model benchmark."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally { fixture.Cleanup(); }
    return;
}

if (args is ["--host-startup", var dotnet, var hostDll, var output])
{
    var measurements = new List<object>();
    for (var sample = 0; sample < 10; sample++)
    {
        var state = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "host-state-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(hostDll));
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(state);
        foreach (var name in start.Environment.Keys.Where(name => name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException("Host did not start.");
        var diagnostics = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":\"startup\",\"method\":\"initialize\",\"params\":{}}");
            await process.StandardInput.FlushAsync();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("Host exited before initialize.");
                using var reply = JsonDocument.Parse(line);
                if (!reply.RootElement.TryGetProperty("id", out var id) || id.GetString() != "startup") continue;
                if (reply.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new IOException("Host initialize failed.");
                break;
            }
            watch.Stop(); process.Refresh();
            var milliseconds = watch.Elapsed.TotalMilliseconds;
            var workingSet = process.WorkingSet64;
            var privateBytes = process.PrivateMemorySize64;
            var cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new IOException("Host did not exit cleanly.");
            await diagnostics;
            measurements.Add(new { sample, milliseconds, workingSet, privateBytes, cpuMilliseconds });
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        measuredAtUtc = DateTimeOffset.UtcNow, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, processorCount = Environment.ProcessorCount,
        description = "Ten fresh processes; first includes cold file-cache effects; later runs share OS caches. No model, browser or MCP server calls.",
        measurements
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
// BDN 0.15.8 does not recognize the .NET 11 RC runtime moniker in its external SDK validator.
// Its supported in-process emit toolchain runs on the actual pinned runtime without substituting .NET 10.
BenchmarkSwitcher.FromAssembly(typeof(ToolBenchmarks).Assembly).Run(args,
    DefaultConfig.Instance.AddJob(Job.ShortRun.WithIterationCount(10).WithToolchain(InProcessEmitToolchain.Instance).WithId("Net11InProcess")));

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class ToolBenchmarks
{
    private readonly WorkspaceService _workspace = new();
    private string _root = "";
    public bool WarmSemantic { get; set; } = true;

    [GlobalSetup]
    public void Setup()
    {
        var parent = Environment.GetEnvironmentVariable("COGITIDE_BENCH_ROOT") ?? Path.Combine(Path.GetTempPath(), "cogitide-benchmarks");
        _root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        for (var file = 0; file < 128; file++)
            File.WriteAllText(Path.Combine(_root, "data", $"file{file:D3}.txt"),
                string.Join('\n', Enumerable.Range(0, 120).Select(line => line == 50
                    ? $"needle_{file:D3} multilingual 中文 deterministic match"
                    : $"line {line:D3}: deterministic repository fixture, no external services.")) + "\n");
        Directory.CreateDirectory(Path.Combine(_root, "semantic"));
        File.WriteAllText(Path.Combine(_root, "semantic", "Bench.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "semantic", "Service.cs"), "public static class Arithmetic\n{\n    public static int Add(int a, int b) => a + b;\n}\n");
        for (var file = 0; file < 30; file++)
            File.WriteAllText(Path.Combine(_root, "semantic", $"Caller{file}.cs"), $"public class Caller{file} {{ public int Run() => Arithmetic.Add({file}, 2); }}\n");
        // Validate the fixture once outside timed operations. Semantic binding and textual matching have different jobs.
        Require(_workspace.ReadFilePageAsync(_root, "data/file000.txt", limit: 20).GetAwaiter().GetResult());
        if (WarmSemantic) Require(_workspace.CSharpSemanticAsync(_root, "references", "semantic/Service.cs", 3, 23).GetAwaiter().GetResult());
    }

    [Benchmark] public Task<ToolResult> ReadPage() => _workspace.ReadFilePageAsync(_root, "data/file000.txt", limit: 20);
    [Benchmark] public Task<ToolResult> LiteralSearch() => _workspace.SearchFilePageAsync(_root, "needle_007", "data/");
    [Benchmark] public Task<ToolResult> RegexSearch() => _workspace.SearchFilePageAsync(_root, @"\bneedle_00[0-9]\b", "data/", regex: true);
    [Benchmark] public Task<ToolResult> SyntaxOutline() => _workspace.InspectSymbolsAsync(_root, "semantic/Service.cs");
    [Benchmark] public Task<ToolResult> SemanticReferences() => _workspace.CSharpSemanticAsync(_root, "references", "semantic/Service.cs", 3, 23);

    [GlobalCleanup]
    public void Cleanup()
    {
        // The exact owned directory was created in Setup; never recursively delete a configured parent.
        if (_root.Length == 0 || !Directory.Exists(_root)) return;
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Benchmark root changed into a link.");
        Directory.Delete(_root, true);
    }
    private static void Require(ToolResult result)
    {
        if (result.IsError) throw new InvalidDataException("Benchmark fixture did not satisfy the real tool contract: " + result.Content);
    }
}
