using System.Runtime.InteropServices;
using System.Text;
using VYaml.Emitter;

namespace DivisionEngine;

// Mutable ref struct — always pass by ref. By-value copies fork the emitter state and corrupt
// the output (ISerializable/IValueFormatter take serializers as ref parameters for this reason).
/// <param name="onUnscopedReference">
///     Called for each reference to an object that belongs to no scope, which is written as null.
/// </param>
internal ref struct YamlSerializer(
    Utf8YamlEmitter emitter,
    Action<ISerializableObject>? onUnscopedReference = null) : IContainerSerializer
{
    private readonly Action<ISerializableObject>? _onUnscopedReference = onUnscopedReference;
    private Utf8YamlEmitter _emitter = emitter;
    private Stack<YamlSerializationModeKind>? _modes;
    private bool _hasDocument;

    /// <summary>
    ///     A serializer whose next write, whatever its id, is the document's root node: a value on its
    ///     own, without the object framing, which <see cref="YamlDeserializer.OverNode" /> reads back.
    /// </summary>
    public static YamlSerializer ForNode(Utf8YamlEmitter emitter,
        Action<ISerializableObject>? onUnscopedReference = null)
    {
        var serializer = new YamlSerializer(emitter, onUnscopedReference);
        serializer._modes = new Stack<YamlSerializationModeKind>();
        serializer._modes.Push(YamlSerializationModeKind.Sequence);
        return serializer;
    }

    private void WriteKey(int id, ReadOnlySpan<byte> hintUtf8)
    {
        if ((_modes?.TryPeek(out var mode) ?? false) && mode == YamlSerializationModeKind.Sequence)
        {
            return;
        }

        // NOTE: hint comments cannot be emitted here — WriteRaw right after a pending key
        // produces `"key":# hint`, where '#' (not preceded by whitespace) is not a comment
        // and corrupts the document.
        _emitter.WriteString(id.ToString());
    }

    public void Bool(int id, ReadOnlySpan<byte> hintUtf8, bool value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteBool(value);
    }

    public void I8(int id, ReadOnlySpan<byte> hintUtf8, sbyte value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteInt32(value);
    }

    public void I16(int id, ReadOnlySpan<byte> hintUtf8, short value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteInt32(value);
    }

    public void I32(int id, ReadOnlySpan<byte> hintUtf8, int value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteInt32(value);
    }

    public void I64(int id, ReadOnlySpan<byte> hintUtf8, long value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteInt64(value);
    }

    public void F32(int id, ReadOnlySpan<byte> hintUtf8, float value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteFloat(value);
    }

    public void F64(int id, ReadOnlySpan<byte> hintUtf8, double value)
    {
        WriteKey(id, hintUtf8);
        _emitter.WriteDouble(value);
    }

    public void Blob(int id, ReadOnlySpan<byte> hintUtf8, scoped ReadOnlySpan<byte> value, BlobKind kind)
    {
        WriteKey(id, hintUtf8);
        _emitter.BeginMapping();
        _emitter.WriteString("0");
        _emitter.WriteInt32((int)kind);
        _emitter.WriteString("1");
        switch (kind)
        {
            case BlobKind.ByteArray:
            {
                // base64
                _emitter.WriteString(Convert.ToBase64String(value));
                break;
            }
            case BlobKind.Utf8:
            {
                _emitter.WriteString(Encoding.UTF8.GetString(value));
                break;
            }
            case BlobKind.Utf16:
            {
                if (value.IsEmpty)
                {
                    _emitter.WriteString("");
                }
                else
                {
                    _emitter.WriteString(
                        MemoryMarshal.Cast<byte, char>(MemoryMarshal.CreateReadOnlySpan(in value[0], value.Length)));
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        _emitter.EndMapping();
    }

    public void BeginArray(int id, ReadOnlySpan<byte> hintUtf8, int length)
    {
        WriteKey(id, hintUtf8);
        _modes ??= new Stack<YamlSerializationModeKind>();
        _modes.Push(YamlSerializationModeKind.Sequence);
        _emitter.BeginMapping();
        _emitter.WriteString("0");
        _emitter.WriteInt32(length);
        _emitter.WriteString("1");
        _emitter.BeginSequence();
    }

    public void EndArray()
    {
        _emitter.EndSequence();
        _emitter.EndMapping();
        _modes!.Pop();
    }

    public void BeginStruct(int id, ReadOnlySpan<byte> hintUtf8)
    {
        WriteKey(id, hintUtf8);
        _modes ??= new Stack<YamlSerializationModeKind>();
        _modes.Push(YamlSerializationModeKind.Mapping);
        _emitter.BeginMapping();
    }

    public void EndStruct()
    {
        _emitter.EndMapping();
        _modes!.Pop();
    }

    public void ObjectReference(int id, ReadOnlySpan<byte> hintUtf8, ISerializableObject? value)
    {
        // An object that belongs to no scope (created at run time rather than loaded from an asset)
        // has no identity that could be written. It is saved as a null reference, and whoever asked
        // for the write is told, so the loss is not silent.
        if (value is not null && value.Scope is null)
        {
            _onUnscopedReference?.Invoke(value);
            value = null;
        }

        WriteKey(id, hintUtf8);
        _emitter.BeginMapping();
        _emitter.WriteString("0");
        _emitter.WriteString(value?.Scope.Id.Value.ToString("N") ?? Guid.Empty.ToString("N"));
        _emitter.WriteString("1");
        _emitter.WriteInt32(value?.Id.Value ?? -1);
        _emitter.EndMapping();
    }

    public void RawNode(int id, ReadOnlySpan<byte> hintUtf8, ReadOnlyMemory<byte> node)
    {
        WriteKey(id, hintUtf8);
        var parser = YamlNodeCopy.Open(node);
        YamlNodeCopy.Copy(ref parser, ref _emitter);
    }

    public void BeginObject(LocalId id, Type type)
    {
        // Each object is its own YAML document — multiple root mappings in one document
        // are not valid YAML, so separate them with an explicit document marker.
        if (_hasDocument)
        {
            _emitter.WriteRaw("---"u8, false, true);
        }

        _hasDocument = true;

        _emitter.BeginMapping();
        _emitter.WriteString("0");
        _emitter.WriteInt32(id.Value);
        _emitter.WriteString("1");
        _emitter.WriteString(SerializedTypeId.Get(type));
        _emitter.WriteString("2");
        _emitter.BeginMapping();
    }

    public void EndObject()
    {
        _emitter.EndMapping();
        _emitter.EndMapping();
    }
}