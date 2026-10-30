using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ContextWindowTests
{
    [Fact]
    public void FifthImageTrimsOldestCompleteTurnAndPreservesSignedToolGroups()
    {
        List<ChatMessage> history = [];
        for (var index = 0; index < 4; index++) history.AddRange(Group(index, Image(8)));
        var current = new ChatMessage("user", "Current task instructions", Images: [Image(8)]);
        history.Add(current);
        var result = ContextWindow.Create("System", history, 100_000, history.Count - 1);
        Assert.True(result.Trimmed);
        Assert.Equal(4, result.Messages.Sum(message => message.Images?.Count ?? 0));
        Assert.DoesNotContain(result.Messages, message => message.Content == "User 0");
        Assert.Contains(current, result.Messages);
        Assert.Equal(["call_1", "call_2", "call_3"], result.Messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id));
        Assert.Equal(["call_1", "call_2", "call_3"], result.Messages.Where(message => message.Role == "tool").Select(message => message.ToolCallId));
        foreach (var assistant in result.Messages.Where(message => message.Continuation is not null))
        {
            var original = Assert.Single(history, message => message.ToolCalls?[0].Id == assistant.ToolCalls![0].Id);
            Assert.True(JsonElement.DeepEquals(original.Continuation!.Payload, assistant.Continuation!.Payload));
        }
        Assert.Equal(5, history.Sum(message => message.Images?.Count ?? 0)); // The journal stays intact.
    }

    [Fact]
    public void ImageBytesUseDecodedSizeAndHonorTheExactAggregateBoundary()
    {
        var large = Image(ImageInputs.MaxImageBytes);
        var current = new ChatMessage("user", "Current", Images: [large]);
        var exact = ContextWindow.Create("System", [.. Group(0, large), current], 100_000);
        Assert.False(exact.Trimmed);
        Assert.Equal(2, exact.Messages.Sum(message => message.Images?.Count ?? 0));
        var over = ContextWindow.Create("System", [.. Group(0, Image(8)), .. Group(1, large), current], 100_000);
        Assert.True(over.Trimmed);
        Assert.Equal(2, over.Messages.Sum(message => message.Images?.Count ?? 0));
        Assert.DoesNotContain(over.Messages, message => message.Content == "User 0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedActiveTaskNeverLosesItsInitialInstructionsOrImages(bool bytes)
    {
        var image = Image(bytes ? ImageInputs.MaxImageBytes : 8);
        var messages = Enumerable.Range(0, bytes ? 3 : 5)
            .Select(index => new ChatMessage("user", $"Active instruction {index}", Images: [image])).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => ContextWindow.Create("System", messages, 100_000, activeStart: 0));
        Assert.Contains("active task", error.Message);
        Assert.Contains("image context budget", error.Message);
        Assert.Equal("Active instruction 0", messages[0].Content);
        Assert.All(messages, message => Assert.Single(message.Images!));
    }

    [Fact]
    public void SignedStateCountsTowardBudgetAndIsNeverTruncated()
    {
        var active = Group(1, null, thinkingLength: 6000).ToArray();
        Assert.Throws<InvalidOperationException>(() => ContextWindow.Create("System", active, 4000, activeStart: 0));
        var current = new ChatMessage("user", "Current concise task");
        var snapshot = ContextWindow.Create("System", [.. active, current], 4000);
        Assert.True(snapshot.Trimmed);
        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Equal(current, snapshot.Messages[^1]);
        Assert.Equal(6000, active[1].Continuation!.Payload[0].GetProperty("thinking").GetString()!.Length);
    }

    private static IEnumerable<ChatMessage> Group(int index, ImageAttachment? image, int thinkingLength = 8)
    {
        var call = new ToolCall($"call_{index}", "read", "{}");
        var state = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "thinking", thinking = new string('t', thinkingLength), signature = "opaque-signed-fixture" },
            new { type = "tool_use", id = call.Id, name = call.Name, input = new { } }
        });
        yield return new("user", $"User {index}", Images: image is null ? null : [image]);
        yield return new("assistant", "", [call], Continuation: new(AnthropicModel.ContinuationFormat, "https://api.anthropic.com", "fixture", state));
        yield return new("tool", "Read result", ToolCallId: call.Id);
        yield return new("assistant", $"Result {index}");
    }

    private static ImageAttachment Image(int length)
    {
        var bytes = new byte[length];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        return new("image/png", Convert.ToBase64String(bytes));
    }
}
