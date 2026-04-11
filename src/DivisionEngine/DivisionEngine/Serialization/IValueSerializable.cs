namespace DivisionEngine;

public interface IValueSerializable<T> where T : struct, IValueSerializable<T>
{
    static abstract IValueFormatter<T> Formatter { get; }
}