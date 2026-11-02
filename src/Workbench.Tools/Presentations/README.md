# Editable presentation generation

`create_presentation` writes an editable `.pptx` in the selected workspace. It is a normal approval-required tool available to the shared Desktop/Host kernel, including the existing isolated tool worker. The tool accepts an output path, a document title, and 1–50 slides with titles, bullet paragraphs, optional speaker notes and optional local PNG/JPEG images. Its JSON result returns the workspace-relative output path, byte count, current SHA256 and image-source hashes. Open that file externally in a presentation application; this module does not add an in-app slide viewer or a direct artifact-open button. Direct tool execution also sets the existing `ToolResult.ArtifactPath`, but the current Desktop does not consume that property and the isolated broker does not retain it.

```json
{
  "path": "exports/project-review.pptx",
  "title": "Project review",
  "slides": [
    {
      "title": "What changed",
      "body": ["Editable native PowerPoint text", "Local image embedded into the package"],
      "notes": "Speaker notes stay outside the visible slide.",
      "imagePath": "assets/diagram.png",
      "imageAspectRatio": 1.7777777778
    }
  ],
  "aspectRatio": "widescreen"
}
```

The default is 16:9; `standard` selects 4:3. The fixed clean theme uses a white background, dark text, blue accent, aligned margins, footers, native text shapes and fitted image boxes. Images are embedded; there are no remote fetches, external relationships, macros, shell commands, templates, model calls or new credentials. Text remains editable in PowerPoint-compatible applications. Existing viewer fonts may substitute for Aptos / Microsoft YaHei.

`imageAspectRatio` is required when an image is supplied and must be its original width divided by height. The caller supplies dimensions; this module intentionally reuses the existing `ImageInputs` signature detector instead of adding an image decoder. Signature validation is not proof that an image fully decodes. The aspect ratio is trusted for layout, and the generated picture is fitted without cropping. A wrong supplied ratio can distort an image. Supported ratios range from 0.1 to 10.

## Reuse and provenance

The implementation directly reuses Microsoft's MIT-licensed **Open XML SDK 3.5.1** (`DocumentFormat.OpenXml`, framework package 3.5.1 and `System.IO.Packaging` 10.0.2). The SDK owns Office package serialization, typed PresentationML/DrawingML objects, part relationships and schema validation. There is no custom ZIP/XML serializer, presentation parser, renderer or Office automation layer. `System.Text.Json` reads the bounded tool contract; BCL streams, SHA-256, `File.Move`, cancellation and the existing `WorkspaceService` path resolver and edit locks own local I/O policy.

The official [presentation creation guide](https://learn.microsoft.com/en-us/office/open-xml/presentation/how-to-create-a-presentation-document-by-providing-a-file-name) and [SDK sample at v3.5.1](https://github.com/dotnet/Open-XML-SDK/blob/3139fdfd27414548a41555f7848d5728f6e71a42/samples/common/PowerPointUtils.cs) reference the required package/master/layout/theme relationships. Product code independently builds its fixed theme, bounded content model, notes, image fit and checked commit through the typed SDK. This is API integration and behavioral reference, not a line-for-line port of the sample. [PROVENANCE](Licenses/PROVENANCE.md) records exact revisions and retained license notices; the project copies these into build/publish output.

Compared choices: Office COM automation needs an installed, interactive Windows application and would violate the headless cross-platform Host boundary; a second XML/ZIP writer would duplicate mature SDK functionality; a full slide-design or graphics runtime is unnecessary for this deliberately narrow editable output. This module does not claim general `.pptx` import, editing of arbitrary existing slides, chart generation, template fidelity or automatic visual design.

## Checked writes and limits

`WorkspaceService.Resolve` checks the output and every image path; `IWorkspacePathTool` exposes all of them to scoped project instructions before execution. Relative paths, Git-internal rejection and link/junction rejection share the ordinary workspace boundary. These application checks are not an OS sandbox or a defense against every hostile filesystem race.

Image reads and existing-output hash reads also reuse the Workspace module's [regular-file preflight](../Workspace/README.md), rejecting Unix FIFOs/devices/sockets and Windows device entries before an open can block. The fixed Unix system predicate uses the existing bounded argv runner; it does not execute package content or a shell. The ordinary same-user race limitation still applies between the preflight and open.

An existing destination is refused by default. A replacement requires `expectedSha256` matching its complete current bytes. The shared per-file edit lock serializes other workspace edits, and the destination is checked again before a staged single-file move. New files use a no-overwrite move. A failed or cancelled preparation removes its temporary file and preserves any existing destination. Cancellation is checked before commit; a completed move is reported as a committed artifact. The output parent directory may have been created during a subsequently cancelled write. As with existing workspace editing, external writers can race the final check; this is not a filesystem transaction across a crash or power loss.

Limits are 50 slides, six bullets per slide, 160 characters per bullet, 600 body characters per slide (320 with an image), 120 characters per title, 4,000 characters per notes page and 100,000 combined text characters. Titles/bullets are single paragraphs; notes permit line breaks. Each image is at most 4 MiB; unique source images total at most 16 MiB, shared image parts avoid re-embedding the same source path, and output is capped at 32 MiB. Full bounded inputs/output are held in memory. These limits are resource and density controls, not a proof of text fit for every script or viewer.

## Verification boundary

Every generated package is checked by the SDK's `OpenXmlValidator` for the Office 2007 feature subset before commit. [PresentationToolsTests](../../../tests/Workbench.Tests/PresentationToolsTests.cs) reopen real `.pptx` packages and check SDK validity, editable text, speaker notes, embedded image bytes and part reuse, both aspect ratios, checked paths, no-clobber/exact-hash replacement, concurrent writers, invalid inputs, cancellation and staging cleanup.

The explicit opt-in [OsIsolationNativeTests](../../../tests/Workbench.Tests/OsIsolationNativeTests.cs) writable-workspace fixture also creates a presentation through the actual native broker/worker, checks the worker's schema-validation result and verifies the saved bytes through their SHA256. This exercises SDK package generation under the native filesystem/dependency grants when that fixture is enabled on a supported host; a skipped fixture supplies no native evidence.

SDK validity does **not** render slides, check font availability, measure glyphs or prove visual quality. Native normal-autofit hints and bounded content help presentation applications fit text, but the returned result explicitly states `visualVerification: "not_rendered"`. Open the artifact in a presentation viewer and inspect it before claiming rendering or polished layout. No desktop application is launched automatically and no paid benchmark is needed for these deterministic fixtures.
