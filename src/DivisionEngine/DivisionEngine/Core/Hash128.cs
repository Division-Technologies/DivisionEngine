using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DivisionEngine;

public readonly record struct Hash128(UInt128 Value) : IHashable
{
    private readonly UInt128 _value = Value;

    public UInt128 Value => _value;

    public ValueTask AppendAsync(ref HashBuilder builder)
    {
        builder.Append(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _value), 1)));
        return default;
    }

    public static Hash128 Hash(ReadOnlySpan<byte> data)
    {
        return new Hash128(XxHash128.HashToUInt128(data));
    }
}

public struct HashBuilder
{
    private NonCryptographicHashAlgorithm _algorithm;

    public void Append(ReadOnlySpan<byte> data)
    {
        (_algorithm ??= new XxHash128()).Append(data);
    }

    public Task AppendAsync(Stream stream)
    {
        return (_algorithm ??= new XxHash128()).AppendAsync(stream);
    }

    public Hash128 Hash()
    {
        if (_algorithm is null)
            return default;
        var hash = _algorithm.GetCurrentHash();
        if (hash.Length != 16)
            throw new InvalidOperationException("Unexpected hash length.");
        return new Hash128(Unsafe.As<byte, UInt128>(ref hash[0]));
    }
}

public interface IHashable
{
    ValueTask AppendAsync(ref HashBuilder builder);
}