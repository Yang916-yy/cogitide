using System.Runtime.CompilerServices;
using Workbench.Protocol;

namespace Workbench.Core;

internal sealed class ReportedTokenLimitException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Counts provider-reported input/output once per call, including failed calls and compaction.</summary>
internal sealed class RunUsageMeter(IAgentModel model, long? limit, Func<RunUsage, Task> record) : IAgentModel
{
    private RunUsage _usage = new();

    public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long? input = null, output = null;
        var completed = false;
        try
        {
            await foreach (var update in model.StreamAsync(messages, tools, cancellationToken))
            {
                input = Observe(input, update.InputTokens);
                output = Observe(output, update.OutputTokens);
                if (limit is { } maximum && checked(_usage.ReportedTokens + (input ?? 0) + (output ?? 0)) >= maximum)
                    throw new ReportedTokenLimitException("reported_token_limit",
                        "The provider-reported token limit was reached. Partial model state was discarded and no pending action was executed. Explicitly continue with a fresh run budget after review.");
                yield return update;
            }
            completed = true;
            if (limit is not null && (input is null || output is null))
                throw new ReportedTokenLimitException("token_usage_unavailable",
                    "The provider did not report complete token usage, so the configured reported-token limit cannot be checked. The task is paused; review usage and explicitly choose the next run budget.");
        }
        finally
        {
            _usage = new(checked(_usage.InputTokens + (input ?? 0)), checked(_usage.OutputTokens + (output ?? 0)),
                checked(_usage.ModelCalls + 1), checked(_usage.CallsWithUnknownUsage + (input is null || output is null ? 1 : 0)),
                checked(_usage.IncompleteCalls + (completed ? 0 : 1)));
            await record(_usage);
        }
    }

    private static long? Observe(long? previous, long? value)
    {
        if (value is null) return previous;
        if (value < 0) throw new InvalidDataException("The model reported negative token usage.");
        return Math.Max(previous ?? 0, value.Value);
    }
}
