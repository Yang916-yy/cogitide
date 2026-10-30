using L = Workbench.Desktop.Localization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;

namespace Workbench.Desktop;

/// <summary>A user-owned PTY. Agent commands continue to use the audited Host tool path.</summary>
internal sealed class WorkbenchTerminalWindow : Window
{
    private readonly TerminalControl _terminal;
    private readonly TextBlock _status;
    private readonly string _workspace;
    private bool _closing;
    private int _processId;
    private Process? _shellProcess;
    internal int ShellProcessId => _processId;
    internal TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkbenchTerminalWindow(string workspace)
    {
        _workspace = workspace;
        Title = L.Format("TerminalTitle", new DirectoryInfo(workspace).Name);
        L.Changed += RefreshLocalization;
        Width = 1100;
        Height = 700;
        MinWidth = 700;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#101217");
        _terminal = new TerminalControl
        {
            Process = "", // Launch explicitly after layout so failures can be shown in the window.
            StartingDirectory = workspace,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 14,
            BufferSize = 5000,
            Background = Brush.Parse("#101217"),
            Foreground = Brush.Parse("#D5DBE8"),
            Margin = new Thickness(12, 8)
        };
        _status = new TextBlock { Text = workspace, FontSize = 11, Foreground = Brush.Parse("#A0ABBF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var interrupt = new Button { Content = L.Text("Interrupt"), Margin = new Thickness(12, 0, 0, 0) };
        LocalizedControl.Content(interrupt, "Interrupt");
        interrupt.Click += async (_, _) =>
        {
            try { await _terminal.SendInputAsync("\u0003", CancellationToken.None); }
            catch (Exception ex) when (ProcessShutdown.IsShutdownRace(ex) || ex is OperationCanceledException)
            { LocalizedControl.Text(_status, "TerminalEnded"); }
        };
        var close = new Button { Content = L.Text("CloseTerminal"), Margin = new Thickness(8, 0, 0, 0) };
        LocalizedControl.Content(close, "CloseTerminal");
        close.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(16, 10) };
        header.Children.Add(_status);
        Grid.SetColumn(interrupt, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(interrupt);
        header.Children.Add(close);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(header);
        Grid.SetRow(_terminal, 1);
        grid.Children.Add(_terminal);
        var hint = new TextBlock { Text = L.Text("TerminalHint"), FontSize = 10, Foreground = Brush.Parse("#7D879D"), Margin = new Thickness(16, 8) };
        LocalizedControl.Text(hint, "TerminalHint");
        Grid.SetRow(hint, 2);
        grid.Children.Add(hint);
        Content = grid;
        _terminal.ProcessExited += (_, args) => Dispatcher.UIThread.Post(() =>
        {
            ReleaseShellProcess();
            LocalizedControl.Text(_status, "ProcessEnded", args.ExitCode);
            interrupt.IsEnabled = false;
        });
        Opened += async (_, _) =>
        {
            try
            {
                var (shell, args) = FindShell();
                await _terminal.LaunchProcess(_workspace, shell, args);
                if (!_terminal.IsLive) { Started.TrySetResult(0); return; }
                var process = Process.GetProcessById(_terminal.Pid);
                try
                {
                    // Retain the native handle for this exact shell until exit/close. Windows cannot
                    // recycle its process ID while this reference is open.
                    if (OperatingSystem.IsWindows()) _ = process.SafeHandle;
                    if (process.HasExited) { process.Dispose(); Started.TrySetResult(0); return; }
                    _shellProcess = process;
                    _processId = process.Id;
                }
                catch { process.Dispose(); throw; }
                if (_closing) StopProcess();
                else _terminal.Focus();
                Started.TrySetResult(_processId);
            }
            catch (Exception ex) { LocalizedControl.Text(_status, "TerminalStartFailed", ex.Message); Started.TrySetException(ex); }
        };
        Closing += (_, _) => { _closing = true; StopProcess(); };
        Closed += (_, _) =>
        {
            L.Changed -= RefreshLocalization;
            try { _terminal.Dispose(); }
            catch (Exception ex) when (ProcessShutdown.IsShutdownRace(ex) || ex is NullReferenceException) { }
            finally { ReleaseShellProcess(); }
        };
    }

    private void RefreshLocalization() => Title = L.Format("TerminalTitle", new DirectoryInfo(_workspace).Name);

    private void StopProcess()
    {
        if (_shellProcess is { } process) ProcessShutdown.Terminate(process);
        try { _terminal.Kill(); }
        catch (Exception ex) when (ProcessShutdown.IsShutdownRace(ex) || ex is NullReferenceException) { }
        finally { ReleaseShellProcess(); }
    }

    private void ReleaseShellProcess()
    {
        _processId = 0;
        _shellProcess?.Dispose();
        _shellProcess = null;
    }

    private static (string Shell, string[] Arguments) FindShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(path => Path.Combine(path.Trim('"'), "pwsh.exe")).FirstOrDefault(File.Exists);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            return (pwsh ?? (File.Exists(powershell) ? powershell : "powershell.exe"), ["-NoLogo", "-NoProfile"]);
        }
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !Path.IsPathRooted(shell) || !File.Exists(shell)) shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (shell, ["-i"]);
    }
}
