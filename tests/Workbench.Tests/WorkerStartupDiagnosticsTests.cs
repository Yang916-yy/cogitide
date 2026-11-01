using System.Text;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class WorkerStartupDiagnosticsTests
{
    [Fact]
    public async Task StartupEvidenceKeepsErrorCodesAndRedactsPathsAndCredentialShapes()
    {
        var diagnostics = new WorkerStartupDiagnostics([@"C:\private\runtime"]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Failed to load C:\\private\\runtime\\coreclr.dll; HRESULT 0x80070005\nBearer abc.def.ghi api_key=fixture-secret password=fixture-password"));
        await diagnostics.DrainAsync(stream);
        var summary = diagnostics.Summary();
        Assert.Contains("0x80070005", summary);
        Assert.Contains("coreclr.dll", summary);
        Assert.DoesNotContain(@"C:\private\runtime", summary);
        Assert.DoesNotContain("abc.def.ghi", summary);
        Assert.DoesNotContain("fixture-secret", summary);
        Assert.DoesNotContain("fixture-password", summary);
    }

    [Fact]
    public async Task StderrIsAlwaysDrainedButOnlyABoundedPrefixIsRetained()
    {
        var diagnostics = new WorkerStartupDiagnostics([]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 32_768)));
        await diagnostics.DrainAsync(stream);
        Assert.Equal(stream.Length, stream.Position);
        Assert.InRange(diagnostics.Summary().Length, 1, 2100);
    }
}
