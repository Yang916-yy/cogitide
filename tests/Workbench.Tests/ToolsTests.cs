using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class ToolsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-tools-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public ToolsTests() => Directory.CreateDirectory(_root);
    public Task InitializeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(".git/config")]
    public void RejectsTraversalAndGitInternals(string path) => Assert.Throws<UnauthorizedAccessException>(() => WorkspaceService.Resolve(_root, path));

    [Fact]
    public void WindowsAliasesCannotBypassGitProtection()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceService.Resolve(_root, ".GIT/config"));
        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceService.Resolve(_root, ".git./config"));
        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceService.Resolve(_root, "test.txt:secret"));
    }

    [Fact]
    public async Task StaleEditDoesNotOverwriteUserChanges()
    {
        var path = Path.Combine(_root, "code.cs");
        await File.WriteAllTextAsync(path, "return 1;");
        using var first = JsonDocument.Parse((await _service.ReadFileAsync(_root, "code.cs")).Content);
        var hash = first.RootElement.GetProperty("sha256").GetString()!;
        await File.WriteAllTextAsync(path, "return 42; // user's edit");
        var result = await _service.ReplaceTextAsync(_root, "code.cs", "return 1;", "return 2;", hash);
        Assert.True(result.IsError);
        Assert.Equal("return 42; // user's edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UniqueReplacementPreservesBomAndUnrelatedContent()
    {
        var path = Path.Combine(_root, "中文.cs");
        await File.WriteAllTextAsync(path, "// 保留\r\nreturn 1;\r\n", new UTF8Encoding(true));
        using var first = JsonDocument.Parse((await _service.ReadFileAsync(_root, "中文.cs")).Content);
        var result = await _service.ReplaceTextAsync(_root, "中文.cs", "return 1;", "return 2;", first.RootElement.GetProperty("sha256").GetString()!);
        Assert.False(result.IsError);
        Assert.Equal("// 保留\r\nreturn 2;\r\n", await File.ReadAllTextAsync(path));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, (await File.ReadAllBytesAsync(path))[..3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingExistingUnixFilePreservesExecutableAndRestrictedMode(bool replaceText)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "verify.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\nprintf 'before'\n");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(path, mode);
        using var read = JsonDocument.Parse((await _service.ReadFileAsync(_root, "verify.sh")).Content);
        var hash = read.RootElement.GetProperty("sha256").GetString()!;
        var result = replaceText
            ? await _service.ReplaceTextAsync(_root, "verify.sh", "before", "after", hash)
            : await _service.WriteFileAsync(_root, "verify.sh", "#!/bin/sh\nprintf 'after'\n", hash);
        Assert.False(result.IsError);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Contains("after", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AmbiguousReplaceAndMissingHashNeverWrite()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "same same");
        using var data = JsonDocument.Parse((await _service.ReadFileAsync(_root, "a.txt")).Content);
        Assert.True((await _service.ReplaceTextAsync(_root, "a.txt", "same", "other", data.RootElement.GetProperty("sha256").GetString()!)).IsError);
        Assert.True((await _service.WriteFileAsync(_root, "a.txt", "overwrite", null)).IsError);
        Assert.Equal("same same", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task CreatesNestedFileAndSearchesUnicode()
    {
        Assert.False((await _service.WriteFileAsync(_root, "src/hello.txt", "hello\n中文工作台", "")).IsError);
        var search = await _service.SearchAsync(_root, "工作");
        Assert.Contains("src/hello.txt", search.Content);
        using var data = JsonDocument.Parse(search.Content);
        Assert.Equal(2, data.RootElement.GetProperty("matches")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task ListSkipsDependencyAndGitDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("init"); start.ArgumentList.Add("-q");
        using var git = Process.Start(start)!;
        await git.WaitForExitAsync();
        Assert.Equal(0, git.ExitCode);
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "node_modules/\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "large.js"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "main.cs"), "x");
        Assert.Equal(new[] { ".gitignore", "main.cs" }, await _service.ListFilesAsync(_root));
    }

    [Fact]
    public async Task BinaryAndOversizedFilesAreRejected()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.bin"), [0, 1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "large.txt"), new byte[WorkspaceService.MaxFileBytes + 1]);
        await Assert.ThrowsAsync<IOException>(() => _service.ReadFileAsync(_root, "binary.bin"));
        await Assert.ThrowsAsync<IOException>(() => _service.ReadFileAsync(_root, "large.txt"));
    }

    [Fact]
    public async Task CapturesExitCodeAndStreamsOutput()
    {
        var seen = new StringBuilder();
        var command = OperatingSystem.IsWindows() ? "Write-Output 'test output'; exit 7" : "printf 'test output'; exit 7";
        var result = await _service.RunCommandAsync(_root, command, onOutput: (chunk, _) => { seen.Append(chunk); return ValueTask.CompletedTask; });
        Assert.True(result.IsError);
        using var data = JsonDocument.Parse(result.Content);
        Assert.Equal(7, data.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("test output", seen.ToString());
    }

    [Fact]
    public async Task UnicodeCommandOutputAndErrorsRemainUtf8()
    {
        var streamed = new StringBuilder();
        var command = OperatingSystem.IsWindows()
            ? "Write-Output '中文测试'; [Console]::Error.WriteLine('错误信息')"
            : "printf '%s\\n' '中文测试'; printf '%s\\n' '错误信息' >&2";
        var result = await _service.RunCommandAsync(_root, command, onOutput: (chunk, _) => { streamed.Append(chunk); return ValueTask.CompletedTask; });
        Assert.False(result.IsError);
        using var value = JsonDocument.Parse(result.Content);
        Assert.Equal(command, value.RootElement.GetProperty("command").GetString());
        var recorded = value.RootElement.GetProperty("output").GetString()!;
        Assert.Contains("中文测试", recorded);
        Assert.Contains("错误信息", recorded);
        Assert.Contains("中文测试", streamed.ToString());
        Assert.Contains("错误信息", streamed.ToString());
        Assert.DoesNotContain('\uFFFD', recorded);
    }

    [Fact]
    public async Task TimeoutEndsCommandAndIsReported()
    {
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var watch = Stopwatch.StartNew();
        var result = await _service.RunCommandAsync(_root, command, 1);
        using var data = JsonDocument.Parse(result.Content);
        Assert.True(data.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task CancellationEndsCommand()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RunCommandAsync(_root, OperatingSystem.IsWindows() ? "Start-Sleep 30" : "sleep 30", ct: cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CancellationEndsCommandAndItsChildProcesses(int attempt)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<(int Root, int Child)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var command = OperatingSystem.IsWindows()
            ? "$fixtureChild = Start-Process -FilePath powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; Write-Output ('ROOT=' + $PID + ';CHILD=' + $fixtureChild.Id); Wait-Process -Id $fixtureChild.Id"
            : "sleep 60 & fixture_child=$!; printf 'ROOT=%s;CHILD=%s\\n' \"$$\" \"$fixture_child\"; wait \"$fixture_child\"";
        var execution = _service.RunCommandAsync(_root, command, onOutput: (chunk, _) =>
        {
            output.Append(chunk);
            var match = Regex.Match(output.ToString(), @"ROOT=(\d+);CHILD=(\d+)");
            if (match.Success) started.TrySetResult((int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)));
            return ValueTask.CompletedTask;
        }, ct: cancellation.Token);
        Process? root = null, child = null;
        try
        {
            var ids = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            root = Process.GetProcessById(ids.Root);
            child = Process.GetProcessById(ids.Child);
            Assert.False(root.HasExited, $"Fixture shell exited early in attempt {attempt}.");
            Assert.False(child.HasExited, $"Fixture child exited early in attempt {attempt}.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.WhenAll(root.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(root.HasExited, $"Shell survived cancellation in attempt {attempt}.");
            Assert.True(child.HasExited, $"Child survived cancellation in attempt {attempt}.");
        }
        finally
        {
            cancellation.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
            // Clean up only the two processes launched by this fixture if an assertion exposed a regression.
            foreach (var process in new[] { child, root })
            {
                if (process is null) continue;
                try
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                finally { process.Dispose(); }
            }
        }
    }

    [Fact]
    public async Task GitPreviewDoesNotExecuteRepositoryTextConversion()
    {
        async Task Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            await output;
        }
        await Git("init", "-q");
        await Git("config", "diff.fixture.textconv", "echo TEXTCONV_EXECUTED");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitattributes"), "*.txt diff=fixture\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "sample.txt"), "before\n");
        await Git("add", ".");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture");
        await File.WriteAllTextAsync(Path.Combine(_root, "sample.txt"), "after\n");
        var result = await _service.GitDiffAsync(_root);
        Assert.False(result.IsError);
        Assert.Contains("-before", result.Content);
        Assert.Contains("+after", result.Content);
        Assert.DoesNotContain("TEXTCONV_EXECUTED", result.Content);
    }

    public async Task DisposeAsync()
    {
        // Only the exact test-owned temporary directory may be removed. Git creates read-only object files.
        var resolvedRoot = Path.GetFullPath(_root);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolvedRoot.StartsWith(tempRoot, comparison) || !Path.GetFileName(resolvedRoot).StartsWith("workbench-tools-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean a directory outside the test's temporary root.");
        if (!Directory.Exists(resolvedRoot)) return;
        var traversal = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(resolvedRoot, "*", traversal))
        {
            var resolvedPath = Path.GetFullPath(path);
            if (!resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException("Refusing to change attributes outside the test directory.");
            var attributes = File.GetAttributes(resolvedPath);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(resolvedPath, attributes & ~FileAttributes.ReadOnly);
        }
        var cleanup = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(2);
        var delayMilliseconds = 20;
        while (true)
        {
            try
            {
                Directory.Delete(resolvedRoot, true);
                return;
            }
            catch (IOException error) when (OperatingSystem.IsWindows() &&
                (error.HResult & 0xffff) is 32 or 33 && cleanup.Elapsed < budget)
            {
                // Cancellation assertions already observe both owned processes exiting. Windows
                // can release their directory handles later; retry only sharing/lock violations.
                // A persistent handle leak still fails with its original filesystem error.
                var remaining = budget - cleanup.Elapsed;
                if (remaining <= TimeSpan.Zero) throw;
                var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
                await Task.Delay(delay < remaining ? delay : remaining);
                delayMilliseconds = Math.Min(200, delayMilliseconds * 2);
            }
        }
    }
}
