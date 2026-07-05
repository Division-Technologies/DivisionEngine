using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Imports a C# project (<c>.csproj</c>) by compiling it to a DLL. The project's source files are
///     recorded as import dependencies so any edit re-triggers the import (and, in turn, a reload).
///     <para>
///         Uses <see cref="MSBuildWorkspace" />, which in recent versions runs MSBuild in an
///         out-of-process BuildHost and therefore does not require <c>MSBuildLocator</c>. The compiled
///         DLL is written to the asset's artifact directory; <see cref="CompiledAssembly" /> records its
///         path and the compiler diagnostics.
///     </para>
/// </summary>
[AssetImporter(".csproj")]
public sealed class CSharpProjectImporter : IAssetImporter
{
    public void Import(AssetImportContext context)
    {
        // Run off the engine's synchronization context to avoid deadlocking the blocking wait.
        var compiled = Task.Run(() => CompileAsync(context)).GetAwaiter().GetResult();
        context.SetMainObject(compiled);
    }

    // This importer has no persisted settings; provide empty serialization (no [AutoSerialization]).
    public void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
    }

    public void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
    }

    private static async Task<CompiledAssembly> CompileAsync(AssetImportContext context)
    {
        var compiled = new CompiledAssembly();

        using var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(context.SourcePath).ConfigureAwait(false);

        // Record every source file as an input dependency so edits re-trigger import.
        foreach (var document in project.Documents)
            if (document.FilePath is { } filePath)
                context.DependsOnFile(filePath);

        var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
        if (compilation is null)
        {
            compiled.Diagnostics.Add("error: failed to obtain a compilation for the project.");
            return compiled;
        }

        compiled.AssemblyName = compilation.AssemblyName ?? "";

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        compiled.Success = result.Success;
        foreach (var diagnostic in result.Diagnostics)
            if (diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                compiled.Diagnostics.Add(diagnostic.ToString());

        if (result.Success)
        {
            var dllName = (compiled.AssemblyName.Length > 0 ? compiled.AssemblyName : "user") + ".dll";
            compiled.DllPath = context.WriteArtifact(dllName, stream.ToArray());
        }

        return compiled;
    }
}