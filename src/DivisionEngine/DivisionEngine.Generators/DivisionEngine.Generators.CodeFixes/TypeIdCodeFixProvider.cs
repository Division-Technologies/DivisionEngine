using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DivisionEngine.Generators.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace DivisionEngine.Generators.CodeFixes;

/// <summary>
///     Fix for DIVSER005: inserts [TypeId("...")] with the GUID computed from the class's current
///     full name — i.e. the ID the class already serializes with — so a subsequent rename keeps
///     persisted data readable.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TypeIdCodeFixProvider))]
[Shared]
public sealed class TypeIdCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("DIVSER005");

    public override FixAllProvider GetFixAllProvider()
    {
        return WellKnownFixAllProviders.BatchFixer;
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var declaration = root.FindToken(diagnostic.Location.SourceSpan.Start)
                .Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (declaration is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Pin serialized type ID with [TypeId]",
                    ct => AddTypeIdAsync(context.Document, declaration, ct),
                    nameof(TypeIdCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> AddTypeIdAsync(
        Document document, ClassDeclarationSyntax declaration, CancellationToken ct)
    {
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel?.GetDeclaredSymbol(declaration, ct) is not { } symbol)
        {
            return document;
        }

        var id = SerializedTypeGuid.ComputeDefault(SerializedTypeGuid.MetadataFullName(symbol)).ToString("N");

        var attributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                        SyntaxFactory.IdentifierName("TypeId"),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal(id))))))))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        return document.WithSyntaxRoot(
            root.ReplaceNode(declaration, declaration.AddAttributeLists(attributeList)));
    }
}