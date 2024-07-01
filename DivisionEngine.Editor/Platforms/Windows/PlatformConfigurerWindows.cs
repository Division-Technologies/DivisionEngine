using DivisionEngine.Editor.Windows;
using DivisionEngine.Graphics;

namespace DivisionEngine.Editor
{
    internal partial interface IPlatformConfigurer
    {
        public static partial IPlatformConfigurer GetInstance()
        {
            return PlatformConfigurerWindows.instance;
        }
    }
}

namespace DivisionEngine.Editor.Windows
{
    internal class PlatformConfigurerWindows : IPlatformConfigurer
    {
        public static readonly PlatformConfigurerWindows instance = new();

        public ICoreLoopRunner CreateCoreLoopRunner(IServiceProvider serviceProvider)
        {
            return new CoreLoopRunnerWindows(serviceProvider.GetService<CoreLoop>() ??
                                             throw new InvalidOperationException(
                                                 "Failed to resolve CoreLoop instance."));
        }

        public IGraphicsBackend CreateGraphicsBackend(IServiceProvider serviceProvider)
        {
            return new D3D12Backend();
        }
    }
}