using Avalonia;

namespace Workbench.Desktop;

internal static class Program
{
    internal static bool SmokeTest { get; private set; }
    internal static string? ScreenshotPath { get; private set; }
    internal static string? DataDirectory { get; private set; }
    internal static string? WorkspacePath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        SmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        var captureIndex = Array.IndexOf(args, "--screenshot");
        if (captureIndex >= 0 && captureIndex + 1 < args.Length) ScreenshotPath = Path.GetFullPath(args[captureIndex + 1]);
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length) DataDirectory = Path.GetFullPath(args[dataIndex + 1]);
        else if (SmokeTest) DataDirectory = Path.Combine(Path.GetTempPath(), "Workbench-Smoke", Guid.NewGuid().ToString("N"));
        var workspaceIndex = Array.IndexOf(args, "--workspace");
        if (workspaceIndex >= 0 && workspaceIndex + 1 < args.Length) WorkspacePath = Path.GetFullPath(args[workspaceIndex + 1]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
