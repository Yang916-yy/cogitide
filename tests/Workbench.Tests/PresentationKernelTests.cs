using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class PresentationKernelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-presentation-kernel-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PresentationLoadsDestinationRulesAndHonorsApproval(bool approved)
    {
        Directory.CreateDirectory(Path.Combine(_root, "slides"));
        File.WriteAllText(Path.Combine(_root, "slides", "AGENTS.md"), "PRESENTATION_POLICY: keep presentation sources editable.");
        using var store = new SqliteSessionStore(Path.Combine(_root, "state.db"));
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(new("deck", "Deck fixture", _root, now, now), []);
        await store.SaveAsync(session);
        var destination = Path.Combine(_root, "slides", "deck.pptx");
        var approvals = 0;
        var steps = 0;
        var arguments = JsonSerializer.Serialize(new
        {
            path = "slides/deck.pptx", title = "Kernel fixture",
            slides = new[] { new { title = "Editable content", body = new[] { "Generated through the shared kernel." } } }
        });
        var model = new InspectingModel((messages, tools) =>
        {
            Assert.Contains(tools, tool => tool.Name == "create_presentation" && tool.RequiresApproval);
            steps++;
            if (steps == 2)
            {
                Assert.Contains("PRESENTATION_POLICY", messages[0].Content);
                Assert.Contains("new or changed", messages.Last().Content);
                Assert.False(File.Exists(destination));
                Assert.Equal(0, approvals);
            }
            return steps <= 2
                ? new(ToolCalls: [new("deck-" + steps, "create_presentation", arguments)])
                : new(TextDelta: "Presentation action observed.");
        });
        await new AgentEngine(store, WorkspaceTools.Create(new()), WorkspaceService.Resolve).RunAsync(
            session.Info.Id, "Create an editable slide deck in slides/deck.pptx.", model, new(),
            (_, _) => ValueTask.CompletedTask, (_, _) => { approvals++; return Task.FromResult(approved); });
        Assert.Equal(1, approvals);
        Assert.Equal(approved, File.Exists(destination));
        var results = (await store.EventsAsync(session.Info.Id)).Where(evt => evt.Type == "tool.completed").ToArray();
        Assert.Equal(2, results.Length);
        Assert.True(results[0].Result!.IsError);
        Assert.Equal(!approved, results[1].Result!.IsError);
    }

    [Fact]
    public async Task ReadOnlyModelCannotSelectPresentationWriter()
    {
        Directory.CreateDirectory(_root);
        using var store = new SqliteSessionStore(Path.Combine(_root, "state.db"));
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new("readonly", "Read only", _root, now, now), []));
        var model = new InspectingModel((_, tools) =>
        {
            Assert.DoesNotContain(tools, tool => tool.Name == "create_presentation");
            return new(TextDelta: "Read-only inspection complete.");
        });
        await new AgentEngine(store, WorkspaceTools.Create(new()), WorkspaceService.Resolve).RunAsync(
            "readonly", "Inspect only.", model, new(ExecutionMode: ExecutionMode.ReadOnly),
            (_, _) => ValueTask.CompletedTask, (_, _) => throw new InvalidOperationException("Read-only action requested approval."));
        Assert.Empty(Directory.GetFiles(_root, "*.pptx"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InspectingModel(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>, ModelUpdate> inspect) : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return inspect(messages, tools);
        }
    }
}
