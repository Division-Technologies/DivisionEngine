using DivisionEngine.Editor.MacCatalyst;
using DivisionEngine.Graphics;

namespace DivisionEngine.Editor
{
    internal partial interface IPlatformConfigurer
    {
        public static partial IPlatformConfigurer GetInstance()
        {
            return PlatformConfigurerMacCatalyst.instance;
        }
    }
}

namespace DivisionEngine.Editor.MacCatalyst
{
    internal class PlatformConfigurerMacCatalyst : IPlatformConfigurer
    {
        public static readonly PlatformConfigurerMacCatalyst instance = new();

        public ICoreLoopRunner CreateCoreLoopRunner(IServiceProvider serviceProvider)
        {
            throw new NotImplementedException();
        }

        public IGraphicsBackend CreateGraphicsBackend(IServiceProvider serviceProvider)
        {
            throw new NotImplementedException();
        }
    }
}