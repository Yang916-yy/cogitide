using System.Buffers;
using System.Security.Cryptography;

namespace Workbench.Core;

/// <summary>Shared, bounded BCL hashing for evidence, speculative reads and frozen validators.</summary>
public static class EvidenceFileHash
{
    public static async Task<string?> ComputeAsync(Stream input, int maximumBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (input.CanSeek && input.Length - input.Position > maximumBytes) return null;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes - total + 1)), cancellationToken)) > 0)
            {
                total += count;
                if (total > maximumBytes) return null;
                hash.AppendData(buffer, 0, count);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
