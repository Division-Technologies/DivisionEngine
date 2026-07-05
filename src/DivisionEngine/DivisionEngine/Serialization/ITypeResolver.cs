namespace DivisionEngine;

/// <summary>
///     Resolves a serialized type name (<see cref="System.Type.FullName" />) to a runtime
///     <see cref="System.Type" /> during deserialization.
///     <para>
///         The default behaviour scans the loaded assemblies in the current <c>AppDomain</c>. A custom
///         resolver is supplied during script hot-reload so that user types resolve against the active
///         (collectible) user <c>AssemblyLoadContext</c> rather than a stale, soon-to-be-unloaded one.
///     </para>
/// </summary>
public interface ITypeResolver
{
    Type? Resolve(string fullName);
}
