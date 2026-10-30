namespace Workbench.Protocol;

/// <summary>A replaceable projection of an unchanged conversation prefix; original messages remain the source of truth.</summary>
public sealed record CompactionCheckpoint(int Version, int CoveredMessageCount, string SourceSha256, string Summary,
    DateTimeOffset CreatedAt, long? InputTokens = null, long? OutputTokens = null);
