using ConsoleAppFramework;
using DivisionEngine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder();


var app = builder.ToConsoleAppBuilder();
app.Add<Commands>();
await app.RunAsync(args);

internal class Commands
{
    [Command("")]
    public async Task Root(string projectPath, [FromServices] ILogger<Program> logger, CancellationToken ct)
    {
        var engine = new Engine(logger);
        engine.Main();
    }
}