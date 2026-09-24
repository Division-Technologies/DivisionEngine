using System.Buffers;
using System.Text;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine;

/// <summary>
///     Copies one YAML node event by event from a parser to an emitter, without knowing its schema.
///     This is what lets data whose type is not loaded — a component whose script was deleted, a
///     field whose type changed — be carried through a load and written back unchanged.
///     <para>
///         The parser does not report how a scalar was quoted, so scalars are re-emitted by content: a
///         null stays null, anything that reads as a number or a boolean is written plain exactly as it
///         was, and everything else is written double-quoted. Readers in this codebase parse a scalar
///         according to the field they expect, not by its style, so that round-trips every value the
///         serializer writes.
///     </para>
/// </summary>
internal static class YamlNodeCopy
{
    /// <summary>Copies the node the parser is on, leaving the parser just after it.</summary>
    public static void Copy(ref YamlParser parser, ref Utf8YamlEmitter emitter)
    {
        switch (parser.CurrentEventType)
        {
            case ParseEventType.MappingStart:
                parser.Read();
                emitter.BeginMapping();
                while (parser.CurrentEventType != ParseEventType.MappingEnd)
                {
                    Copy(ref parser, ref emitter); // key
                    Copy(ref parser, ref emitter); // value
                }

                parser.Read();
                emitter.EndMapping();
                break;
            case ParseEventType.SequenceStart:
                parser.Read();
                emitter.BeginSequence();
                while (parser.CurrentEventType != ParseEventType.SequenceEnd)
                {
                    Copy(ref parser, ref emitter);
                }

                parser.Read();
                emitter.EndSequence();
                break;
            case ParseEventType.Scalar:
                CopyScalar(ref parser, ref emitter);
                parser.Read();
                break;
            default:
                throw new InvalidOperationException($"Cannot copy a YAML {parser.CurrentEventType} event.");
        }
    }

    private static void CopyScalar(ref YamlParser parser, ref Utf8YamlEmitter emitter)
    {
        if (parser.IsNullScalar())
        {
            emitter.WriteNull();
            return;
        }

        if (parser.TryGetScalarAsInt64(out _) || parser.TryGetScalarAsDouble(out _) ||
            parser.TryGetScalarAsBool(out _))
        {
            emitter.WriteScalar(parser.GetScalarAsUtf8());
            return;
        }

        emitter.WriteString(parser.GetScalarAsString() ?? "", ScalarStyle.DoubleQuoted);
    }

    /// <summary>Writes the node the parser is on as a standalone document.</summary>
    public static byte[] Capture(ref YamlParser parser)
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        Copy(ref parser, ref emitter);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>A parser positioned on the single node of a document written by <see cref="Capture" />.</summary>
    public static YamlParser Open(ReadOnlyMemory<byte> node)
    {
        var parser = new YamlParser(new ReadOnlySequence<byte>(node));
        while (parser.CurrentEventType is ParseEventType.Nothing or ParseEventType.StreamStart
               or ParseEventType.DocumentStart)
        {
            if (!parser.Read())
            {
                throw new InvalidOperationException(
                    $"Not a captured YAML node: {Encoding.UTF8.GetString(node.Span)}");
            }
        }

        return parser;
    }
}