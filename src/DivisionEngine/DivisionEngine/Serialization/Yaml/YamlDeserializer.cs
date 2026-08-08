using System.Diagnostics.CodeAnalysis;
using System.Text;
using VYaml.Parser;

namespace DivisionEngine;

// Mutable ref struct — always pass by ref. See the note on YamlSerializer.
internal ref struct YamlDeserializer(
    YamlParser parser,
    ISerializedObjectResolver? resolver,
    ITypeResolver? typeResolver = null) : IContainerDeserializer
{
    private YamlParser _parser = parser;
    private Stack<YamlSerializationModeKind>? _modes;

    private bool TryReadNextId(out int id)
    {
        if (_parser.CurrentEventType == ParseEventType.Scalar)
        {
            id = int.Parse(_parser.ReadScalarAsString() ?? throw new InvalidOperationException());
            return true;
        }

        id = 0;
        return false;
    }

    private bool TryRead(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if ((_modes?.TryPeek(out var mode) ?? false) && mode is YamlSerializationModeKind.Sequence)
        {
            return true;
        }

        if (!TryReadNextId(out var nextId))
        {
            return false;
        }

        if (nextId != id)
        {
            Console.WriteLine($"Failed to read {id} ({hintUtf8.ToString()})");
            _parser.SkipCurrentNode();
            return false;
        }

        return true;
    }

    public bool Bool(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return _parser.ReadScalarAsBool();
    }

    public sbyte I8(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return (sbyte)_parser.ReadScalarAsInt32();
    }

    public short I16(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return (short)_parser.ReadScalarAsInt32();
    }

    public int I32(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return _parser.ReadScalarAsInt32();
    }

    public long I64(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return _parser.ReadScalarAsInt64();
    }

    public float F32(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return _parser.ReadScalarAsFloat();
    }

    public double F64(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return default;
        }

        return _parser.ReadScalarAsDouble();
    }

    public byte[] Blob(int id, ReadOnlySpan<byte> hintUtf8, out BlobKind kind)
    {
        if (!TryBeginStruct(id, hintUtf8))
        {
            kind = default;
            return [];
        }

        kind = (BlobKind)I32(0, "kind"u8);

        if (!TryRead(1, "value"u8))
        {
            kind = default;
            EndStruct();
            return [];
        }

        var value = _parser.ReadScalarAsString() ?? "";

        EndStruct();

        return kind switch
        {
            BlobKind.ByteArray => Convert.FromBase64String(value),
            BlobKind.Utf8 => Encoding.UTF8.GetBytes(value),
            BlobKind.Utf16 => Encoding.Unicode.GetBytes(value),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public ISerializableObject? ObjectReference(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (resolver is null)
        {
            throw new InvalidOperationException();
        }

        if (!TryBeginStruct(id, hintUtf8))
        {
            return null;
        }

        if (!TryRead(0, "scope"u8))
        {
            EndStruct();
            return null;
        }

        var scope = _parser.ReadScalarAsString() ?? "";

        var localId = new LocalId(I32(1, "id"u8));

        EndStruct();

        return resolver.Resolve(new GlobalId(new ScopeId(Guid.Parse(scope)), localId));
    }

    public bool TryBeginArray(int id, ReadOnlySpan<byte> hintUtf8, out int length)
    {
        if (!TryBeginStruct(id, hintUtf8))
        {
            length = 0;
            return false;
        }

        length = I32(0, "length"u8);

        if (!TryRead(1, "elements"u8) || _parser.CurrentEventType != ParseEventType.SequenceStart)
        {
            EndStruct();
            return false;
        }

        _parser.Read(); // enter the sequence
        _modes ??= new Stack<YamlSerializationModeKind>();
        _modes.Push(YamlSerializationModeKind.Sequence);
        return true;
    }

    public void EndArray()
    {
        if (_modes?.Peek() is not YamlSerializationModeKind.Sequence)
        {
            throw new InvalidOperationException();
        }

        _modes.Pop();
        while (_parser.CurrentEventType != ParseEventType.SequenceEnd)
        {
            _parser.SkipCurrentNode();
        }

        _parser.Read();

        // TryBeginArray framed the sequence inside a {length, elements} mapping — close it too
        EndStruct();
    }

    public bool TryBeginStruct(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if (!TryRead(id, hintUtf8))
        {
            return false;
        }

        if (_parser.CurrentEventType is not ParseEventType.MappingStart)
        {
            _parser.SkipCurrentNode();
            return false;
        }

        _parser.Read();

        _modes ??= new Stack<YamlSerializationModeKind>();
        _modes.Push(YamlSerializationModeKind.Mapping);
        return true;
    }

    public void EndStruct()
    {
        if (_modes?.Peek() is not YamlSerializationModeKind.Mapping)
        {
            throw new InvalidOperationException();
        }

        _modes.Pop();
        while (_parser.CurrentEventType != ParseEventType.MappingEnd)
        {
            _parser.SkipCurrentNode();
        }

        _parser.Read();
    }

    /// <summary>
    ///     Advances the parser past stream/document framing events to the next content node.
    /// </summary>
    private bool SkipToContent()
    {
        while (true)
        {
            switch (_parser.CurrentEventType)
            {
                case ParseEventType.Nothing:
                case ParseEventType.StreamStart:
                case ParseEventType.DocumentStart:
                case ParseEventType.DocumentEnd:
                    if (!_parser.Read())
                    {
                        return false;
                    }

                    continue;
                case ParseEventType.StreamEnd:
                    return false;
                default:
                    return true;
            }
        }
    }

    public bool TryBeginObject(out LocalId id, [NotNullWhen(true)] out Type? type)
    {
        if (!SkipToContent())
        {
            id = default;
            type = null;
            return false;
        }

        _modes ??= new Stack<YamlSerializationModeKind>();
        _modes.Push(YamlSerializationModeKind.Sequence);
        if (!TryBeginStruct(0, []))
        {
            id = default;
            type = null;
            _modes.Pop();
            return false;
        }

        id = new LocalId(I32(0, "id"u8));
        type = TryRead(1, "type"u8)
            ? ResolveType(_parser.ReadScalarAsString() ?? throw new InvalidOperationException(), typeResolver)
            : null;
        if (type == null || !TryBeginStruct(2, "value"u8))
        {
            EndStruct();
            _modes.Pop();
            id = default;
            type = null;
            return false;
        }

        return true;
    }

    public void EndObject()
    {
        EndStruct();
        EndStruct();
        _modes!.Pop();
    }

    /// <summary>
    ///     Objects are framed with a stable serialized type ID: the [TypeId] GUID, or an MD5 hash
    ///     of Type.FullName for types without one. IDs resolve through the generator-emitted
    ///     [SerializedTypeRegistration] assembly attributes. Documents written before GUID type IDs
    ///     contain the type's FullName instead; those fall back to name-based resolution.
    /// </summary>
    private static Type? ResolveType(string typeId, ITypeResolver? typeResolver)
    {
        // The custom resolver (script hot-reload) takes precedence so user types bind to the
        // active user AssemblyLoadContext rather than a stale one still present in the AppDomain.
        if (Guid.TryParse(typeId, out var guid))
        {
            var canonical = guid.ToString("N");
            if (typeResolver?.Resolve(canonical) is { } resolved)
            {
                return resolved;
            }

            return SerializedTypeRegistry.Resolve(canonical);
        }

        // Legacy name-based document.
        if (typeResolver?.Resolve(typeId) is { } legacyResolved)
        {
            return legacyResolved;
        }

        if (Type.GetType(typeId) is { } type)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            if (assembly.GetType(typeId) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}