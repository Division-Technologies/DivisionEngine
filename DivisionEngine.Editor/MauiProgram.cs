using Microsoft.Extensions.Logging;

namespace DivisionEngine.Editor;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
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

        var configurer = IPlatformConfigurer.GetInstance();

        builder.Services.AddSingleton(serviceProvider =>
        {
            var backend = configurer.CreateGraphicsBackend(serviceProvider);
            backend.Initialize();
            return backend;
        });
        builder.Services.AddSingleton(configurer.CreateCoreLoopRunner);
        builder.Services.AddSingleton<CoreLoop>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}