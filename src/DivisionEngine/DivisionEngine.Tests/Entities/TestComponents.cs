using System.Runtime.InteropServices;

namespace DivisionEngine.Tests.Entities;

public struct Position
{
    public float X, Y, Z;

    public Position(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct Velocity
{
    public float X, Y, Z;

    public Velocity(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct Health
{
    public int Value;
}

/// <summary>Empty struct: a tag component with no storage.</summary>
public struct Frozen
{
}

/// <summary>A class component: stored by reference.</summary>
public sealed class Name
{
    public string Value = "";
}

[StructLayout(LayoutKind.Sequential, Size = 20_000)]
public struct Huge
{
    public byte First;
}
