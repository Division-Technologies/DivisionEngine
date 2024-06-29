using Microsoft.UI.Xaml.Media;

namespace DivisionEngine.Editor.Windows;

internal class CoreLoopRunnerWindows : ICoreLoopRunner
{
    public CoreLoopRunnerWindows(CoreLoop coreLoop)
    {
        CompositionTarget.Rendering += (sender, e) => { coreLoop.Execute(); };
    }
}