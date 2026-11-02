using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class SkillManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(OsIsolationTestPaths.PhysicalTemporaryDirectory(), "cogitide-skills-fixture-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "workspace");
    public SkillManagementTests() => Directory.CreateDirectory(Workspace);
    private static string Manifest(string name, string description = "Build and validate fixtures.") =>
        "---\nname: " + name + "\ndescription: " + description + "\n---\nReference only; do not execute scripts implicitly.\n";

    [Fact]
    public async Task ImportCopiesOnlyTheSelectedPackageAndNeverExecutesScriptsOrOverwrites()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "import-package")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), Manifest("fixture"));
        await File.WriteAllTextAsync(Path.Combine(source, "scripts", "install.ps1"), "throw 'This must never execute'\n");
        await File.WriteAllTextAsync(Path.Combine(source, "LICENSE.txt"), "Fixture license");
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
        var result = await service.ImportAsync(Workspace, source);
        Assert.Equal("fixture", result.Name);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(Manifest("fixture"), await File.ReadAllTextAsync(Path.Combine(Workspace, result.Path, "SKILL.md")));
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Workspace, result.ProvenancePath)));
        Assert.False(provenance.RootElement.GetProperty("scriptsExecuted").GetBoolean());
        Assert.Equal(result.Sha256, provenance.RootElement.GetProperty("manifestSha256").GetString());
        Assert.Single((await new ProjectRules(Workspace, WorkspaceService.Resolve).DiscoverSkillsAsync()).Skills);
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(Workspace, source));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Workspace, ".agents", "skills"), ".install-*"));
    }

    [Fact]
    public async Task ImportOfAnInstalledFolderRegeneratesItsReceiptInAnotherWorkspaceWithoutOverwriting()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "original-package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), Manifest("reusable"));
        await File.WriteAllTextAsync(Path.Combine(source, "LICENSE.txt"), "Fixture license");
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
        var first = await service.ImportAsync(Workspace, source);
        var installedSource = Path.GetFullPath(Path.Combine(Workspace, first.Path));
        var firstReceipt = await File.ReadAllBytesAsync(Path.Combine(Workspace, first.ProvenancePath));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "second-workspace")).FullName;

        var second = await service.ImportAsync(destination, installedSource);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.FileCount, second.FileCount); // The old generated receipt is not copied as package content.
        Assert.Equal(first.Sha256, second.Sha256);
        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(destination, second.ProvenancePath)));
        Assert.Equal("Local directory import", receipt.RootElement.GetProperty("source").GetString());
        Assert.Equal(installedSource, receipt.RootElement.GetProperty("sourcePath").GetString());
        Assert.False(receipt.RootElement.GetProperty("scriptsExecuted").GetBoolean());
        Assert.DoesNotContain(receipt.RootElement.GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == ".cogitide-skill.json");
        Assert.Equal(firstReceipt, await File.ReadAllBytesAsync(Path.Combine(Workspace, first.ProvenancePath)));
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(destination, installedSource));
        Assert.Single((await new ProjectRules(destination, WorkspaceService.Resolve).DiscoverSkillsAsync()).Skills);
    }

    [Fact]
    public async Task SearchUsesPinnedCatalogMetadataWithoutInstallingAndInstallRetainsLicenses()
    {
        using var handler = new ArchiveHandler(Archive(
            ("skills/check/SKILL.md", Manifest("check", "Inspect C# source and build fixtures."), 0),
            ("skills/check/scripts/probe.ps1", "throw 'Do not execute'", 0),
            ("skills/check/LICENSE.txt", "Fixture Apache license", 0),
            ("skills/other/SKILL.md", Manifest("other", "Inspect a second source."), 0),
            ("skills/other/LICENSE.txt", "Fixture MIT license", 0),
            ("LICENSE", "Repository license", 0),
            ("THIRD_PARTY_NOTICES.md", "Repository notices", 0)));
        using var http = new HttpClient(handler);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        var page = await service.SearchAsync("Inspect", limit: 1);
        Assert.Single(page.Results);
        Assert.True(page.Truncated);
        Assert.Equal(1, page.NextOffset);
        Assert.False(page.CatalogIncomplete);
        Assert.Equal("anthropic:check", page.Results[0].Id);
        Assert.Equal(SkillManagementService.DefaultRevision, page.Results[0].Revision);
        Assert.Contains("untrusted", page.Warning);
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
        var installed = await service.InstallAsync(Workspace, page.Results[0].Id);
        Assert.Equal("check", installed.Name);
        Assert.Equal(1, handler.RequestCount); // The immutable archive is reused, not fetched again for each manifest.
        Assert.True(File.Exists(Path.Combine(Workspace, installed.Path, "LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(Workspace, installed.Path, ".cogitide-source-notices", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(Workspace, installed.Path, ".cogitide-source-notices", "THIRD_PARTY_NOTICES.md")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents", "skills", "other")));
        var lastPage = await service.SearchAsync("Inspect", offset: page.NextOffset!.Value, limit: 1);
        Assert.Equal("other", Assert.Single(lastPage.Results).Name);
        Assert.False(lastPage.Truncated);
        Assert.Null(lastPage.NextOffset);
        Assert.False(lastPage.CatalogIncomplete);
    }

    [Fact]
    public async Task OmittedCatalogEntriesDoNotAdvertiseEndlessOrEmptyContinuationPages()
    {
        using var handler = new ArchiveHandler(Archive(
            ("skills/check/SKILL.md", Manifest("check", "Inspect the source."), 0),
            ("skills/other/SKILL.md", Manifest("other", "Inspect another source."), 0),
            ("skills/oversized/SKILL.md", Manifest("oversized") + new string('x', 64 * 1024), 0)));
        using var http = new HttpClient(handler);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        var first = await service.SearchAsync("Inspect", limit: 1);
        Assert.True(first.Truncated);
        Assert.True(first.CatalogIncomplete);
        Assert.Equal(1, first.NextOffset);
        Assert.Contains("pagination cannot recover omitted entries", first.Warning);

        var last = await service.SearchAsync("Inspect", offset: first.NextOffset!.Value, limit: 1);
        Assert.Single(last.Results);
        Assert.False(last.Truncated);
        Assert.Null(last.NextOffset);
        Assert.True(last.CatalogIncomplete);
        foreach (var empty in new[]
        {
            await service.SearchAsync("Inspect", offset: 2, limit: 1),
            await service.SearchAsync("no-match", limit: 1)
        })
        {
            Assert.Empty(empty.Results);
            Assert.False(empty.Truncated);
            Assert.Null(empty.NextOffset);
            Assert.True(empty.CatalogIncomplete);
        }
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SourceEntryCapReportsIncompleteCatalogWithoutAdvertisingAnExtraPage()
    {
        var files = Enumerable.Range(0, 129).Select(index =>
        {
            var name = "fixture-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            return ("skills/" + name + "/SKILL.md", Manifest(name), 0);
        }).ToArray();
        using var handler = new ArchiveHandler(Archive(files));
        using var http = new HttpClient(handler);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        var last = await service.SearchAsync(offset: 127, limit: 1);
        Assert.Single(last.Results);
        Assert.True(last.CatalogIncomplete);
        Assert.False(last.Truncated);
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public async Task DownloadDeadlineCoversAStalledBodyAfterSuccessfulHeadersWithoutInstallingOrRetrying()
    {
        using var handler = new StalledDownloadHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http, TimeSpan.FromSeconds(1));
        var installing = service.InstallAsync(Workspace, repository: "fixture/skills", revision: SkillManagementService.DefaultRevision,
            path: "skills/check", ct: caller.Token);
        try
        {
            await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<IOException>(() => installing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Skill archive download timed out before completion; no skill was installed.", error.Message);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(handler.Body.Disposed);
            Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
        }
        finally { caller.Cancel(); }
    }

    [Fact]
    public async Task CallerCancellationDuringAStalledDownloadRemainsCancellation()
    {
        using var handler = new StalledDownloadHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        var searching = service.SearchAsync(ct: caller.Token);
        try
        {
            await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searching.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, handler.RequestCount);
            Assert.True(handler.Body.Disposed);
            Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
        }
        finally { caller.Cancel(); }
    }

    [Theory]
    [InlineData("https://github.com/owner/repository", "0123456789012345678901234567890123456789", "skills/check")]
    [InlineData("owner/repository", "main", "skills/check")]
    [InlineData("owner/repository", "0123456789012345678901234567890123456789", "../check")]
    [InlineData("owner/repository", "0123456789012345678901234567890123456789", "skills/check:stream")]
    public async Task CustomInstallRejectsUnpinnedOrUnsafeRequestsBeforeNetworking(string repository, string revision, string path)
    {
        using var handler = new ArchiveHandler([]);
        using var http = new HttpClient(handler);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(Workspace, repository: repository, revision: revision, path: path));
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workspace));
    }

    [Theory]
    [InlineData("skills/check/scripts/link", 0xA1FF)]
    [InlineData("skills/check/../escape", 0)]
    [InlineData("skills/check/CON.txt", 0)]
    [InlineData("skills/check/nested/.git/config", 0)]
    public async Task ArchiveLinksAndUnsafePathsFailBeforePublishing(string entryPath, int mode)
    {
        using var handler = new ArchiveHandler(Archive(
            ("skills/check/SKILL.md", Manifest("check"), 0),
            ("skills/check/LICENSE.txt", "Fixture license", 0),
            (entryPath, "unsafe", mode)));
        using var http = new HttpClient(handler);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Workspace, repository: "fixture/skills",
            revision: SkillManagementService.DefaultRevision, path: "skills/check"));
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
    }

    [Fact]
    public async Task CaseCollisionsOversizedFilesAndMissingLicensesAreRejectedAtomically()
    {
        foreach (var files in new[]
        {
            Archive(("skills/check/SKILL.md", Manifest("check"), 0), ("skills/check/LICENSE.txt", "license", 0),
                ("skills/check/readme.md", "a", 0), ("skills/check/README.md", "b", 0)),
            Archive(("skills/check/SKILL.md", Manifest("check"), 0), ("skills/check/LICENSE.txt", "license", 0),
                ("skills/check/large.bin", new string('x', SkillManagementService.MaxPackageFileBytes + 1), 0)),
            Archive(("skills/check/SKILL.md", Manifest("check"), 0), ("THIRD_PARTY_NOTICES.md", "Not a license", 0))
        })
        {
            using var handler = new ArchiveHandler(files);
            using var http = new HttpClient(handler);
            var service = new SkillManagementService(ProjectRules.ValidateSkillManifest, http);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Workspace, repository: "fixture/skills",
                revision: SkillManagementService.DefaultRevision, path: "skills/check"));
            Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
        }
    }

    [Fact]
    public async Task CancellationCannotPublishACompleteLookingSkill()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "cancel-package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), Manifest("cancelled"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(Workspace, source, cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
    }

    [Fact]
    public async Task ImportRequiresThePortableManifestFilenameBeforeWritingAnything()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "lowercase-manifest")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "skill.md"), Manifest("fixture"));
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Workspace, source));
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
    }

    [Fact]
    public async Task UnixImportRejectsAnUnopenedFifoWithoutHangingTheHost()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = Directory.CreateDirectory(Path.Combine(_root, "fifo-package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), Manifest("fixture"));
        var fifo = Path.Combine(source, "blocked-pipe");
        var created = await new WorkspaceService().RunProgramAsync(Workspace, "/usr/bin/mkfifo", [fifo], 2);
        Assert.False(created.IsError, created.Content);
        var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Workspace, source).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
    }

    [Fact]
    public void ModelInstallHasApprovalAndReportsItsActualDestinationScope()
    {
        var tools = SkillManagementTools.Create(new(ProjectRules.ValidateSkillManifest));
        Assert.False(tools.Single(tool => tool.Definition.Name == "search_skills").Definition.RequiresApproval);
        var install = tools.Single(tool => tool.Definition.Name == "install_skill");
        Assert.True(install.Definition.RequiresApproval);
        var paths = Assert.IsAssignableFrom<IWorkspacePathTool>(install).GetPaths(new("id", "install_skill", "{\"skillId\":\"anthropic:check\"}"), Workspace);
        Assert.Equal(new[] { ".agents/skills/check" }, paths);
    }

    [Fact]
    public async Task HostDirectImportAndInstallCannotBypassBusyWorkspaceAdmission()
    {
        await using var host = new WorkbenchHost(Path.Combine(_root, "state"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, Task<bool>> mutation = async _ => { entered.TrySetResult(); await release.Task; return true; };
        var held = (Task<bool>)typeof(WorkbenchHost).GetMethod("WithReviewMutationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(bool)).Invoke(host, [Workspace, mutation, "fixture mutation"])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var method in new[] { "project.skills.import", "project.skills.install" })
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DispatchAsync(method,
                    Wire.Element(new { workspacePath = Workspace, sourcePath = Path.Combine(_root, "not-read"), skillId = "anthropic:check" })));
                Assert.Contains("project skills", error.Message);
            }
        }
        finally { release.TrySetResult(); Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(5))); }
        Assert.False(Directory.Exists(Path.Combine(Workspace, ".agents")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KernelApprovalControlsInstallAndRefreshesTheSkillCatalogWithinTheSameRun(bool approved)
    {
        using var handler = new ArchiveHandler(Archive(
            ("skills/check/SKILL.md", Manifest("check"), 0), ("skills/check/LICENSE.txt", "Fixture license", 0)));
        using var http = new HttpClient(handler);
        using var store = new SqliteSessionStore(Path.Combine(_root, "kernel.db"));
        var at = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new("skill-session", "Skill fixture", Workspace, at, at), []));
        var model = new StepModel((step, messages, tools) =>
        {
            var system = messages.Single(message => message.Role == "system").Content;
            if (step == 0)
            {
                Assert.Contains(tools, tool => tool.Name == "install_skill" && tool.RequiresApproval);
                Assert.DoesNotContain("\"name\":\"check\"", system);
                return new(ToolCalls: [new("install", "install_skill", "{\"skillId\":\"anthropic:check\"}")]);
            }
            if (step == 1)
            {
                if (!approved)
                {
                    Assert.DoesNotContain("\"name\":\"check\"", system);
                    Assert.Contains(messages, message => message.Role == "tool" && message.ToolIsError == true && message.Content.Contains("declined"));
                    return new(TextDelta: "Installation declined; no further action.");
                }
                Assert.Contains("\"name\":\"check\"", system);
                Assert.Contains(tools, tool => tool.Name == "read_skill");
                return new(ToolCalls: [new("read", "read_skill", "{\"name\":\"check\"}")]);
            }
            Assert.Equal(2, step);
            Assert.Contains(messages, message => message.Role == "tool" && message.ToolCallId == "read" &&
                message.ToolIsError != true && message.Content.Contains("Reference only; do not execute scripts implicitly."));
            return new(TextDelta: "Selected skill is available as a reference.");
        });
        var events = new List<AgentEvent>();
        var approvals = 0;
        var engine = new AgentEngine(store, SkillManagementTools.Create(new(ProjectRules.ValidateSkillManifest, http)), WorkspaceService.Resolve);
        await engine.RunAsync("skill-session", "Install the selected anthropic:check skill, then read its instructions.", model,
            new(MaxIterations: 5, TimeoutSeconds: 30, AutoCompact: false),
            (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; },
            (request, _) => { approvals++; Assert.Equal("install_skill", request.ToolName); return Task.FromResult(approved); });
        Assert.Equal(1, approvals);
        Assert.Contains(events, evt => evt.Type == "run.completed");
        Assert.DoesNotContain(events, evt => evt.Type == "run.failed");
        Assert.Equal(approved, Directory.Exists(Path.Combine(Workspace, ".agents", "skills", "check")));
        Assert.Equal(approved ? 1 : 0, handler.RequestCount);
    }

    private static byte[] Archive(params (string Path, string Content, int Mode)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry("skills-fixture/" + file.Path);
                entry.ExternalAttributes = file.Mode << 16;
                using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                output.Write(file.Content);
            }
        return stream.ToArray();
    }
    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("codeload.github.com", request.RequestUri!.Host);
            Assert.EndsWith(SkillManagementService.DefaultRevision, request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }
    private sealed class StalledDownloadHandler : HttpMessageHandler
    {
        public StalledReadStream Body { get; } = new();
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
        }
    }
    private sealed class StalledReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(StallAsync(cancellationToken));
        private async Task<int> StallAsync(CancellationToken ct)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class StepModel(Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>, ModelUpdate> next) : IAgentModel
    {
        private int _step;
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return next(_step++, messages, tools);
        }
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
