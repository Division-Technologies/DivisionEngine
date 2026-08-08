using System.Numerics;

namespace DivisionEngine;

[CustomFormatter(typeof(Vector2))]
internal readonly struct Vector2Formatter : IValueFormatter<Vector2>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Vector2 value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.F32(0, "x"u8, value.X);
        s.F32(1, "y"u8, value.Y);
        s.EndStruct();
    }

    public Vector2 Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint))
        {
            return default;
        }

        var v = new Vector2(d.F32(0, "x"u8), d.F32(1, "y"u8));
        d.EndStruct();
        return v;
    }
}

[CustomFormatter(typeof(Vector3))]
internal readonly struct Vector3Formatter : IValueFormatter<Vector3>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Vector3 value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.F32(0, "x"u8, value.X);
        s.F32(1, "y"u8, value.Y);
        s.F32(2, "z"u8, value.Z);
        s.EndStruct();
    }

    public Vector3 Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint))
        {
            return default;
        }

        var v = new Vector3(d.F32(0, "x"u8), d.F32(1, "y"u8), d.F32(2, "z"u8));
        d.EndStruct();
        return v;
    }
}

[CustomFormatter(typeof(Vector4))]
internal readonly struct Vector4Formatter : IValueFormatter<Vector4>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Vector4 value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.F32(0, "x"u8, value.X);
        s.F32(1, "y"u8, value.Y);
        s.F32(2, "z"u8, value.Z);
        s.F32(3, "w"u8, value.W);
        s.EndStruct();
    }

    public Vector4 Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint))
        {
            return default;
        }

        var v = new Vector4(d.F32(0, "x"u8), d.F32(1, "y"u8), d.F32(2, "z"u8), d.F32(3, "w"u8));
        d.EndStruct();
        return v;
    }
}

[CustomFormatter(typeof(Quaternion))]
internal readonly struct QuaternionFormatter : IValueFormatter<Quaternion>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Quaternion value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.F32(0, "x"u8, value.X);
        s.F32(1, "y"u8, value.Y);
        s.F32(2, "z"u8, value.Z);
        s.F32(3, "w"u8, value.W);
        s.EndStruct();
    }

    public Quaternion Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint))
        {
            return default;
        }

        var v = new Quaternion(d.F32(0, "x"u8), d.F32(1, "y"u8), d.F32(2, "z"u8), d.F32(3, "w"u8));
        d.EndStruct();
        return v;
    }
}

[CustomFormatter(typeof(Matrix4x4))]
internal readonly struct Matrix4x4Formatter : IValueFormatter<Matrix4x4>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Matrix4x4 value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.F32(0, "m11"u8, value.M11);
        s.F32(1, "m12"u8, value.M12);
        s.F32(2, "m13"u8, value.M13);
        s.F32(3, "m14"u8, value.M14);
        s.F32(4, "m21"u8, value.M21);
        s.F32(5, "m22"u8, value.M22);
        s.F32(6, "m23"u8, value.M23);
        s.F32(7, "m24"u8, value.M24);
        s.F32(8, "m31"u8, value.M31);
        s.F32(9, "m32"u8, value.M32);
        s.F32(10, "m33"u8, value.M33);
        s.F32(11, "m34"u8, value.M34);
        s.F32(12, "m41"u8, value.M41);
        s.F32(13, "m42"u8, value.M42);
        s.F32(14, "m43"u8, value.M43);
        s.F32(15, "m44"u8, value.M44);
        s.EndStruct();
    }

    public Matrix4x4 Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint))
        {
            return default;
        }

        var m = new Matrix4x4(
            d.F32(0, "m11"u8), d.F32(1, "m12"u8), d.F32(2, "m13"u8), d.F32(3, "m14"u8),
            d.F32(4, "m21"u8), d.F32(5, "m22"u8), d.F32(6, "m23"u8), d.F32(7, "m24"u8),
            d.F32(8, "m31"u8), d.F32(9, "m32"u8), d.F32(10, "m33"u8), d.F32(11, "m34"u8),
            d.F32(12, "m41"u8), d.F32(13, "m42"u8), d.F32(14, "m43"u8), d.F32(15, "m44"u8));
        d.EndStruct();
        return m;
    }
}