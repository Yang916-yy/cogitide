using System.Text.Json;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class WorkspaceBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-file-browser-" + Guid.NewGuid().ToString("N"));

    public WorkspaceBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
    }

    [Fact]
    public async Task ScopedServerFilterAndPagesReachLateFilesAndRejectInventoryChanges()
    {
        for (var index = 0; index < 405; index++) await File.WriteAllTextAsync(Path.Combine(_root, "src", $"file{index:D3}.cs"), "class C { }");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "file404.cs"), "outside requested prefix");
        var service = new WorkspaceService();
        using var first = JsonDocument.Parse((await service.BrowseFilesAsync(_root, "src/", limit: 200)).Content);
        Assert.Equal(405, first.RootElement.GetProperty("totalMatchesInInventory").GetInt32());
        using var last = JsonDocument.Parse((await service.BrowseFilesAsync(_root, "src/", offset: 400, limit: 200,
            expectedSnapshot: first.RootElement.GetProperty("snapshot").GetString())).Content);
        Assert.Equal(5, last.RootElement.GetProperty("files").GetArrayLength());
        using var filtered = JsonDocument.Parse((await service.BrowseFilesAsync(_root, "src/", "404")).Content);
        Assert.Equal("src/file404.cs", Assert.Single(filtered.RootElement.GetProperty("files").EnumerateArray()).GetString());
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "added.cs"), "class Added { }");
        var stale = await service.BrowseFilesAsync(_root, "src/", offset: 200,
            expectedSnapshot: first.RootElement.GetProperty("snapshot").GetString());
        Assert.True(stale.IsError);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BrowseFilesAsync(_root, "../"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
