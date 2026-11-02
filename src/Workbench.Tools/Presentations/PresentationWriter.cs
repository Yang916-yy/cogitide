using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Workbench.Protocol;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int PresentationFileLimit = 32 * 1024 * 1024;
    private const int PresentationImageLimit = 4 * 1024 * 1024;
    private const int PresentationImagesLimit = 16 * 1024 * 1024;
    private sealed record PresentationImage(string Path, byte[] Bytes, string MediaType, string Sha256);

    /// <summary>Generate a bounded SDK package, then use the workspace edit lock and current-byte guard to commit it.</summary>
    public async Task<ToolResult> CreatePresentationAsync(string workspace, PresentationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ct.ThrowIfCancellationRequested();
        var root = Root(workspace);
        var full = Resolve(root, request.Path);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        var gate = FileLocks.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            byte[]? previous = null;
            if (File.Exists(full))
            {
                if (string.IsNullOrEmpty(request.ExpectedSha256)) return new("The presentation already exists. Supply its exact current SHA256 to replace it, or choose a new path.", true);
                previous = await ReadPresentationBytesAsync(root, full, PresentationFileLimit, ct);
                if (!Hash(previous).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    return new("Presentation changed or expectedSha256 does not match. No changes were made.", true);
            }
            else if (!string.IsNullOrEmpty(request.ExpectedSha256)) return new("The expected presentation no longer exists. No changes were made.", true);

            Dictionary<string, PresentationImage> images = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            long imageBytes = 0;
            foreach (var slide in request.Slides.Where(slide => slide.ImagePath is not null))
            {
                ct.ThrowIfCancellationRequested();
                var source = Resolve(root, slide.ImagePath!);
                if (images.ContainsKey(source)) continue;
                var bytes = await ReadPresentationBytesAsync(root, source, PresentationImageLimit, ct);
                imageBytes += bytes.Length;
                if (imageBytes > PresentationImagesLimit) throw new ArgumentException("Combined unique presentation images exceed 16 MiB.");
                var mediaType = ImageInputs.DetectMediaType(bytes);
                if (mediaType is not ("image/png" or "image/jpeg")) throw new ArgumentException("Presentation images must be PNG or JPEG files matching their file signatures.");
                images.Add(source, new(Path.GetRelativePath(root, source).Replace('\\', '/'), bytes, mediaType, Hash(bytes)));
            }
            var data = BuildPresentation(root, request, images, ct);
            if (data.Length > PresentationFileLimit) throw new IOException("Presentation exceeds the 32 MiB output budget.");
            ct.ThrowIfCancellationRequested();
            Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Resolve(root, relative);
            var temporary = full + ".workbench-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
                    await stream.WriteAsync(data, ct);
                Resolve(root, relative);
                if (previous is not null)
                {
                    var current = await ReadPresentationBytesAsync(root, full, PresentationFileLimit, ct);
                    if (!current.AsSpan().SequenceEqual(previous)) return new("Presentation changed while preparing it. No changes were made.", true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
                }
                else if (File.Exists(full)) return new("A file was created at the presentation path while preparing it. No changes were made.", true);
                ct.ThrowIfCancellationRequested();
                // A cancelled preparation never reaches this single-file commit. Do not observe cancellation
                // after the move and misreport a known committed artifact as an unobserved outcome.
                File.Move(temporary, full, previous is not null);
                return JsonResult(new
                {
                    path = relative, sha256 = Hash(data), bytes = data.Length, slideCount = request.Slides.Count,
                    aspectRatio = request.AspectRatio, editable = true, packageValidated = true,
                    visualVerification = "not_rendered", changed = true,
                    images = images.Values.Select(image => new { path = image.Path, sha256 = image.Sha256, bytes = image.Bytes.Length }),
                    hint = "Open the .pptx in a presentation viewer to inspect text fit and image proportions. SDK schema validation does not render slides."
                }, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadPresentationBytesAsync(string workspace, string full, int maximum, CancellationToken ct)
    {
        await RequireRegularFileAsync(workspace, full, ct);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximum) throw new IOException($"Presentation input exceeds its {maximum}-byte budget.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        if (stream.ReadByte() != -1) throw new IOException("Presentation input changed size while being read.");
        return bytes;
    }

    private static byte[] BuildPresentation(string workspace, PresentationRequest request, Dictionary<string, PresentationImage> images, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            document.PackageProperties.Title = request.Title;
            document.PackageProperties.Creator = "Cogitide";
            document.PackageProperties.Description = "Editable structured slides created with the Open XML SDK; visual layout requires viewer inspection.";
            var presentation = document.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>();
            var layout = master.AddNewPart<SlideLayoutPart>();
            layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(EmptyShapeTree()) { Name = "Cogitide content" },
                new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank, Preserve = true };
            layout.AddPart(master);
            master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(EmptyShapeTree()), PresentationColorMap(),
                new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }),
                new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
            var theme = master.AddNewPart<ThemePart>();
            theme.Theme = PresentationTheme();
            var slideIds = new P.SlideIdList();
            var width = request.AspectRatio == "standard" ? 9_144_000 : 12_192_000;
            presentation.Presentation = new P.Presentation(new P.SlideMasterIdList(new P.SlideMasterId
                { Id = 2147483648U, RelationshipId = presentation.GetIdOfPart(master) }));
            NotesMasterPart? notesMaster = null;
            if (request.Slides.Any(slide => !string.IsNullOrEmpty(slide.Notes)))
            {
                notesMaster = presentation.AddNewPart<NotesMasterPart>();
                notesMaster.AddPart(theme);
                notesMaster.NotesMaster = new P.NotesMaster(new P.CommonSlideData(EmptyShapeTree()), PresentationColorMap(), new P.NotesStyle());
                presentation.Presentation.Append(new P.NotesMasterIdList(new P.NotesMasterId { Id = presentation.GetIdOfPart(notesMaster) }));
            }
            presentation.Presentation.Append(slideIds, new P.SlideSize { Cx = width, Cy = 6_858_000, Type = request.AspectRatio == "standard" ? P.SlideSizeValues.Screen4x3 : P.SlideSizeValues.Screen16x9 },
                new P.NotesSize { Cx = 6_858_000, Cy = 9_144_000 }, new P.DefaultTextStyle());
            Dictionary<string, ImagePart> embedded = new(images.Comparer);
            for (var index = 0; index < request.Slides.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var content = request.Slides[index];
                var part = presentation.AddNewPart<SlidePart>();
                part.AddPart(layout);
                var tree = EmptyShapeTree();
                var wide = request.AspectRatio == "widescreen";
                tree.Append(PresentationRectangle(2, "Accent", .64, .35, .46, .07, "2563EB"));
                tree.Append(PresentationText(3, "Title", .64, .68, wide ? 12.05 : 8.72, 1.15,
                    [content.Title], 3400, "152238", bold: true));
                var bodyWidth = content.ImagePath is null ? (wide ? 11.9 : 8.6) : (wide ? 5.55 : 4.05);
                tree.Append(PresentationText(4, "Body", .70, 2.05, bodyWidth, 4.44, content.Body,
                    content.ImagePath is null ? 2600 : wide ? 2300 : 2100, "334155", bullets: true));
                if (content.ImagePath is not null)
                {
                    var key = Resolve(workspace, content.ImagePath);
                    if (!embedded.TryGetValue(key, out var imagePart))
                    {
                        imagePart = part.AddImagePart(images[key].MediaType == "image/png" ? ImagePartType.Png : ImagePartType.Jpeg);
                        using var imageStream = new MemoryStream(images[key].Bytes, writable: false);
                        imagePart.FeedData(imageStream);
                        embedded.Add(key, imagePart);
                    }
                    else part.AddPart(imagePart);
                    tree.Append(PresentationPicture(5, part.GetIdOfPart(imagePart), images[key].Path,
                        wide ? 6.95 : 5.15, 2.05, wide ? 5.65 : 4.15, 4.44, content.ImageAspectRatio!.Value));
                }
                tree.Append(PresentationRectangle(6, "Footer rule", .64, 6.82, wide ? 12.05 : 8.72, .008, "E2E8F0"));
                tree.Append(PresentationText(7, "Presentation", .64, 6.98, wide ? 10.6 : 7.25, .25, [request.Title], 1000, "64748B"));
                tree.Append(PresentationText(8, "Slide number", wide ? 11.6 : 8.25, 6.98, 1.05, .25,
                    [$"{index + 1} / {request.Slides.Count}"], 1000, "64748B"));
                part.Slide = new P.Slide(new P.CommonSlideData(new P.Background(new P.BackgroundProperties(PresentationFill("FFFFFF"))), tree),
                    new P.ColorMapOverride(new A.MasterColorMapping()));
                slideIds.Append(new P.SlideId { Id = (uint)(256 + index), RelationshipId = presentation.GetIdOfPart(part) });
                if (!string.IsNullOrEmpty(content.Notes))
                {
                    var notes = part.AddNewPart<NotesSlidePart>();
                    notes.AddPart(part);
                    notes.AddPart(notesMaster!);
                    var notesTree = EmptyShapeTree();
                    var text = PresentationText(2, "Speaker notes", .6, 1, 6.3, 8,
                        content.Notes.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'), 1400, "152238");
                    text.NonVisualShapeProperties!.ApplicationNonVisualDrawingProperties!.Append(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U });
                    notesTree.Append(text);
                    notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(notesTree), new P.ColorMapOverride(new A.MasterColorMapping()));
                }
            }
            var validation = new OpenXmlValidator(FileFormatVersions.Office2007) { MaxNumberOfErrors = 5 };
            var errors = validation.Validate(document, ct).Select(error => $"{error.Description} ({error.Part?.Uri})").ToArray();
            if (errors.Length > 0) throw new InvalidDataException("Generated presentation failed Open XML validation: " + string.Join("; ", errors));
            ct.ThrowIfCancellationRequested();
        }
        return stream.ToArray();
    }

    private static long Emu(double inches) => (long)Math.Round(inches * 914400);
    private static A.SolidFill PresentationFill(string color) => new(new A.RgbColorModelHex { Val = color });
    private static A.Transform2D PresentationTransform(double x, double y, double width, double height) => new(
        new A.Offset { X = Emu(x), Y = Emu(y) }, new A.Extents { Cx = Emu(width), Cy = Emu(height) });

    private static P.ShapeTree EmptyShapeTree() => new(new P.NonVisualGroupShapeProperties(
        new P.NonVisualDrawingProperties { Id = 1U, Name = "Shapes" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 },
            new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 0, Cy = 0 })));

    private static P.NonVisualShapeProperties PresentationNonVisual(uint id, string name, bool text = false) => new(
        new P.NonVisualDrawingProperties { Id = id, Name = name }, new P.NonVisualShapeDrawingProperties { TextBox = text }, new P.ApplicationNonVisualDrawingProperties());

    private static P.Shape PresentationRectangle(uint id, string name, double x, double y, double width, double height, string color) => new(
        PresentationNonVisual(id, name), new P.ShapeProperties(PresentationTransform(x, y, width, height),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }, PresentationFill(color), new A.Outline(new A.NoFill())));

    private static P.Shape PresentationText(uint id, string name, double x, double y, double width, double height,
        IEnumerable<string> paragraphs, int fontSize, string color, bool bold = false, bool bullets = false)
    {
        var body = new P.TextBody(new A.BodyProperties(new A.NormalAutoFit())
            { LeftInset = 0, RightInset = 0, TopInset = 0, BottomInset = 0, Wrap = A.TextWrappingValues.Square, Anchor = A.TextAnchoringTypeValues.Top }, new A.ListStyle());
        foreach (var paragraph in paragraphs)
        {
            var properties = new A.ParagraphProperties(new A.LineSpacing(new A.SpacingPercent { Val = 110000 }),
                new A.SpaceAfter(new A.SpacingPoints { Val = bullets ? 1000 : 0 }));
            if (bullets) { properties.LeftMargin = 260000; properties.Indent = -200000; properties.Append(new A.CharacterBullet { Char = "•" }); }
            else properties.Append(new A.NoBullet());
            var run = new A.RunProperties(PresentationFill(color), new A.LatinFont { Typeface = "Aptos" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" })
                { FontSize = fontSize, Bold = bold, Language = "en-US", AlternativeLanguage = "zh-CN" };
            var line = new A.Paragraph(properties);
            var lines = paragraph.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) line.Append(new A.Break((A.RunProperties)run.CloneNode(true)));
                line.Append(new A.Run((A.RunProperties)run.CloneNode(true), new A.Text(lines[index])));
            }
            line.Append(new A.EndParagraphRunProperties { FontSize = fontSize, Language = "en-US" });
            body.Append(line);
        }
        if (!body.Elements<A.Paragraph>().Any()) body.Append(new A.Paragraph(new A.EndParagraphRunProperties { FontSize = fontSize }));
        return new P.Shape(PresentationNonVisual(id, name, text: true), new P.ShapeProperties(PresentationTransform(x, y, width, height),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }, new A.NoFill(), new A.Outline(new A.NoFill())), body);
    }

    private static P.Picture PresentationPicture(uint id, string relationship, string source, double x, double y, double width, double height, double ratio)
    {
        var fittedWidth = Math.Min(width, height * ratio);
        var fittedHeight = fittedWidth / ratio;
        return new P.Picture(new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = id, Name = "Image", Description = source },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }), new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(PresentationTransform(x + (width - fittedWidth) / 2, y + (height - fittedHeight) / 2, fittedWidth, fittedHeight),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    private static P.ColorMap PresentationColorMap() => new()
    {
        Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
        Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
        Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2,
        Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
        Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
        Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
    };

    private static A.Theme PresentationTheme()
    {
        var colors = new A.ColorScheme(new A.Dark1Color(new A.RgbColorModelHex { Val = "152238" }), new A.Light1Color(new A.RgbColorModelHex { Val = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "334155" }), new A.Light2Color(new A.RgbColorModelHex { Val = "F1F5F9" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "2563EB" }), new A.Accent2Color(new A.RgbColorModelHex { Val = "0F766E" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "7C3AED" }), new A.Accent4Color(new A.RgbColorModelHex { Val = "EA580C" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "0284C7" }), new A.Accent6Color(new A.RgbColorModelHex { Val = "BE185D" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "2563EB" }), new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "7C3AED" })) { Name = "Cogitide" };
        var fonts = new A.FontScheme(new A.MajorFont(new A.LatinFont { Typeface = "Aptos Display" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" }),
            new A.MinorFont(new A.LatinFont { Typeface = "Aptos" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" })) { Name = "Cogitide" };
        var fills = new A.FillStyleList();
        var lines = new A.LineStyleList();
        var effects = new A.EffectStyleList();
        var backgrounds = new A.BackgroundFillStyleList();
        for (var index = 0; index < 3; index++)
        {
            fills.Append(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }));
            lines.Append(new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.PresetDash { Val = A.PresetLineDashValues.Solid }) { Width = 9525 });
            effects.Append(new A.EffectStyle(new A.EffectList()));
            backgrounds.Append(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }));
        }
        return new A.Theme(new A.ThemeElements(colors, fonts, new A.FormatScheme(fills, lines, effects, backgrounds) { Name = "Cogitide" }),
            new A.ObjectDefaults(), new A.ExtraColorSchemeList()) { Name = "Cogitide clean" };
    }
}
