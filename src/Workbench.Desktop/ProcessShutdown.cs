using System.ComponentModel;
using System.Diagnostics;

namespace Workbench.Desktop;

internal static class ProcessShutdown
{
    internal static bool IsShutdownRace(Exception exception) => exception is
        InvalidOperationException or Win32Exception or AggregateException or IOException;

    internal static void Terminate(Process process) => Terminate(
        () => { if (!process.HasExited) process.Kill(entireProcessTree: true); },
        () => { if (!process.HasExited) process.Kill(); });

    internal static void Terminate(Action killTree, Action killRoot)
    {
        try { killTree(); }
        catch (Exception ex) when (IsShutdownRace(ex))
        {
            // Child exit/access races may make Kill(true) throw even when our root is still alive.
            // Reuse the owned Process/handle: looking up the PID again could target a recycled ID.
            try { killRoot(); }
            catch (Exception fallback) when (IsShutdownRace(fallback)) { }
        }
    }
}
