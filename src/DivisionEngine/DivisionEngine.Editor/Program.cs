using ConsoleAppFramework;
using DivisionEngine;
using DivisionEngine.Authoring.Assets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder();


var app = builder.ToConsoleAppBuilder();
app.Add<Commands>();
await app.RunAsync(args);

internal class Commands
{
    /// <summary>Opens a project: sets up the asset database, imports the project's assets, then runs the engine.</summary>
    /// <param name="projectPath">Path to the project folder (contains the Assets and Library folders).</param>
    [Command("")]
    public async Task Root(string projectPath, [FromServices] ILogger<Program> logger, CancellationToken ct)
    {
        var assetsRoot = Path.Combine(projectPath, "Assets");
        var cacheDirectory = Path.Combine(projectPath, "Library", "AssetCache");
        Directory.CreateDirectory(assetsRoot);

        using var assets = new AssetDatabase([assetsRoot], cacheDirectory);
        logger.LogInformation("Importing assets under {Root}", assetsRoot);
        assets.Refresh();
        logger.LogInformation("{Count} asset(s) ready; watching {Root} for changes.",
            assets.AllAssets.Count, assetsRoot);
        assets.StartWatching();

        var engine = new Engine(logger);
        // Apply pending asset changes (queued by the watcher) once per frame, on the engine thread.
        engine.AddSystem(new AssetRefreshSystem(assets));
        engine.Main();
    }
}