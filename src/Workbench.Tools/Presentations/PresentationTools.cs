using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    public IAgentTool CreatePresentationTool() => new PresentationTool(this);

    private sealed class PresentationTool(WorkspaceService service) : IAgentTool, IWorkspacePathTool
    {
        private static readonly string Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Workspace-relative output path ending in .pptx. Existing files are refused unless expectedSha256 matches." },
                title = new { type = "string", minLength = 1, maxLength = 120, description = "Presentation title for document metadata and slide footers." },
                slides = new
                {
                    type = "array", minItems = 1, maxItems = PresentationRequest.MaxSlides,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", minLength = 1, maxLength = 120 },
                            body = new { type = "array", maxItems = 6, items = new { type = "string", minLength = 1, maxLength = 160 }, description = "Up to six bullet paragraphs; combined body text at most 600 characters (320 with an image). Split dense material into more slides." },
                            notes = new { type = "string", maxLength = 4000, description = "Optional editable speaker notes; excluded from the visible slide." },
                            imagePath = new { type = "string", description = "Optional checked workspace-relative PNG or JPEG, at most 4 MiB; no network fetches." },
                            imageAspectRatio = new { type = "number", minimum = 0.1, maximum = 10, description = "Required with imagePath: original image width / height. Used to fit without cropping; dimensions are supplied by the caller." }
                        },
                        required = new[] { "title", "body" }, additionalProperties = false
                    }
                },
                aspectRatio = new { type = "string", @enum = new[] { "widescreen", "standard" }, description = "Default widescreen (16:9); standard is 4:3." },
                expectedSha256 = new { type = "string", description = "Optional current binary-file SHA256 to replace an existing .pptx. Omit or use empty for a new file. Never overwrite an unobserved file." }
            },
            required = new[] { "path", "title", "slides" }, additionalProperties = false
        });

        public ToolDefinition Definition { get; } = new("create_presentation",
            "Create an editable PowerPoint .pptx inside the chosen workspace using the Open XML SDK. Writes require approval. Supply structured titles/bullets, optional local PNG/JPEG images with their aspect ratios, and speaker notes. Generates a clean fixed theme and native text shapes; checks package validity before committing. Returns the workspace-relative file path and SHA256; open the file externally in a presentation application. Existing files require their exact current hash. A valid package is not rendered visual verification; inspect the result in a presentation viewer before claiming layout quality.", Parameters, true);

        public IReadOnlyList<string> GetPaths(ToolCall call, string workspace)
        {
            var request = Parse(call.ArgumentsJson);
            var paths = new[] { request.Path }.Concat(request.Slides.Where(slide => slide.ImagePath is not null).Select(slide => slide.ImagePath!));
            return paths.Select(path => Path.GetRelativePath(Root(workspace), Resolve(workspace, path)).Replace('\\', '/'))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            try { return await service.CreatePresentationAsync(context.WorkspacePath, Parse(call.ArgumentsJson), cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { return new(ex.Message, true); }
        }

        private static PresentationRequest Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var args = document.RootElement;
            RequireProperties(args, ["path", "title", "slides", "aspectRatio", "expectedSha256"]);
            if (!args.TryGetProperty("slides", out var slides) || slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() is < 1 or > PresentationRequest.MaxSlides)
                throw new ArgumentException($"slides must contain 1 to {PresentationRequest.MaxSlides} objects.");
            List<PresentationSlide> parsed = [];
            foreach (var slide in slides.EnumerateArray())
            {
                RequireProperties(slide, ["title", "body", "notes", "imagePath", "imageAspectRatio"]);
                if (!slide.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array || body.GetArrayLength() > 6)
                    throw new ArgumentException("Each slide body must be an array of at most six strings.");
                var paragraphs = body.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Slide body entries must be strings.")).ToArray();
                var ratio = slide.TryGetProperty("imageAspectRatio", out var ratioValue)
                    ? ratioValue.ValueKind == JsonValueKind.Number && ratioValue.TryGetDouble(out var number) ? number : throw new ArgumentException("imageAspectRatio must be a finite number.")
                    : (double?)null;
                parsed.Add(new(Required(slide, "title"), paragraphs, Optional(slide, "notes"), Optional(slide, "imagePath"), ratio));
            }
            var request = new PresentationRequest(Required(args, "path"), Required(args, "title"), parsed,
                Optional(args, "aspectRatio") ?? "widescreen", Optional(args, "expectedSha256"));
            request.Validate();
            return request;
        }

        private static void RequireProperties(JsonElement value, string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Presentation arguments and slides must be objects.");
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                    throw new ArgumentException($"Unknown or duplicate presentation property: {property.Name}");
        }

        private static string Required(JsonElement value, string name) => Optional(value, name) ?? throw new ArgumentException($"Missing string argument: {name}");
        private static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : throw new ArgumentException($"Expected string argument: {name}") : null;
    }
}

public sealed record PresentationSlide(string Title, IReadOnlyList<string> Body, string? Notes = null, string? ImagePath = null, double? ImageAspectRatio = null);

public sealed record PresentationRequest(string Path, string Title, IReadOnlyList<PresentationSlide> Slides,
    string AspectRatio = "widescreen", string? ExpectedSha256 = null)
{
    public const int MaxSlides = 50;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > 4096 || !System.IO.Path.GetExtension(Path).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must end in .pptx.");
        if (AspectRatio is not ("widescreen" or "standard")) throw new ArgumentException("aspectRatio must be widescreen or standard.");
        ValidateText(Title, 120, "Presentation title");
        if (ExpectedSha256 is { Length: > 0 } && (ExpectedSha256.Length != 64 || !ExpectedSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("expectedSha256 must be empty or a 64-character SHA256.");
        if (Slides is null || Slides.Count is < 1 or > MaxSlides) throw new ArgumentException($"A presentation needs 1 to {MaxSlides} slides.");
        var total = Title.Length;
        foreach (var slide in Slides)
        {
            if (slide is null) throw new ArgumentException("Slides must not be null.");
            ValidateText(slide.Title, 120, "Slide title");
            if (slide.Body is null || slide.Body.Count > 6) throw new ArgumentException("Each slide supports at most six bullet paragraphs.");
            foreach (var paragraph in slide.Body) ValidateText(paragraph, 160, "Bullet paragraph");
            if (slide.Body.Sum(paragraph => paragraph.Length) > (slide.ImagePath is null ? 600 : 320))
                throw new ArgumentException("Slide body is too dense. Use at most 600 characters (320 with an image), or split it into more slides.");
            if (slide.Notes is not null) ValidateText(slide.Notes, 4000, "Speaker notes", allowEmpty: true, multiline: true);
            if (slide.ImagePath is not null && (string.IsNullOrWhiteSpace(slide.ImagePath) || slide.ImagePath.Length > 4096 || slide.ImageAspectRatio is not { } ratio || !double.IsFinite(ratio) || ratio is < 0.1 or > 10))
                throw new ArgumentException("imagePath requires imageAspectRatio (original width / height) between 0.1 and 10.");
            if (slide.ImagePath is null && slide.ImageAspectRatio is not null) throw new ArgumentException("imageAspectRatio requires imagePath.");
            total += slide.Title.Length + slide.Body.Sum(paragraph => paragraph.Length) + (slide.Notes?.Length ?? 0);
        }
        if (total > 100_000) throw new ArgumentException("Presentation text exceeds the 100,000-character budget.");
    }

    private static void ValidateText(string? text, int maximum, string name, bool allowEmpty = false, bool multiline = false)
    {
        if (text is null || text.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw new ArgumentException($"{name} must contain {(allowEmpty ? 0 : 1)} to {maximum} characters.");
        try { System.Xml.XmlConvert.VerifyXmlChars(text); }
        catch (System.Xml.XmlException ex) { throw new ArgumentException($"{name} contains invalid XML text.", ex); }
        if (text.Any(character => char.IsControl(character) && (!multiline || character is not ('\r' or '\n' or '\t'))))
            throw new ArgumentException($"{name} contains an unsupported control character.");
    }
}
