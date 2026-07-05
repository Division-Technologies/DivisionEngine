using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     The result of importing a C# project (.csproj): the path to the compiled DLL artifact, whether
///     compilation succeeded, and the compiler diagnostics. This is the main object of a compiled
///     script asset; the script host loads <see cref="DllPath" /> into the user assembly context.
/// </summary>
[AutoSerialization]
public sealed partial class CompiledAssembly : ISerializableObject
{
    [Serialize] public string AssemblyName = "";

    /// <summary>Absolute path to the compiled DLL in the project's import cache (machine-local).</summary>
    [Serialize] public string DllPath = "";
    [Serialize] public bool Success;

    /// <summary>Formatted compiler diagnostics (errors and warnings).</summary>
    [Serialize] public List<string> Diagnostics = new();

    public SerializationScope Scope { get; set; } = null!;
    public LocalId Id { get; set; }
}
