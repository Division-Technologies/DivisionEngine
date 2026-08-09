namespace DivisionEngine;

public interface ITimeProvider
{
    bool DoUpdate(Realtime realtime, out Time time);
}