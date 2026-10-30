using System.Diagnostics;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class ProjectRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-rules-" + Guid.NewGuid().ToString("N"));
    public ProjectRulesTests() => Directory.CreateDirectory(_root);
    private ProjectRules Rules() => new(_root, WorkspaceService.Resolve);
    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task RulesWalkRootToTargetWithoutLeakingSiblingScopeAndReloadChanges()
    {
        Write("AGENTS.md", "Preserve public APIs.");
        Write("src/AGENTS.md", "Use async in src.");
        Write("src/ui/AGENTS.md", "UI rules.");
        Write("tests/AGENTS.md", "Tests only.");
        var rules = Rules();
        var first = await rules.LoadAsync("src/ui/new.cs");
        Assert.Equal(new[] { "AGENTS.md", "src/AGENTS.md", "src/ui/AGENTS.md" }, first.Select(x => x.Path));
        Assert.Equal(new[] { ".", "src", "src/ui" }, first.Select(x => x.Scope));
        Assert.DoesNotContain(first, x => x.Content.Contains("Tests only"));
        Write("src/AGENTS.md", "Updated async rule.");
        File.Delete(Path.Combine(_root, "src/ui/AGENTS.md"));
        var second = await rules.LoadAsync("src/ui/new.cs");
        Assert.Equal(2, second.Count);
        Assert.NotEqual(first[1].Sha256, second[1].Sha256);
        Assert.Contains("subordinate", ProjectRules.Format(second));
        Assert.Single(await rules.LoadAsync());
        Assert.Single(await rules.LoadAsync("."));
        Assert.Equal(WorkspaceService.Resolve(_root, "", true), WorkspaceService.Resolve(_root, ".", true));
    }

    [Fact]
    public async Task ExplicitDirectoryAndNewFilePathsUseTheirApplicableAncestors()
    {
        Write("AGENTS.md", "Root");
        Write("future/AGENTS.md", "Future");
        Assert.Equal(2, (await Rules().LoadAsync("future", directory: true)).Count);
        Assert.Equal(2, (await Rules().LoadAsync("future/not-created/a.cs")).Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Rules().LoadAsync("../outside.cs"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Rules().LoadAsync(".git/config"));
    }

    [Fact]
    public async Task OversizedRulesFailClearlyRatherThanSilentlyRemovingInstructions()
    {
        Write("AGENTS.md", new string('a', 32769));
        await Assert.ThrowsAsync<InvalidDataException>(() => Rules().LoadAsync());
        Write("AGENTS.md", new string('a', 32768));
        Write("src/AGENTS.md", new string('b', 32768));
        Write("src/deep/AGENTS.md", "x");
        await Assert.ThrowsAsync<InvalidDataException>(() => Rules().LoadAsync("src/deep/file.cs"));
    }

    [Fact]
    public async Task PiSkillDiscoveryStopsAtSkillRootAndUsesRealYamlFrontmatter()
    {
        Write(".agents/skills/build/SKILL.md", "---\nname: build\ndescription: >-\n  Build and check\n  the local project.\n---\nRun the documented checks.");
        Write(".agents/skills/build/nested/SKILL.md", "---\nname: nested\ndescription: Must not discover this.\n---\nx");
        Write(".agents/skills/.hidden/SKILL.md", "---\nname: hidden\ndescription: Hidden.\n---\nx");
        Write(".agents/skills/node_modules/dep/SKILL.md", "---\nname: dependency\ndescription: Dependency.\n---\nx");
        Write(".pi/skills/root.md", "---\nname: root-file\ndescription: Direct root markdown.\n---\nx");
        var catalog = await Rules().DiscoverSkillsAsync();
        Assert.Equal(new[] { "build", "root-file" }, catalog.Skills.Select(x => x.Name));
        Assert.Equal("Build and check the local project.", catalog.Skills[0].Description);
        Assert.Equal(".agents/skills/build", catalog.Skills[0].BaseDirectory);
        Assert.Contains("read_skill", ProjectRules.FormatSkills(catalog));
        Assert.False(catalog.Truncated);
    }

    [Fact]
    public async Task DuplicateAndInvalidSkillsHaveDeterministicDiagnostics()
    {
        Write(".agents/skills/build/SKILL.md", "---\nname: build\ndescription: First.\n---\nx");
        Write(".cogito/skills/build/SKILL.md", "---\nname: build\ndescription: Second.\n---\nx");
        Write(".pi/skills/bad/SKILL.md", "---\nname: Invalid_Name\ndescription: Wrong.\n---\nx");
        Write(".opencode/skills/empty/SKILL.md", "---\nname: empty\n---\nx");
        var catalog = await Rules().DiscoverSkillsAsync();
        Assert.Single(catalog.Skills);
        Assert.Equal("First.", catalog.Skills[0].Description);
        Assert.Equal(3, catalog.Diagnostics.Count);
    }

    [Fact]
    public async Task DisabledSkillRequiresExplicitUserSelectionAndReadReloadsVersion()
    {
        const string path = ".agents/skills/manual/SKILL.md";
        Write(path, "---\nname: manual\ndescription: Manual only.\ndisable-model-invocation: true\n---\nReference only.");
        var rules = Rules();
        var catalog = await rules.DiscoverSkillsAsync();
        Assert.Empty(ProjectRules.FormatSkills(catalog));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rules.ReadSkillAsync("manual"));
        var first = await rules.ReadSkillAsync("manual", explicitlyRequested: true);
        Write(path, "---\nname: manual\ndescription: Manual only.\ndisable-model-invocation: true\n---\nChanged reference.");
        var second = await rules.ReadSkillAsync("manual", explicitlyRequested: true);
        Assert.NotEqual(first.Skill.Sha256, second.Skill.Sha256);
        Assert.Equal(new[] { "manual" }, ProjectRules.ExplicitSkills("Please use /skill:manual now."));
        Assert.Empty(ProjectRules.ExplicitSkills("https://example.invalid/skill:manual"));
    }

    [Fact]
    public async Task ReadSkillToolDoesNotExecuteScriptsAndRejectsScopeOrArgumentOverrides()
    {
        Write(".agents/skills/check/SKILL.md", "---\nname: check\ndescription: Test checks.\n---\nRun scripts/check.ps1 when authorized.");
        var tool = new ProjectSkillTool(Rules());
        var result = await tool.ExecuteAsync(new("a", "read_skill", "{\"name\":\"check\"}"), new(_root));
        Assert.False(result.IsError);
        Assert.Contains("No script was executed", result.Content);
        Assert.Contains("scripts/check.ps1", result.Content);
        Assert.True((await tool.ExecuteAsync(new("b", "read_skill", "{\"name\":\"check\",\"explicit\":true}"), new(_root))).IsError);
        Assert.True((await tool.ExecuteAsync(new("c", "read_skill", "{\"name\":\"check\"}"), new(Path.GetTempPath()))).IsError);
    }

    [Fact]
    public async Task SymbolicRulesAndSkillsCannotEscapeExistingWorkspaceBoundary()
    {
        var outside = Path.Combine(Path.GetTempPath(), "cogito-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "Outside");
            await CreateDirectoryLinkAsync(Path.Combine(_root, "linked"), outside);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Rules().LoadAsync("linked/file.cs"));
            Directory.CreateDirectory(Path.Combine(_root, ".agents/skills"));
            await CreateDirectoryLinkAsync(Path.Combine(_root, ".agents/skills/outside"), outside);
            var catalog = await Rules().DiscoverSkillsAsync();
            Assert.Empty(catalog.Skills);
            Assert.Contains(catalog.Diagnostics, message => message.Contains("not accessible"));
        }
        finally { Directory.Delete(outside, true); }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // Junctions exercise the same existing reparse-point guard without requiring Windows symlink privileges.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["COGITO_TEST_LINK"] = link;
        start.Environment["COGITO_TEST_TARGET"] = target;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path $env:COGITO_TEST_LINK -Target $env:COGITO_TEST_TARGET -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
