using ConsoleAppFramework;
using DivisionEngine;

Console.WriteLine();
Console.Write(">");
var app = ConsoleApp.Create();
app.Add<Commands>();
await app.RunAsync(args);

internal class Commands
{
    [Command("")]
    public void Root(string projectPath)
    {

        var engine = new Engine();
        engine.Main();
    }
}