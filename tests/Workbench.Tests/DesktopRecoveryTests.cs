using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class DesktopRecoveryTests
{
    public DesktopRecoveryTests() => Localization.SetLanguage("zh-CN");
    private static AgentEvent E(string run, string type, string? text, long sequence) => new("session", run, type, text, Sequence: sequence);

    [Fact]
    public void RecoversUnconfirmedQueuedInputsByRunAndFifoWithoutTreatingTerminalEventsAsConfirmation()
    {
        var recovered = DesktopEventPresentation.UnconfirmedInstructions([
            E("first", "input.queued", "same", 1),
            E("first", "input.queued", "same", 2),
            E("second", "input.queued", "same", 3),
            E("first", "input.steered", "same", 4),
            E("second", "input.steered", "different", 5),
            E("first", "run.cancelled", null, 6)
        ]);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(2, recovered[0].Sequence);
        Assert.Equal("second", recovered[1].RunId);
        Assert.Empty(DesktopEventPresentation.UnconfirmedInstructions([
            E("r", "input.steered", "done", 2), E("r", "input.queued", "done", 1)
        ]));
        Assert.Single(DesktopEventPresentation.UnconfirmedInstructions([
            E("r", "input.queued", "not done", 1), E("r", "run.completed", null, 2)
        ]));
    }

    [Fact]
    public void FormatsTokenUsageWithoutInventingMissingCountsOrShowingRawMalformedJson()
    {
        Assert.Equal("本次模型调用 · 输入 12,345 tokens · 输出 78 tokens",
            DesktopEventPresentation.Usage("{\"inputTokens\":12345,\"outputTokens\":78}"));
        Assert.Contains("输入 未知 tokens · 输出 78 tokens",
            DesktopEventPresentation.Usage("{\"inputTokens\":null,\"outputTokens\":78}"));
        Assert.Equal("模型用量统计暂不可用。", DesktopEventPresentation.Usage("invalid-json"));
        Assert.Equal("会话摘要 · 输入 123 tokens · 输出 8 tokens",
            DesktopEventPresentation.Usage("{\"inputTokens\":123,\"outputTokens\":8,\"purpose\":\"compaction\"}"));
    }

    [Fact]
    public void PreservesLoadedSessionOutcomeInsteadOfAlwaysShowingReady()
    {
        Assert.Equal("已完成", DesktopEventPresentation.SessionStatus("completed"));
        Assert.Equal("执行中断", DesktopEventPresentation.SessionStatus("interrupted"));
        Assert.Equal("已停止", DesktopEventPresentation.SessionStatus("cancelled"));
        Assert.Equal("执行失败", DesktopEventPresentation.SessionStatus("failed"));
        Assert.Equal("准备就绪", DesktopEventPresentation.SessionStatus("idle"));
    }
}
