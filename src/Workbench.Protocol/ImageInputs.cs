namespace Workbench.Protocol;

/// <summary>Shared request limits; provider SDKs remain responsible for their own wire image format.</summary>
public static class ImageInputs
{
    public const int MaxCount = 4;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxTotalBytes = 8 * 1024 * 1024;
    public const int MaxRequestCharacters = 16 * 1024 * 1024;

    public static void Validate(IReadOnlyList<ImageAttachment>? images)
    {
        if (images is null) return;
        if (images.Count > MaxCount) throw new ArgumentException($"A message supports at most {MaxCount} images.");
        long total = 0;
        foreach (var image in images)
        {
            if (image is null || image.Base64Data is null || image.Base64Data.Length > 4 * ((MaxImageBytes + 2) / 3))
                throw new ArgumentException("An image exceeds the 4 MiB limit.");
            if (image.Name?.Length > 256 || image.Detail is not ("auto" or "low" or "high"))
                throw new ArgumentException("Invalid image name or detail setting.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(image.Base64Data); }
            catch (FormatException) { throw new ArgumentException("Image data must be valid base64."); }
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !MatchesMediaType(bytes, image.MediaType))
                throw new ArgumentException("Image data must match its PNG, JPEG, GIF or WebP media type and size limit.");
            total += bytes.Length;
        }
        if (total > MaxTotalBytes) throw new ArgumentException("The combined image size exceeds 8 MiB.");
    }

    public static string? DetectMediaType(ReadOnlySpan<byte> bytes) =>
        MatchesMediaType(bytes, "image/png") ? "image/png" :
        MatchesMediaType(bytes, "image/jpeg") ? "image/jpeg" :
        MatchesMediaType(bytes, "image/gif") ? "image/gif" :
        MatchesMediaType(bytes, "image/webp") ? "image/webp" : null;

    private static bool MatchesMediaType(ReadOnlySpan<byte> bytes, string? type) => type switch
    {
        "image/png" => bytes.StartsWith<byte>([137, 80, 78, 71, 13, 10, 26, 10]),
        "image/jpeg" => bytes.StartsWith<byte>([255, 216, 255]),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };
}
