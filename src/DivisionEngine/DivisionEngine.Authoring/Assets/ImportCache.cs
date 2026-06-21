using System.IO.Hashing;
using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     On-disk cache of imported asset data. Each asset's produced objects are stored as one scope
///     file keyed by the asset GUID, so subsequent loads can skip re-running the importer when the
///     source and importer are unchanged.
/// </summary>
internal sealed class ImportCache(string directory)
{
    public string PathFor(ScopeId guid) => Path.Combine(directory, guid.ToString() + ".cache");

    public bool Exists(ScopeId guid) => File.Exists(PathFor(guid));

    public void Write(ScopeId guid, byte[] data)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathFor(guid), data);
    }

    /// <summary>Content hash used to detect source-file changes.</summary>
    public static long Hash(ReadOnlySpan<byte> data) => unchecked((long)XxHash64.HashToUInt64(data));
}
