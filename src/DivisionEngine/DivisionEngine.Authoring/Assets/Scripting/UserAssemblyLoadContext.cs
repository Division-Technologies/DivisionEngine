using System.Reflection;
using System.Runtime.Loader;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     The collectible load context for user (script) assemblies. Shared assemblies — the engine,
///     the authoring library, and the BCL — are deliberately NOT loaded here: <see cref="Load" />
///     returns null so they resolve from the default context, keeping a single type identity for
///     types like <c>Component</c> and <c>ISerializableObject</c>. Only the compiled user DLLs live
///     in this context, so unloading it (on reload) releases just the user code.
/// </summary>
public sealed class UserAssemblyLoadContext() : AssemblyLoadContext("DivisionUser", true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
