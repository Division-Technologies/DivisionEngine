using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DivisionEngine.Generators.Tests;

/// <summary>
///     Compiles a snippet against the engine and runs one analyzer over it.
///     <para>
///         Hand-rolled rather than built on the analyzer-testing packages, because all these tests
///         need is "which diagnostics came out of this source", and a compilation plus
///         <see cref="CompilationWithAnalyzers" /> gives that without pinning a second Roslyn version.
///     </para>
/// </summary>
internal static class AnalyzerHarness
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trusted
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(ComponentAttribute).Assembly.Location))
            .ToArray();
    }

    /// <summary>The ids reported for <paramref name="source" />, in the order they appear in the file.</summary>
    public static string[] Diagnose(DiagnosticAnalyzer analyzer, string source)
    {
        return Run(analyzer, source).Select(d => d.Id).ToArray();
    }

    public static Diagnostic[] Run(DiagnosticAnalyzer analyzer, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A snippet that does not compile would make any diagnostic meaningless.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.That(errors, Is.Empty, "the analyzed snippet must compile:\n" + string.Join("\n", errors.AsEnumerable()));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToArray();
    }
}
