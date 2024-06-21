using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DivisionEngine.Editor;

internal class CoreLoopRunnerWindows : ICoreLoopRunner
{
    public CoreLoopRunnerWindows(CoreLoop coreLoop)
    {
        CompositionTarget.Rendering += (sender, e) => { coreLoop.Execute(); };
    }
}