using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ProtocolSerializationTests
{
    private static readonly JsonSerializerOptions PreviousOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void StableContractsKeepExactWireAndStorageJson()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.FromHours(8));
        var call = new ToolCall("call-1", "replace_text", "{\"path\":\"代码.cs\",\"newText\":\"a < b && c\"}");
        var message = new ChatMessage("assistant", "中文 ✓\n```csharp\na < b;\n```", [call]);
        var info = new SessionInfo("session-1", "项目 ✨", "D:/工作区", at, at.AddSeconds(1), "running", "codex", "thread-1");
        var result = new ToolResult("line one\r\nline two", true, "output/报告.txt");
        var evt = new AgentEvent(info.Id, "run-1", "tool.completed", "完成", call, result, "approval-1", 9007199254740993L, at);
        AssertCompatible(call);
        AssertCompatible(message);
        AssertCompatible(new ChatMessage("tool", "结果", ToolCallId: call.Id));
        AssertCompatible(new ToolDefinition("replace_text", "替换", "{\"type\":\"object\"}", true));
        AssertCompatible(result);
        AssertCompatible(info);
        AssertCompatible(new AgentSession(info, [new("user", "修改"), message, new("tool", "完成", ToolCallId: call.Id)]));
        AssertCompatible(evt);
        AssertCompatible(new ApprovalRequest("approval-1", info.Id, "run-1", call.Name, call.ArgumentsJson, "请检查"));
        AssertCompatible(new RunOptions(RunId: "run-1"));
        AssertCompatible(new ProviderSettings());
        AssertCompatible(new ModelUpdate("片段", [call], 123, 456));
        AssertCompatible(new AccountState(true, true, "test@example.invalid", "test-plan"));
        AssertCompatible(new LoginInfo("https://example.invalid/login?state=fixture", "login-1"));
        AssertCompatible(new ModelInfo("fixture-model", "模型"));
        AssertCompatible(new RpcRequest("1", "run.start", Wire.Element(new { sessionId = info.Id, prompt = "你好" })));
        AssertCompatible(new RpcReply("1", Wire.Element(info)));
        AssertCompatible(new RpcReply("2", Error: "fixture error"));
        AssertCompatible(new RpcNotification("agent.event", Wire.Element(evt)));
        AssertCompatible<IReadOnlyList<SessionInfo>>([info]);
        AssertCompatible<IReadOnlyList<AgentEvent>>([evt]);
        AssertCompatible<IReadOnlyList<ModelInfo>>([new("fixture", "Fixture")]);
        AssertCompatible(new List<SessionInfo> { info });
        AssertCompatible(new List<AgentEvent> { evt });
        AssertCompatible(new[] { info });
        AssertCompatible(new[] { evt });
    }

    [Fact]
    public void AnonymousAndObjectTypedPayloadsRetainReflectionFallback()
    {
        var payload = new { operation = "run.start", state = SampleState.Waiting, items = new[] { "中文", "<tag>" }, optional = (string?)null };
        AssertCompatible(payload);
        AssertCompatible<object>(payload);
        var options = new RunOptions(RunId: "run-1");
        AssertCompatible<object>(options);
        AssertCompatible<object>(new List<ModelInfo> { new("fixture", "Fixture") });
        AssertCompatible(new Dictionary<string, object?> { ["status"] = SampleState.Waiting, ["options"] = options, ["missing"] = null });
    }

    [Fact]
    public void HistoricalCaseInsensitiveNumericAndMissingOptionalFieldsRemainReadable()
    {
        const string optionsJson = "{\"MAXITERATIONS\":\"7\",\"TimeoutSeconds\":\"90\",\"AutoApproveWrites\":true}";
        var expected = JsonSerializer.Deserialize<RunOptions>(optionsJson, PreviousOptions);
        var actual = JsonSerializer.Deserialize<RunOptions>(optionsJson, Wire.Json);
        Assert.Equal(expected, actual);
        Assert.Equal(100000, actual!.MaxContextCharacters);
        Assert.Null(actual.RunId);

        const string eventJson = "{\"SESSIONID\":\"s\",\"RUNID\":\"r\",\"TYPE\":\"message.delta\",\"Sequence\":\"42\"}";
        Assert.Equal(JsonSerializer.Deserialize<AgentEvent>(eventJson, PreviousOptions), JsonSerializer.Deserialize<AgentEvent>(eventJson, Wire.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunOptions>("{\"maxIterations\":{}}", Wire.Json));
    }

    private static void AssertCompatible<T>(T value)
    {
        var expected = JsonSerializer.Serialize(value, PreviousOptions);
        Assert.Equal(expected, JsonSerializer.Serialize(value, Wire.Json));
        Assert.Equal(expected, Wire.Element(value).GetRawText());
        var roundTrip = JsonSerializer.Deserialize<T>(expected, Wire.Json);
        Assert.Equal(expected, JsonSerializer.Serialize(roundTrip, Wire.Json));
    }

    private enum SampleState { Waiting, Completed }
}
