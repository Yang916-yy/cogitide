using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Host.RemoteService;

internal static class RemoteServiceCommandLine
{
    internal static async Task<int> RunAsync(string[] args, string dataDirectory)
    {
        try
        {
            var command = args[0];
            var expectedIndex = Array.IndexOf(args, "--expected-service-id");
            var expected = expectedIndex >= 0 && expectedIndex + 1 < args.Length ? args[expectedIndex + 1] : null;
            if (expected is not null && !Guid.TryParseExact(expected, "N", out _)) throw new ArgumentException("The expected service identity is invalid.");
            switch (command)
            {
                case "--service":
                {
                    Directory.CreateDirectory(dataDirectory);
                    using var owner = new FileStream(Path.Combine(dataDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var files = new RemoteServiceFiles(dataDirectory, create: true);
                    RemoteHostService? service = null;
                    await using var host = new WorkbenchHost(dataDirectory, notificationSink: notification => service?.PublishAsync(notification) ?? Task.CompletedTask);
                    await host.InitializeAsync();
                    await using (service = new RemoteHostService(files, host.DispatchAsync))
                    {
                        await service.StartAsync();
                        try { await service.WaitForShutdownAsync(); }
                        finally { await host.ShutdownAsync(); }
                    }
                    return 0;
                }
                case "--service-bridge":
                {
                    if (expected is null) throw new ArgumentException("Attachment requires the previously verified service identity.");
                    var endpoint = await new RemoteServiceFiles(dataDirectory).ReadAsync(expected);
                    // Console.In/Out synchronize their async methods and can block before returning
                    // a Task. Use the BCL stream wrappers directly so pipe waits remain asynchronous.
                    using var inputStream = Console.OpenStandardInput();
                    using var outputStream = Console.OpenStandardOutput();
                    using var input = new StreamReader(inputStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                        bufferSize: 4096, leaveOpen: true);
                    var output = new StreamWriter(outputStream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);
                    await RemoteServiceBridge.RunAsync(endpoint, input, output);
                    // Every completed frame flushes explicitly. Do not Dispose the writer here: its
                    // implicit flush could block on abandoned pipe I/O. Owned streams close directly;
                    // the managed wrapper/buffer has no independent handle and is collected normally.
                    return 0;
                }
                case "--service-start":
                {
                    var files = new RemoteServiceFiles(dataDirectory, create: true);
                    var identity = files.GetOrCreateServiceId();
                    if (expected is not null && expected != identity) throw new InvalidDataException("The installed service identity changed; no service was started.");
                    var running = await TryVerifyAsync(files, identity);
                    if (running is null)
                    {
                        StartDetached(files, dataDirectory);
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        while (running is null)
                        {
                            await Task.Delay(100, deadline.Token);
                            running = await TryVerifyAsync(files, identity, deadline.Token);
                        }
                    }
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(running, Wire.Json));
                    return 0;
                }
                case "--service-stop":
                {
                    if (expected is null) throw new ArgumentException("Stopping requires the previously verified service identity.");
                    var endpoint = await new RemoteServiceFiles(dataDirectory).ReadAsync(expected);
                    await using var connection = new RemoteServiceConnection(endpoint);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await connection.VerifyAsync(deadline.Token);
                    using var daemon = Process.GetProcessById(endpoint.Identity.ProcessId);
                    // Capture the authenticated daemon before asking it to stop, so PID reuse cannot turn
                    // shutdown acceptance into an observation about an unrelated later process.
                    var reply = await connection.RequestAsync(new("stop", "remote.service.shutdown", Wire.Element(new { })), deadline.Token);
                    if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
                    await daemon.WaitForExitAsync(deadline.Token);
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { stopped = true, serviceId = expected }, Wire.Json));
                    return 0;
                }
                default: throw new ArgumentException("Unknown service command.");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Never print endpoint descriptors or HTTP headers containing the service token.
            await Console.Error.WriteLineAsync(e is OperationCanceledException
                ? "Service startup/stop timed out; the remote outcome is unknown. Inspect service status before retrying."
                : "Remote service operation failed: " + e.Message);
            return 1;
        }
    }

    private static async Task<RemoteServiceIdentity?> TryVerifyAsync(RemoteServiceFiles files, string identity, CancellationToken ct = default)
    {
        try
        {
            var endpoint = await files.ReadAsync(identity, ct);
            await using var connection = new RemoteServiceConnection(endpoint);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(1));
            return await connection.VerifyAsync(probe.Token);
        }
        catch (Exception e) when (e is FileNotFoundException or HttpRequestException or OperationCanceledException) { return null; }
    }

    private static void StartDetached(RemoteServiceFiles files, string dataDirectory)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Detached remote services currently require a POSIX SSH/WSL endpoint.");
        files.PrepareLog();
        var command = WorkbenchHost.CommandWorkerStart();
        command.ArgumentList.RemoveAt(command.ArgumentList.Count - 1);
        var arguments = new[] { command.FileName }.Concat(command.ArgumentList).Concat(["--service", "--data-dir", dataDirectory]);
        string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        // nohup plus explicit descriptor redirection prevents the daemon retaining SSH channel handles.
        // All fields are independently quoted; no configured startup command is accepted.
        start.ArgumentList.Add("umask 077; nohup " + string.Join(' ', arguments.Select(Quote)) + " </dev/null >>" + Quote(files.LogPath) + " 2>&1 &");
        using var launcher = Process.Start(start) ?? throw new IOException("The detached service launcher did not start.");
        launcher.WaitForExit();
        if (launcher.ExitCode != 0) throw new IOException("The detached service launcher failed; inspect endpoint-local service logs.");
    }
}
