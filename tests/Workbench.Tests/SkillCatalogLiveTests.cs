using Workbench.Core;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class SkillCatalogLiveTests
{
    [LiveSkillCatalogFact]
    public async Task PinnedPublicCatalogCanBeSearchedInstalledAndDiscoveredWithoutExecutingScripts()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "cogitide-live-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var service = new SkillManagementService(ProjectRules.ValidateSkillManifest);
            var catalog = await service.SearchAsync("pptx", ct: deadline.Token);
            var skill = Assert.Single(catalog.Results, entry => entry.Name == "pptx");
            Assert.Equal(SkillManagementService.DefaultRevision, skill.Revision);
            Assert.Equal("anthropics/skills", skill.Repository);
            var installed = await service.InstallAsync(workspace, skill.Id, ct: deadline.Token);
            Assert.True(File.Exists(Path.Combine(workspace, installed.ProvenancePath)));
            Assert.True(installed.FileCount > 1);
            var discovered = await new ProjectRules(workspace, WorkspaceService.Resolve).DiscoverSkillsAsync(deadline.Token);
            var local = Assert.Single(discovered.Skills, entry => entry.Name == "pptx");
            Assert.Equal(installed.Sha256, local.Sha256);
            Assert.False(File.Exists(Path.Combine(workspace, "output.pptx")));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    private sealed class LiveSkillCatalogFactAttribute : FactAttribute
    {
        public LiveSkillCatalogFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("COGITIDE_TEST_LIVE_SKILLS") != "1")
                Skip = "Set COGITIDE_TEST_LIVE_SKILLS=1 to download and copy a pinned public skill into an isolated generated workspace. No scripts or model calls run.";
        }
    }
}
