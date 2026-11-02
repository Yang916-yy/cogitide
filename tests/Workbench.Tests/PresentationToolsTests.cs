using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Workbench.Tests;

public sealed class PresentationToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-presentation-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    private static readonly byte[] SquarePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aU1cAAAAASUVORK5CYII=");

    public PresentationToolsTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("widescreen", 12192000)]
    [InlineData("standard", 9144000)]
    public async Task ToolCreatesValidatedEditableTextImageAndSpeakerNotes(string aspectRatio, int width)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "图像.png"), SquarePng);
        var tool = WorkspaceTools.Create(_service).Single(tool => tool.Definition.Name == "create_presentation");
        Assert.True(tool.Definition.RequiresApproval);
        var arguments = JsonSerializer.Serialize(new
        {
            path = "exports/演示.pptx", title = "Editable deck 可编辑", aspectRatio,
            slides = new[]
            {
                new { title = "A clear title 标题", body = new[] { "Native bullet text", "Evidence before claims" }, notes = "Speaker notes 第一行\nSecond line", imagePath = "图像.png", imageAspectRatio = 1.0 },
                new { title = "Second slide", body = Array.Empty<string>(), notes = "", imagePath = "图像.png", imageAspectRatio = 1.0 }
            }
        });
        var call = new ToolCall("presentation", tool.Definition.Name, arguments);
        Assert.Equal(new[] { "exports/演示.pptx", "图像.png" }, Assert.IsAssignableFrom<IWorkspacePathTool>(tool).GetPaths(call, _root));
        var result = await tool.ExecuteAsync(call, new(_root));
        Assert.False(result.IsError, result.Content);
        var full = Path.Combine(_root, "exports", "演示.pptx");
        Assert.Equal(full, result.ArtifactPath);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.GetProperty("packageValidated").GetBoolean());
        Assert.Equal("not_rendered", json.RootElement.GetProperty("visualVerification").GetString());
        Assert.Equal(Hash(await File.ReadAllBytesAsync(full)), json.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(Hash(SquarePng), json.RootElement.GetProperty("images")[0].GetProperty("sha256").GetString());
        using var document = PresentationDocument.Open(full, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2007).Validate(document).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error => error.Description + " " + error.Path?.XPath)));
        Assert.Equal("Editable deck 可编辑", document.PackageProperties.Title);
        var presentation = document.PresentationPart!;
        var presentationRoot = Assert.IsType<P.Presentation>(presentation.Presentation);
        Assert.Equal(width, presentationRoot.SlideSize!.Cx!.Value);
        Assert.Equal(6858000, presentationRoot.SlideSize.Cy!.Value);
        var slides = presentationRoot.SlideIdList!.Elements<P.SlideId>()
            .Select(id => (SlidePart)presentation.GetPartById(id.RelationshipId!)).ToArray();
        Assert.Equal(2, slides.Length);
        var firstSlide = Assert.IsType<P.Slide>(slides[0].Slide);
        var text = firstSlide.Descendants<A.Text>().Select(node => node.Text).ToArray();
        Assert.Contains("A clear title 标题", text);
        Assert.Contains("Native bullet text", text);
        Assert.DoesNotContain(text, value => value.Contains("Speaker notes", StringComparison.Ordinal));
        var notes = Assert.IsType<P.NotesSlide>(slides[0].NotesSlidePart!.NotesSlide);
        Assert.Equal("Speaker notes 第一行Second line", string.Concat(notes.Descendants<A.Text>().Select(node => node.Text)));
        Assert.NotNull(slides[0].NotesSlidePart!.NotesMasterPart);
        Assert.NotNull(slides[0].SlideLayoutPart!.SlideMasterPart!.ThemePart);
        Assert.Equal(slides[0].ImageParts.Single().Uri, slides[1].ImageParts.Single().Uri);
        using var image = new MemoryStream();
        using var source = slides[0].ImageParts.Single().GetStream();
        source.CopyTo(image);
        Assert.Equal(SquarePng, image.ToArray());
        var picture = firstSlide.Descendants<P.Picture>().Single();
        var dimensions = picture.ShapeProperties!.Transform2D!.Extents!;
        Assert.Equal(dimensions.Cx!.Value, dimensions.Cy!.Value);
        var picturePosition = picture.ShapeProperties.Transform2D.Offset!;
        Assert.InRange(picturePosition.X!.Value + dimensions.Cx.Value, 1, width);
        Assert.InRange(picturePosition.Y!.Value + dimensions.Cy.Value, 1, 6858000);
        AssertNoStaging();
    }

    [Fact]
    public async Task ExistingDestinationNeedsExactHashAndStaleReplacementKeepsOriginalBytes()
    {
        var first = await _service.CreatePresentationAsync(_root, Deck("deck.pptx", "Original"));
        Assert.False(first.IsError, first.Content);
        var original = await File.ReadAllBytesAsync(first.ArtifactPath!);
        Assert.True((await _service.CreatePresentationAsync(_root, Deck("deck.pptx", "Missing hash"))).IsError);
        Assert.True((await _service.CreatePresentationAsync(_root, Deck("deck.pptx", "Stale hash") with { ExpectedSha256 = new string('0', 64) })).IsError);
        Assert.Equal(original, await File.ReadAllBytesAsync(first.ArtifactPath!));
        var replaced = await _service.CreatePresentationAsync(_root, Deck("deck.pptx", "Replacement") with { ExpectedSha256 = Hash(original) });
        Assert.False(replaced.IsError, replaced.Content);
        using var document = PresentationDocument.Open(replaced.ArtifactPath!, false);
        Assert.Equal("Replacement", document.PackageProperties.Title);
        AssertNoStaging();
    }

    [Fact]
    public async Task ConcurrentNewDecksNeverClobberEachOther()
    {
        var results = await Task.WhenAll(_service.CreatePresentationAsync(_root, Deck("race.pptx", "First")),
            new WorkspaceService().CreatePresentationAsync(_root, Deck("race.pptx", "Second")));
        var winner = Assert.Single(results, result => !result.IsError);
        Assert.Single(results, result => result.IsError);
        using var metadata = JsonDocument.Parse(winner.Content);
        Assert.Equal(metadata.RootElement.GetProperty("sha256").GetString(), Hash(await File.ReadAllBytesAsync(winner.ArtifactPath!)));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("../escape.pptx", null)]
    [InlineData(".git/escape.pptx", null)]
    [InlineData("deck.pptx", "../image.png")]
    [InlineData("deck.pptx", ".git/image.png")]
    public async Task OutputAndImagePathsUseWorkspaceBoundary(string path, string? imagePath)
    {
        var request = Deck(path, "Boundary") with
        {
            Slides = [new PresentationSlide("Title", [], ImagePath: imagePath, ImageAspectRatio: imagePath is null ? null : 1)]
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreatePresentationAsync(_root, request));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidImageAndCancelledPreparationPreserveDestination()
    {
        var first = await _service.CreatePresentationAsync(_root, Deck("deck.pptx", "Original"));
        var original = await File.ReadAllBytesAsync(first.ArtifactPath!);
        await File.WriteAllTextAsync(Path.Combine(_root, "fake.png"), "This is not an image");
        var request = Deck("deck.pptx", "Replacement") with
        {
            ExpectedSha256 = Hash(original), Slides = [new PresentationSlide("Title", [], ImagePath: "fake.png", ImageAspectRatio: 1)]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreatePresentationAsync(_root, request));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreatePresentationAsync(_root, Deck("cancelled.pptx", "Cancelled"), cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_root, "cancelled.pptx")));
        Assert.Equal(original, await File.ReadAllBytesAsync(first.ArtifactPath!));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("{\"path\":\"deck.pptx\",\"title\":\"x\",\"slides\":[]}")]
    [InlineData("{\"path\":\"deck.pptx\",\"title\":\"x\",\"slides\":[{\"title\":\"x\",\"body\":[],\"imagePath\":\"x.png\"}]}")]
    [InlineData("{\"path\":\"deck.pptx\",\"title\":\"x\",\"slides\":[{\"title\":\"x\",\"body\":[]}],\"unexpected\":true}")]
    [InlineData("{\"path\":\"deck.pptx\",\"title\":\"x\",\"slides\":[{\"title\":\"x\\u0000\",\"body\":[]}]}")]
    public async Task ToolRejectsInvalidStructuredArgumentsWithoutCreatingFiles(string arguments)
    {
        var tool = _service.CreatePresentationTool();
        var result = await tool.ExecuteAsync(new("invalid", tool.Definition.Name, arguments), new(_root));
        Assert.True(result.IsError);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DenseBodyAndOversizedImageAreRefusedBeforeOutput()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreatePresentationAsync(_root, Deck("dense.pptx", "Dense") with
        { Slides = [new PresentationSlide("Title", Enumerable.Repeat(new string('x', 160), 6).ToArray())] }));
        await using (var stream = File.Create(Path.Combine(_root, "oversized.png"))) stream.SetLength(4 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<IOException>(() => _service.CreatePresentationAsync(_root, Deck("image.pptx", "Oversized") with
        { Slides = [new PresentationSlide("Title", [], ImagePath: "oversized.png", ImageAspectRatio: 1)] }));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.pptx", SearchOption.AllDirectories));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixFifoImagesAndExistingOutputsAreRejectedBeforeAnOpenCanBlock(bool existingOutput)
    {
        if (OperatingSystem.IsWindows()) return;
        var fifo = Path.Combine(_root, existingOutput ? "fifo-output.pptx" : "fifo-image.png");
        var created = await _service.RunProgramAsync(_root, "/usr/bin/mkfifo", [fifo], 2);
        Assert.False(created.IsError, created.Content);
        var request = existingOutput
            ? Deck("fifo-output.pptx", "FIFO output") with { ExpectedSha256 = new string('0', 64) }
            : Deck("deck.pptx", "FIFO image") with
            { Slides = [new PresentationSlide("Title", [], ImagePath: "fifo-image.png", ImageAspectRatio: 1)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreatePresentationAsync(_root, request).WaitAsync(TimeSpan.FromSeconds(5)));
        var isFifo = await _service.RunProgramAsync(_root, OperatingSystem.IsMacOS() ? "/bin/test" : "/usr/bin/test", ["-p", fifo], 2);
        Assert.False(isFifo.IsError, isFifo.Content);
        Assert.False(File.Exists(Path.Combine(_root, "deck.pptx")));
        AssertNoStaging();
    }

    private static PresentationRequest Deck(string path, string title) => new(path, title, [new("Title", ["One bullet", "Another bullet"], "Speaker notes")]);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private void AssertNoStaging() => Assert.Empty(Directory.EnumerateFiles(_root, "*.workbench-*.tmp", SearchOption.AllDirectories));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
