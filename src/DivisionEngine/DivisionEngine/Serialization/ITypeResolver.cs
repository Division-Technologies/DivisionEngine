namespace DivisionEngine;

/// <summary>
///     Resolves a serialized type ID to a runtime <see cref="System.Type" /> during
///     deserialization. The ID is normally a GUID in canonical "N" format (the
///     <see cref="TypeIdAttribute" /> value, or the hash of the type name); documents written
///     before GUID type IDs pass the legacy <see cref="System.Type.FullName" /> instead.
///     <para>
///         The default behaviour reads the <see cref="SerializedTypeRegistrationAttribute" />s of
///         loaded non-collectible assemblies. A custom resolver is supplied during script
///         hot-reload so that user types resolve against the active (collectible) user
///         <c>AssemblyLoadContext</c> rather than a stale, soon-to-be-unloaded one.
///     </para>
/// </summary>
public interface ITypeResolver
{
    Type? Resolve(string typeId);
}