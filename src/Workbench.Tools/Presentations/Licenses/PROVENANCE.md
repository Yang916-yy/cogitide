# Presentation dependency provenance

Checked 2026-11-02. These are dependency/source records, not test or visual-quality claims.

| Component | Fixed source / license | Integration boundary |
| --- | --- | --- |
| DocumentFormat.OpenXml and DocumentFormat.OpenXml.Framework | Microsoft / .NET Foundation, [NuGet 3.5.1](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1), MIT; upstream tag `v3.5.1`, Git revision [`3139fdfd27414548a41555f7848d5728f6e71a42`](https://github.com/dotnet/Open-XML-SDK/tree/3139fdfd27414548a41555f7848d5728f6e71a42) | Official typed .NET APIs own package serialization, parts, relationships and validation. Both versions are recorded in package locks. No SDK implementation was copied, modified or translated. [Original license](Open-XML-SDK-LICENSE.txt) applies to both packages. |
| System.IO.Packaging | Microsoft, MIT, transitive NuGet `10.0.2`; package metadata records [dotnet/dotnet revision `44525024595742ebe09023abe709df51de65009b`](https://github.com/dotnet/dotnet/tree/44525024595742ebe09023abe709df51de65009b) | The SDK's package dependency supplies OPC storage. Product code does not implement it. Retained [repository license](System-IO-Packaging-LICENSE.txt) and [package third-party notices](System-IO-Packaging-THIRD-PARTY-NOTICES.txt). The full original notice is retained; its inventory is not a claim that all listed components are directly used by this module. |

Behavioral/API references are the [Microsoft Learn presentation creation guide](https://learn.microsoft.com/en-us/office/open-xml/presentation/how-to-create-a-presentation-document-by-providing-a-file-name), the fixed revision's [`samples/common/PowerPointUtils.cs`](https://github.com/dotnet/Open-XML-SDK/blob/3139fdfd27414548a41555f7848d5728f6e71a42/samples/common/PowerPointUtils.cs), and the [official validator contract](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.validation.openxmlvalidator). The relationship structure is referenced; the theme/layout, bounded JSON contract, speaker notes, local image placement and checked file commit are project integration, not a sample source port.

Existing C# components reused are `WorkspaceService.Resolve`, its shared `FileLocks`, `Hash`/`JsonResult`, `ImageInputs.DetectMediaType`, `System.Text.Json`, `FileStream`, `MemoryStream`, `File.Move` and cancellation tokens. There is no duplicate presentation parser, HTTP client, image decoder, ZIP/XML implementation, renderer or process scheduler. Office/LibreOffice is not a runtime dependency.

`PresentationToolsTests` provide deterministic package-level and file-policy coverage. SDK validation is distinct from rendered layout, usability evidence and research/benchmark benefit; no rendered result or paid model benchmark is asserted here.
