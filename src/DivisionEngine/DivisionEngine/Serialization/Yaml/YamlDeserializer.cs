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

    /// <summary>
    ///     A key that was read but does not belong to the field being asked for. Both sides walk field
    ///     ids in ascending order, so a key larger than the one wanted belongs to a later field and has
    ///     to wait rather than be consumed.
    /// </summary>
    private int? _pendingId;

    private bool TryReadNextId(out int id)
    {
        if (_pendingId is { } pending)
        {
            _pendingId = null;
            id = pending;
            return true;
        }

        if (_parser.CurrentEventType == ParseEventType.Scalar)
        {
            id = int.Parse(_parser.ReadScalarAsString() ?? throw new InvalidOperationException());
            return true;
        }

        id = 0;
        return false;
    }

    /// <summary>
    ///     Positions the parser on the value of field <paramref name="id" />, or reports that this
    ///     document does not carry it.
    ///     <para>
    ///         Written and requested ids both ascend, so a mismatch says which way the two schemas
    ///         differ. A smaller key is a field the reader no longer has — it gets dropped and the
    ///         search continues. A larger key is a field the writer did not have — the reader takes the
    ///         default and the key is held back for whichever field claims it. Without this, one added
    ///         or removed field would swallow every field after it, and since ids are hashes of field
    ///         names an author has no way to tell which of their edits are safe.
    ///     </para>
    /// </summary>
    private bool TryRead(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if ((_modes?.TryPeek(out var mode) ?? false) && mode is YamlSerializationModeKind.Sequence)
        {
            return true;
        }

        while (true)
        {
            if (!TryReadNextId(out var nextId))
            {
                return false;
            }

            if (nextId == id)
            {
                return true;
            }

            if (nextId > id)
            {
                _pendingId = nextId;
                return false;
            }

            _parser.SkipCurrentNode();
        }
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

    public byte[]? RawNode(int id, ReadOnlySpan<byte> hintUtf8)
    {
        return TryRead(id, hintUtf8) ? YamlNodeCopy.Capture(ref _parser) : null;
    }

    /// <summary>
    ///     A deserializer over a node captured by <see cref="RawNode" />. The next field read, whatever
    ///     its id, reads that node: framed like an element of a sequence, which carries no keys.
    /// </summary>
    public static YamlDeserializer OverNode(ReadOnlyMemory<byte> node, ISerializedObjectResolver? resolver,
        ITypeResolver? typeResolver = null)
    {
        var deserializer = new YamlDeserializer(YamlNodeCopy.Open(node), resolver, typeResolver);
        deserializer._modes = new Stack<YamlSerializationModeKind>();
        deserializer._modes.Push(YamlSerializationModeKind.Sequence);
        return deserializer;
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

        // A key held back for a field that never asked for it belongs to this mapping, not the parent.
        _pendingId = null;
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