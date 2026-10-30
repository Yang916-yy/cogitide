using System.Security.Cryptography;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    /// <summary>Presentation paging over the existing bounded Git inventory; no private index or cache.</summary>
    public async Task<ToolResult> BrowseFilesAsync(string workspace, string pathPrefix = "", string filter = "",
        int offset = 0, int limit = 200, string? expectedSnapshot = null, CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        if (filter.Length > 512) throw new ArgumentException("The file-name filter must be at most 512 characters.", nameof(filter));
        var prefix = NormalizePrefix(workspace, pathPrefix);
        var inventory = await InventoryAsync(workspace, prefix, ct);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendNavigationDigest(digest, new { prefix, filter, inventory.Source, inventory.Truncated, inventory.SkippedEntries });
        foreach (var path in inventory.Files)
        {
            ct.ThrowIfCancellationRequested();
            AppendNavigationDigest(digest, path);
        }
        var snapshot = Convert.ToHexStringLower(digest.GetHashAndReset());
        if (expectedSnapshot is not null && !snapshot.Equals(expectedSnapshot, StringComparison.OrdinalIgnoreCase))
            return new("File inventory or query changed between pages. Refresh the file list; no stale page was returned.", true);
        var selected = inventory.Files.Where(path => path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = selected.Skip(offset).Take(limit).ToArray();
        return BoundedPageResult(page, offset, (long)offset + page.Length < selected.Length, (files, nextOffset, responseTruncated) => new
        {
            files, pathPrefix = prefix, filter, offset, limit, nextOffset, snapshot,
            totalMatchesInInventory = selected.Length, inventory.Source, inventory.Warning,
            inventoryTruncated = inventory.Truncated, skippedEntries = inventory.SkippedEntries,
            truncated = inventory.Truncated || responseTruncated, responseTruncated
        });
    }
}
