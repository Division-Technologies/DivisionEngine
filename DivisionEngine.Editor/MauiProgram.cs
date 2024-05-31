using DivisionEngine.Graphics;
using Microsoft.Extensions.Logging;

namespace DivisionEngine.Editor
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var backend = GraphicsBackend.CreateBackend();
            backend.Initialize();

            Task.Run(() =>
            {
                while (true)
                {
                    backend.Render();
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

            builder.Services.AddSingleton(typeof(GraphicsBackend), backend);

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
