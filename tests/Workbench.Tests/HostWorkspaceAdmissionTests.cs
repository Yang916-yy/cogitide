using System.Reflection;
using Microsoft.Data.Sqlite;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class HostWorkspaceAdmissionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogitide-workspace-admission-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("run.start")]
    [InlineData("workspace.command")]
    public async Task HeldReviewMutationRejectsConcurrentWorkEvenThroughAnotherWorkspace(string method)
    {
        var reviewWorkspace = Path.Combine(_directory, "review");
        var otherWorkspace = Path.Combine(_directory, "other");
        Directory.CreateDirectory(reviewWorkspace);
        Directory.CreateDirectory(otherWorkspace);
        await using var host = new WorkbenchHost(Path.Combine(_directory, "state"));
        var session = (AgentSession)await host.DispatchAsync("session.create", Wire.Element(new { workspacePath = otherWorkspace }));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = Review(host, reviewWorkspace, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var request = method == "run.start"
                ? Wire.Element(new
                {
                    sessionId = session.Info.Id, prompt = "Do not overlap the review mutation.",
                    provider = new ProviderSettings("https://fixture.invalid/v1", "fixture", "COGITIDE_ABSENT_" + Guid.NewGuid().ToString("N"))
                })
                : Wire.Element(new { workspacePath = otherWorkspace, command = "echo review-overlap" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => host.DispatchAsync(method, request)));
            Assert.Contains("Git review", error.Message);
        }
        finally
        {
            release.TrySetResult();
            Assert.True(await review.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        // Rejected run admission must not leave a session/workspace lease behind.
        Assert.True(await Review(host, reviewWorkspace, _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task ReviewMutationFailureReleasesOwnershipForAnotherMutation()
    {
        var workspace = Path.Combine(_directory, "review");
        Directory.CreateDirectory(workspace);
        await using var host = new WorkbenchHost(Path.Combine(_directory, "state"));
        await Assert.ThrowsAsync<IOException>(() => Review(host, workspace, _ => Task.FromException<bool>(new IOException("fixture failure"))));
        Assert.True(await Review(host, workspace, _ => Task.FromResult(true)));
    }

    private static Task<bool> Review(WorkbenchHost host, string workspace, Func<string, Task<bool>> mutation) =>
        (Task<bool>)typeof(WorkbenchHost).GetMethod("WithReviewMutationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(bool)).Invoke(host, [workspace, mutation])!;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
