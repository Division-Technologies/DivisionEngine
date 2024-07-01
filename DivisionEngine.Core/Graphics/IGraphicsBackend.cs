namespace DivisionEngine.Graphics;

public interface IGraphicsBackend : IDisposable
{
    void Initialize();

    void Render();
}