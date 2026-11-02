namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Refuse links, directories and special files before a bounded reader performs a potentially blocking open.
    /// Callers retain their existing workspace/source authorization and ancestor-path checks.
    /// </summary>
    internal async Task RequireRegularFileAsync(string workspace, string fullPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("A regular-file preflight requires an absolute path.");
        RejectNonFileAttributes(File.GetAttributes(fullPath));
        if (!OperatingSystem.IsWindows())
        {
            // Public BCL file attributes omit Unix FIFO/device/socket kinds. The fixed OS predicate supplies
            // that missing check through the existing bounded argv adapter, without a shell or native ABI.
            var executable = OperatingSystem.IsMacOS() ? "/bin/test" : "/usr/bin/test";
            var regular = await RunProgramAsync(workspace, executable, ["-f", fullPath], 2, ct);
            if (regular.IsError) throw new InvalidDataException("Only regular files can be read; special files are not supported.");
            RejectNonFileAttributes(File.GetAttributes(fullPath));
        }
        ct.ThrowIfCancellationRequested();
    }

    private static void RejectNonFileAttributes(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Only regular files can be read; links, directories and device files are not supported.");
    }
}
