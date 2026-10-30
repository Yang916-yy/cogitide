using System.Diagnostics;
using System.Text;
using Porta.Pty;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    internal static async Task<ProcessResult> RunInputCommandAsync(string workspace, string command, int timeoutSeconds,
        bool tty, Action<Stream> bindInput, Func<string, CancellationToken, ValueTask> onOutput, CancellationToken ct)
    {
        var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh";
        var shellCommand = OperatingSystem.IsWindows()
            ? "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; " + command
            : command;
        string[] arguments = OperatingSystem.IsWindows() ? ["-NoLogo", "-NoProfile", "-Command", shellCommand] : ["-c", shellCommand];
        if (!tty)
            return await ExecuteAsync(shell, arguments, Root(workspace), timeoutSeconds, onOutput, ct, bindInput: bindInput);

        // Porta.Pty is the existing Desktop PTY dependency, including its ConPTY/native Unix
        // backend. This adapter adds ownership and output budgets, not terminal emulation or P/Invoke.
        var environment = new Dictionary<string, string>();
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))) environment[name] = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var terminal = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "Cogitide owned command", App = shell, CommandLine = arguments,
            Cwd = Root(workspace), Cols = 120, Rows = 30, Environment = environment, UseAsyncIo = true
        }, linked.Token);
        Process? ownedProcess = null;
        try { ownedProcess = Process.GetProcessById(terminal.Pid); }
        catch (ArgumentException) when (terminal.WaitForExit(0)) { /* A short-lived command may already be reaped. */ }
        using var process = ownedProcess;
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.ProcessExited += (_, exit) => exited.TrySetResult(exit.ExitCode);
        if (terminal.WaitForExit(0)) exited.TrySetResult(terminal.ExitCode);
        bindInput(terminal.WriterStream);
        void KillTerminalIfRunning()
        {
            try { if (!terminal.WaitForExit(0)) terminal.Kill(); }
            catch (InvalidOperationException) when (terminal.WaitForExit(0)) { /* Shared tree cleanup won the exit race. */ }
        }
        async Task DrainAsync()
        {
            using var reader = new StreamReader(terminal.ReaderStream, new UTF8Encoding(false, false), false, 4096, leaveOpen: true);
            var buffer = new char[2048];
            string carry = "";
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), linked.Token);
                if (count == 0)
                {
                    if (carry.Length != 0) await onOutput("\uFFFD", linked.Token);
                    return;
                }
                var text = carry + new string(buffer, 0, count);
                carry = "";
                if (char.IsHighSurrogate(text[^1])) { carry = text[^1].ToString(); text = text[..^1]; }
                if (text.Length != 0) await onOutput(text, linked.Token);
            }
        }
        var output = DrainAsync();
        try
        {
            // Observe sink/read failures while the process is still alive so a failing output
            // store cannot leave a session running until its (potentially long) expiry.
            var first = await Task.WhenAny(exited.Task, output).WaitAsync(linked.Token);
            if (first == output) await output;
            var code = await exited.Task.WaitAsync(linked.Token);
            await output.WaitAsync(linked.Token);
            return new(code, "", false, false);
        }
        catch (OperationCanceledException)
        {
            if (process is not null) await StopProcessAsync(process); // Includes the shared pinned-runtime macOS workaround.
            KillTerminalIfRunning();
            try { await output.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            ct.ThrowIfCancellationRequested();
            return new(-1, "", timeout.IsCancellationRequested, false);
        }
        catch
        {
            if (process is not null) await StopProcessAsync(process);
            KillTerminalIfRunning();
            try { await output.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            throw;
        }
    }
}
