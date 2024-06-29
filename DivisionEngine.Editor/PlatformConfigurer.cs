using DivisionEngine.Graphics;

namespace DivisionEngine.Editor;

/// <summary>
///     Platform-dependent configuration methods.
/// </summary>
internal partial interface IPlatformConfigurer
{
    /// <summary>
    ///     Get the configurer instance of the current platform.
    /// </summary>
    /// <returns></returns>
    public static partial IPlatformConfigurer GetInstance();

    IGraphicsBackend CreateGraphicsBackend(IServiceProvider serviceProvider);
    ICoreLoopRunner CreateCoreLoopRunner(IServiceProvider serviceProvider);
}