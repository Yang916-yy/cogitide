using System.ComponentModel;
using Workbench.Desktop;
using Xunit;

namespace Workbench.Tests;

public sealed class DesktopProcessShutdownTests
{
    [Fact]
    public void FallsBackToOwnedRootWhenTreeEnumerationRacesWithExit()
    {
        foreach (var failure in new Exception[]
        {
            new AggregateException(new Win32Exception(5)),
            new Win32Exception(5),
            new InvalidOperationException("Process has already exited")
        })
        {
            var rootAttempts = 0;
            ProcessShutdown.Terminate(() => throw failure, () => rootAttempts++);
            Assert.Equal(1, rootAttempts);
        }
    }

    [Fact]
    public void ToleratesRootExitingDuringFallbackWithoutRepeatingSuccessfulTreeKill()
    {
        ProcessShutdown.Terminate(
            () => throw new AggregateException(new Win32Exception(5)),
            () => throw new InvalidOperationException("Process has already exited"));
        var rootAttempts = 0;
        ProcessShutdown.Terminate(() => { }, () => rootAttempts++);
        Assert.Equal(0, rootAttempts);
    }
}
