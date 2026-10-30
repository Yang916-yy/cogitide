using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int SemanticFileLimit = 256;
    private const int SemanticByteLimit = 8 * 1024 * 1024;
    private const int SemanticNodeLimit = 250_000;
    private const int SemanticResultLimit = 10_000;
    private sealed record SemanticSource(string Path, string Sha256, SyntaxTree Tree, SyntaxNode Root);
    private sealed record SemanticBoundary(string Directory, string[] Markers);
    private sealed record SemanticLocation(string Path, int Line, int Column, int EndLine, int EndColumn, int Length,
        string Kind, string? Code = null, string? Message = null, string? Severity = null);

    public IAgentTool CreateCSharpSemanticTool() => new CSharpSemanticTool(this);

    /// <summary>Source-only Roslyn binding. Does not evaluate or execute repository projects.</summary>
    public async Task<ToolResult> CSharpSemanticAsync(string workspace, string action, string path, int line = 1,
        int column = 1, int offset = 0, int limit = 100, string? expectedSnapshot = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidatePage(offset, limit);
        if (action is not ("definition" or "references" or "diagnostics"))
            return new("action must be definition, references or diagnostics.", true);
        if (line < 1 || column < 1) throw new ArgumentOutOfRangeException(nameof(line), "line and column are one-based UTF-16 positions.");
        var root = Root(workspace);
        var full = Resolve(root, path);
        if (!Path.GetExtension(full).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return new("csharp_semantic supports C# .cs source files only.", true);
        path = SemanticRelative(root, full);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await CSharpSemanticCoreAsync(root, full, action, path, line, column, offset, limit, expectedSnapshot, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { return new("C# semantic analysis exceeded its 20 second budget. No partial semantic page was returned. Select a smaller workspace.", true); }
    }

    private async Task<ToolResult> CSharpSemanticCoreAsync(string workspace, string full, string action, string path,
        int line, int column, int offset, int limit, string? expectedSnapshot, CancellationToken ct)
    {
        // Project files are boundary markers only: never load an MSBuild workspace, imports or generators.
        Dictionary<string, SemanticBoundary?> boundaries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var probes = 0;
        SemanticBoundary? Boundary(string probeDirectory)
        {
            List<string> trail = [];
            SemanticBoundary? found;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (boundaries.TryGetValue(probeDirectory, out found)) break;
                if (++probes > 4096) throw new IOException("C# project-boundary probe budget reached. Select a smaller workspace.");
                trail.Add(probeDirectory);
                var markers = Directory.EnumerateFiles(probeDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Take(33)
                    .Select(marker => SemanticRelative(workspace, Resolve(workspace, SemanticRelative(workspace, marker)))).Order(StringComparer.Ordinal).ToArray();
                if (markers.Length > 32) throw new IOException("C# project-marker budget reached in one directory.");
                if (markers.Length > 0) { found = new(probeDirectory, markers); break; }
                if (probeDirectory.Equals(workspace, PathComparison)) { found = null; break; }
                probeDirectory = Path.GetDirectoryName(probeDirectory)!;
            }
            foreach (var visited in trail) boundaries[visited] = found;
            return found;
        }
        var boundary = Boundary(Path.GetDirectoryName(full)!);
        var directory = boundary?.Directory ?? workspace;
        var prefix = SemanticRelative(workspace, directory);
        var inventory = await InventoryAsync(workspace, prefix.Length == 0 ? "" : prefix + "/", ct);
        if (inventory.Truncated)
            return new("C# semantic inventory budget reached. No partial compilation was returned; select a smaller workspace.", true);
        var paths = new List<string>();
        foreach (var candidate in inventory.Files.Where(candidate => Path.GetExtension(candidate).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var candidateBoundary = Boundary(Path.GetDirectoryName(Resolve(workspace, candidate))!);
            if (!string.Equals(candidateBoundary?.Directory, boundary?.Directory, PathComparison)) continue;
            paths.Add(candidate);
            if (paths.Count > SemanticFileLimit)
                return new($"C# semantic source-file budget ({SemanticFileLimit} files) reached. No partial compilation was returned; select a smaller workspace.", true);
        }
        paths.Sort(StringComparer.Ordinal);
        if (!paths.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            return new("The requested source is absent from the repository inventory (it may be ignored). No ignored source was loaded.", true);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendNavigationDigest(digest, new { action, path, line, column, directory = prefix, projectMarkers = boundary?.Markers,
            paths, inventory.Source, inventory.SkippedEntries, parser = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
            runtime = Environment.Version.ToString(), semanticVersion = 1 });
        List<SemanticSource> sources = [];
        var bytesRead = 0;
        var visitedNodes = 0;
        foreach (var candidate in paths)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = Resolve(workspace, candidate);
            var before = new FileInfo(sourcePath);
            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;
            if (length > MaxFileBytes || length + bytesRead > SemanticByteLimit)
                return new("C# semantic byte budget reached (1 MiB per file, 8 MiB total). No partial compilation was returned.", true);
            var bytes = await ReadBoundedAsync(sourcePath, ct);
            var after = new FileInfo(sourcePath);
            if (after.Length != length || after.LastWriteTimeUtc != lastWrite || bytes.Length != length)
                return new("Source changed while reading. Restart semantic analysis; no partial page was returned.", true);
            bytesRead += bytes.Length;
            if (bytesRead > SemanticByteLimit) return new("C# semantic byte budget reached. No partial compilation was returned.", true);
            var sha256 = Hash(bytes);
            AppendNavigationDigest(digest, new { path = candidate, sha256 });
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(Decode(bytes), Encoding.UTF8),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), candidate, ct);
            var syntax = await tree.GetRootAsync(ct);
            Stack<(SyntaxNodeOrToken Syntax, int Depth)> pending = new();
            pending.Push((syntax, 0));
            while (pending.TryPop(out var next))
            {
                ct.ThrowIfCancellationRequested();
                if (++visitedNodes > SemanticNodeLimit || next.Depth > 128)
                    return new("C# semantic syntax budget reached (250000 nodes/tokens or depth 128). No partial compilation was returned.", true);
                if (next.Syntax.AsNode() is { } node)
                    foreach (var child in node.ChildNodesAndTokens()) pending.Push((child, next.Depth + 1));
            }
            sources.Add(new(candidate, sha256, tree, syntax));
        }
        // Only framework assemblies already advertised by this process are referenced. Repository DLLs,
        // NuGet dependencies, project references and analyzers are never loaded or executed.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var referencePaths = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? [])
            .Where(reference => string.Equals(Path.GetDirectoryName(reference), runtimeDirectory, PathComparison))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(257).ToArray();
        if (referencePaths.Length is 0 or > 256)
            return new("A bounded set of runtime BCL metadata references is unavailable. Semantic analysis was not attempted.", true);
        AppendNavigationDigest(digest, referencePaths.Select(reference => new { name = Path.GetFileName(reference), length = new FileInfo(reference).Length }).ToArray());
        var snapshot = Convert.ToHexStringLower(digest.GetHashAndReset());
        if (expectedSnapshot is not null && !snapshot.Equals(expectedSnapshot, StringComparison.OrdinalIgnoreCase))
            return new("Semantic source contents, scope or query changed between pages. Restart at offset 0; no stale semantic page was returned.", true);

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var references = referencePaths.Select(reference => MetadataReference.CreateFromFile(reference)).ToArray();
            var compilation = CSharpCompilation.Create("Cogitide.SourceInspection", sources.Select(source => source.Tree), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false, nullableContextOptions: NullableContextOptions.Enable));
            List<SemanticLocation> results = [];
            ISymbol? symbol = null;
            var selected = sources.Single(source => source.Path.Equals(path, PathComparison));
            if (action == "diagnostics")
            {
                foreach (var diagnostic in compilation.GetDiagnostics(ct).Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                    .OrderBy(diagnostic => diagnostic.Location.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (results.Count >= SemanticResultLimit) return new ToolResult("C# semantic diagnostic budget (10000 results) reached. Narrow the workspace; no partial page was returned.", true);
                    results.Add(SemanticAt(diagnostic.Location, "diagnostic", diagnostic.Id,
                        SemanticClip(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), 1000), diagnostic.Severity.ToString()));
                }
            }
            else
            {
                var text = selected.Tree.GetText(ct);
                if (line > text.Lines.Count || column > text.Lines[line - 1].Span.Length + 1)
                    return new ToolResult("The one-based UTF-16 line/column is outside the requested source line.", true);
                var position = text.Lines[line - 1].Start + column - 1;
                if (position >= selected.Root.FullSpan.End) return new ToolResult("No bound C# symbol at this position.", true);
                var token = selected.Root.FindToken(position);
                if (!token.Span.Contains(position) || token.Parent is null ||
                    !(token.IsKind(SyntaxKind.IdentifierToken) || token.Parent is PredefinedTypeSyntax || token.Parent is OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax))
                    return new ToolResult("No bound C# symbol at this position (comments, trivia and punctuation are not symbol references).", true);
                var model = compilation.GetSemanticModel(selected.Tree);
                symbol = SemanticCanonical(SemanticSymbol(model, token.Parent, ct));
                if (symbol is null || symbol.Kind == SymbolKind.ErrorType)
                    return new ToolResult("No uniquely bound C# symbol at this position. Inspect diagnostics for missing dependencies or ambiguous overloads.", true);
                if (action == "definition")
                {
                    results.AddRange(symbol.Locations.Where(location => location.IsInSource).Select(location => SemanticAt(location, "definition"))
                        .OrderBy(location => location.Path, StringComparer.Ordinal).ThenBy(location => location.Line).ThenBy(location => location.Column));
                }
                else
                {
                    foreach (var source in sources)
                    {
                        var semanticModel = compilation.GetSemanticModel(source.Tree);
                        HashSet<TextSpan> found = [];
                        foreach (var node in source.Root.DescendantNodesAndSelf())
                        {
                            ct.ThrowIfCancellationRequested();
                            // Roslyn resolves each occurrence; text matches, comments and shadowed names cannot qualify.
                            var operatorUse = symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } &&
                                node is BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or AssignmentExpressionSyntax or CastExpressionSyntax;
                            if (node is not (SimpleNameSyntax or PredefinedTypeSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or ConstructorInitializerSyntax) && !operatorUse) continue;
                            var bound = SemanticCanonical(SemanticSymbol(semanticModel, node, ct));
                            if (!SymbolEqualityComparer.Default.Equals(symbol, bound)) continue;
                            var span = node switch
                            {
                                SimpleNameSyntax name => name.Identifier.Span,
                                PredefinedTypeSyntax predefined => predefined.Keyword.Span,
                                ObjectCreationExpressionSyntax creation => creation.Type.Span,
                                ConstructorInitializerSyntax initializer => initializer.ThisOrBaseKeyword.Span,
                                BinaryExpressionSyntax binary => binary.OperatorToken.Span,
                                PrefixUnaryExpressionSyntax prefixExpression => prefixExpression.OperatorToken.Span,
                                PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken.Span,
                                AssignmentExpressionSyntax assignment => assignment.OperatorToken.Span,
                                CastExpressionSyntax cast => cast.Type.Span,
                                _ => node.Span
                            };
                            if (!found.Add(span)) continue;
                            if (results.Count >= SemanticResultLimit) return new ToolResult("C# semantic reference budget (10000 results) reached. Narrow the workspace; no partial page was returned.", true);
                            results.Add(SemanticAt(Location.Create(source.Tree, span), "reference"));
                        }
                    }
                    results = results.OrderBy(location => location.Path, StringComparer.Ordinal).ThenBy(location => location.Line).ThenBy(location => location.Column).ToList();
                }
            }
            ct.ThrowIfCancellationRequested();
            var page = results.Skip(offset).Take(limit).ToArray();
            var description = symbol is null ? null : new { name = SemanticClip(symbol.Name, 300), kind = symbol.Kind.ToString(),
                display = SemanticClip(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), 600), metadataOnly = !symbol.Locations.Any(location => location.IsInSource) };
            return BoundedPageResult(page, offset, (long)offset + page.Length < results.Count, (items, nextOffset, responseTruncated) => new
            {
                action, path, line, column, positionEncoding = "one-based UTF-16", symbol = description, results = items, offset, limit, nextOffset,
                totalResults = results.Count, snapshot, snapshotValidated = expectedSnapshot is not null,
                snapshotScope = "Query, scope and content hashes of all compiled source files; not an OS-atomic filesystem snapshot.",
                truncated = (long)offset + items.Count < results.Count || responseTruncated, responseTruncated,
                scope = new { directory = prefix, projectMarkers = boundary?.Markers ?? [], nearestProjectBoundary = boundary is not null,
                    completeProject = false, sourceOnly = true, nestedProjectsExcluded = true, projectEvaluation = false,
                    dependencies = "Runtime BCL metadata only; no NuGet, ProjectReference, MSBuild evaluation, implicit SDK usings, generators or analyzers." },
                scannedFiles = sources.Count, scannedBytes = bytesRead, visitedNodes, bclReferences = referencePaths.Length,
                inventory.Source, inventory.SkippedEntries, inventory.Warning,
                limits = new { files = SemanticFileLimit, fileBytes = MaxFileBytes, totalBytes = SemanticByteLimit, nodesAndTokens = SemanticNodeLimit,
                    syntaxDepth = 128, results = SemanticResultLimit, responseCharacters = MaxResponseCharacters, seconds = 20 },
                hint = "Binding and diagnostics apply to this bounded source compilation, not a full project build or language server. References exclude declarations and use exact Roslyn symbol identity."
            }, full);
        }, ct);
    }

    private static ISymbol? SemanticSymbol(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        if (node is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier, ct) is { } alias) return alias.Target;
        // GetSymbolInfo returns null for ambiguous overloads; never pick a CandidateSymbol arbitrarily.
        return model.GetDeclaredSymbol(node, ct) ?? model.GetSymbolInfo(node, ct).Symbol;
    }

    private static ISymbol? SemanticCanonical(ISymbol? symbol)
    {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol method) symbol = (method.ReducedFrom ?? method).PartialDefinitionPart ?? method.ReducedFrom ?? method;
        return symbol?.OriginalDefinition;
    }

    private static SemanticLocation SemanticAt(Location location, string kind, string? code = null, string? message = null, string? severity = null)
    {
        if (!location.IsInSource) return new("", 0, 0, 0, 0, 0, kind, code, message, severity);
        var span = location.GetLineSpan();
        return new(location.SourceTree!.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1, location.SourceSpan.Length, kind, code, message, severity);
    }

    private static string SemanticClip(string value, int limit) => value.Length <= limit ? value : value[..SafeTextEnd(value, 0, limit)] + "…";
    private static string SemanticRelative(string workspace, string full)
    {
        var relative = Path.GetRelativePath(workspace, full).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? "" : relative;
    }

    private sealed class CSharpSemanticTool(WorkspaceService service) : IAgentTool, IWorkspacePathTool
    {
        public ToolDefinition Definition { get; } = new("csharp_semantic",
            "Resolve C# definitions, exact-symbol references or compiler diagnostics with Roslyn. Uses bounded source files under the nearest .csproj directory, excluding nested projects and ignored files. Does not evaluate projects or load NuGet/ProjectReference/generators. line/column are one-based UTF-16. Continue with expectedSnapshot and nextOffset. Inspect scope limitations; this is not a full project build or LSP.",
            """
            {"type":"object","properties":{"action":{"type":"string","enum":["definition","references","diagnostics"]},"path":{"type":"string","description":"Workspace-relative C# source file."},"line":{"type":"integer","minimum":1},"column":{"type":"integer","minimum":1},"offset":{"type":"integer","minimum":0,"maximum":100000},"limit":{"type":"integer","minimum":1,"maximum":1000},"expectedSnapshot":{"type":"string"}},"required":["action","path"],"additionalProperties":false}
            """);

        public IReadOnlyList<string> GetPaths(ToolCall call, string workspace)
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var path = Text(document.RootElement, "path") ?? throw new ArgumentException("path is required.");
            return [SemanticRelative(Root(workspace), Resolve(workspace, path))];
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var args = document.RootElement;
                return await service.CSharpSemanticAsync(context.WorkspacePath, Text(args, "action") ?? throw new ArgumentException("action is required."),
                    Text(args, "path") ?? throw new ArgumentException("path is required."), Number(args, "line", 1), Number(args, "column", 1),
                    Number(args, "offset", 0), Number(args, "limit", 100), Text(args, "expectedSnapshot"), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return new(ex.Message, true); }
        }

        private static string? Text(JsonElement args, string name) => args.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException(name + " must be a string.") : null;
        private static int Number(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var value)
            ? value.TryGetInt32(out var number) ? number : throw new ArgumentException(name + " must be an integer.") : fallback;
    }
}
