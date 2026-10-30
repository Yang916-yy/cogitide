using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    public async Task<ToolResult> InspectSymbolsAsync(string workspace, string path, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        ct.ThrowIfCancellationRequested();
        var full = Resolve(workspace, path);
        if (!Path.GetExtension(full).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return new ToolResult("inspect_symbols currently supports C# .cs files only. Use read_file/search_files for other languages.", true);
        var bytes = await ReadBoundedAsync(full, ct);
        var text = SourceText.From(Decode(bytes));
        // Parse text only. Never load a project, run MSBuild, generators or analyzers from the repository.
        var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), path, ct);
        var root = await tree.GetRootAsync(ct);
        var found = new List<object>();
        var declarations = 0;
        var visited = 0;
        var truncated = false;
        var depthLimited = false;
        Stack<(SyntaxNode Node, int Depth)> pending = new();
        pending.Push((root, 0));
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > 20_000) { truncated = true; break; }
            if (entry.Depth > 64) { depthLimited = true; continue; }
            if (Declaration(entry.Node) is { } declaration)
            {
                if (declarations++ >= offset)
                {
                    if (found.Count >= limit) { truncated = true; break; }
                    var span = tree.GetLineSpan(entry.Node.Span, ct);
                    var headerLength = Math.Min(600, Math.Max(0, declaration.HeaderEnd - entry.Node.SpanStart));
                    var signature = text.ToString(new TextSpan(entry.Node.SpanStart, headerLength));
                    signature = string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    found.Add(new { kind = declaration.Kind, name = declaration.Name, signature, signatureTruncated = declaration.HeaderEnd - entry.Node.SpanStart > 600,
                        startLine = span.StartLinePosition.Line + 1, endLine = span.EndLinePosition.Line + 1 });
                }
            }
            // Declaration outlines do not descend into method bodies/expressions: local syntax cannot swamp the map.
            IEnumerable<SyntaxNode> children = entry.Node switch
            {
                CompilationUnitSyntax compilation => compilation.Members,
                BaseNamespaceDeclarationSyntax ns => ns.Members,
                TypeDeclarationSyntax type => type.Members,
                EnumDeclarationSyntax enumeration => enumeration.Members,
                _ => []
            };
            foreach (var child in children.Reverse()) pending.Push((child, entry.Depth + 1));
        }
        var diagnostics = tree.GetDiagnostics(ct).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(21).ToArray();
        return BoundedPageResult(found, offset, truncated && !depthLimited && visited <= 20_000, (items, nextOffset, responseTruncated) => new
        {
            path, sha256 = Hash(bytes), language = "C#", parser = "Microsoft.CodeAnalysis.CSharp 5.9.0", semanticAnalysis = false,
            scope = "Namespace, type and member declarations only; no local declarations or project compilation.",
            symbols = items, offset, limit, nextOffset,
            truncated = truncated || depthLimited || responseTruncated, responseTruncated, depthLimited,
            syntaxErrors = diagnostics.Take(20).Select(diagnostic => new { code = diagnostic.Id, line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1, message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture) }),
            diagnosticsTruncated = diagnostics.Length > 20
        }, full);
    }

    private sealed record DeclarationInfo(string Kind, string Name, int HeaderEnd);
    private static DeclarationInfo? Declaration(SyntaxNode node)
    {
        int End(SyntaxToken token) => token.IsMissing || token.RawKind == 0 ? node.Span.End : token.SpanStart;
        return node switch
        {
            BaseNamespaceDeclarationSyntax ns => new("namespace", ns.Name.ToString(), ns.Name.Span.End),
            TypeDeclarationSyntax type => new(type.Keyword.ValueText, type.Identifier.ValueText, End(type.OpenBraceToken.RawKind != 0 ? type.OpenBraceToken : type.SemicolonToken)),
            EnumDeclarationSyntax enumeration => new("enum", enumeration.Identifier.ValueText, End(enumeration.OpenBraceToken)),
            EnumMemberDeclarationSyntax member => new("enum-member", member.Identifier.ValueText, member.Identifier.Span.End),
            DelegateDeclarationSyntax declaration => new("delegate", declaration.Identifier.ValueText, End(declaration.SemicolonToken)),
            MethodDeclarationSyntax method => new("method", method.Identifier.ValueText, method.Body?.SpanStart ?? method.ExpressionBody?.SpanStart ?? End(method.SemicolonToken)),
            ConstructorDeclarationSyntax constructor => new("constructor", constructor.Identifier.ValueText, constructor.Body?.SpanStart ?? constructor.ExpressionBody?.SpanStart ?? End(constructor.SemicolonToken)),
            DestructorDeclarationSyntax destructor => new("destructor", destructor.Identifier.ValueText, destructor.Body?.SpanStart ?? destructor.ExpressionBody?.SpanStart ?? End(destructor.SemicolonToken)),
            PropertyDeclarationSyntax property => new("property", property.Identifier.ValueText, property.AccessorList?.SpanStart ?? property.ExpressionBody?.SpanStart ?? property.Initializer?.SpanStart ?? End(property.SemicolonToken)),
            IndexerDeclarationSyntax indexer => new("indexer", "this", indexer.AccessorList?.SpanStart ?? indexer.ExpressionBody?.SpanStart ?? End(indexer.SemicolonToken)),
            EventDeclarationSyntax evt => new("event", evt.Identifier.ValueText, evt.AccessorList?.SpanStart ?? End(evt.SemicolonToken)),
            BaseFieldDeclarationSyntax field => new(field is EventFieldDeclarationSyntax ? "event-field" : "field", string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)), End(field.SemicolonToken)),
            OperatorDeclarationSyntax op => new("operator", op.OperatorToken.ValueText, op.Body?.SpanStart ?? op.ExpressionBody?.SpanStart ?? End(op.SemicolonToken)),
            ConversionOperatorDeclarationSyntax conversion => new("conversion", conversion.Type.ToString(), conversion.Body?.SpanStart ?? conversion.ExpressionBody?.SpanStart ?? End(conversion.SemicolonToken)),
            GlobalStatementSyntax statement => new("top-level", "statement", Math.Min(statement.Span.End, statement.SpanStart + 160)),
            _ => null
        };
    }
}
