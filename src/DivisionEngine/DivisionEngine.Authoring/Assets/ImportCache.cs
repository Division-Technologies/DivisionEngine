using System.IO.Hashing;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     On-disk cache of imported asset data. Each asset's produced objects are stored as one scope
///     file keyed by the asset GUID, so subsequent loads can skip re-running the importer when the
///     source and importer are unchanged.
/// </summary>
internal sealed class ImportCache(string directory)
{
    public string PathFor(ScopeId guid)
    {
        return Path.Combine(directory, guid + ".cache");
    }

    /// <summary>Per-asset directory for build artifacts (e.g. a compiled DLL) keyed by GUID.</summary>
    public string ArtifactDirectory(ScopeId guid)
    {
        return Path.Combine(directory, guid + ".artifacts");
    }

    public bool Exists(ScopeId guid)
    {
        return File.Exists(PathFor(guid));
    }

    public void Write(ScopeId guid, byte[] data)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathFor(guid), data);
    }

    /// <summary>Content hash used to detect source-file changes.</summary>
    public static long Hash(ReadOnlySpan<byte> data)
    {
        return unchecked((long)XxHash64.HashToUInt64(data));
    }

    /// <summary>
    ///     Combined content hash of a source file plus any extra input files, in a deterministic order.
    ///     Used to detect changes to any input an importer consumed (e.g. the .cs files behind a .csproj).
    /// </summary>
    public static long CombinedHash(string sourcePath, IEnumerable<string> inputFiles)
    {
        var hasher = new XxHash64();
        Append(hasher, sourcePath);
        foreach (var file in inputFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Append(hasher, file);
        }

        return unchecked((long)hasher.GetCurrentHashAsUInt64());

        static void Append(XxHash64 hasher, string path)
        {
            if (File.Exists(path))
            {
                hasher.Append(File.ReadAllBytes(path));
            }
        }
    }
}