using Microsoft.Extensions.Logging;

namespace DivisionEngine.Editor
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {

            NativeMethods.InitD3D12(0, 600, 400);

            Task.Run(() =>
            {
                while (true)
                {
                    NativeMethods.Render();
                }
            });

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler(typeof(NativeGraphicsPanel), typeof(NativeGraphicsPanelHandler));
                })
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
