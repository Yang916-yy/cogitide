namespace Workbench.Protocol;

/// <summary>Metadata from the shared project skill parser, without loading a skill body into model context.</summary>
public sealed record SkillManifestInfo(string Name, string Description, bool DisableModelInvocation);

/// <summary>A pinned upstream reference, not a safety, compatibility or vendor-verification assertion.</summary>
public sealed record SkillCatalogEntry(string Id, string Name, string Description, string Repository, string Revision,
    string Path, string SourceUrl, string? LicensePath, string SourceLabel);
public sealed record SkillCatalogSearchResult(IReadOnlyList<SkillCatalogEntry> Results, string Source, string Revision,
    bool Truncated, string Warning, int? NextOffset = null, bool CatalogIncomplete = false);
public sealed record SkillInstallResult(string Name, string Path, string Sha256, int FileCount, long TotalBytes,
    string Source, string? Revision, string? SourcePath, string ProvenancePath);
