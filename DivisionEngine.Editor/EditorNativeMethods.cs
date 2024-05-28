using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DivisionEngine.Editor;
internal static partial class EditorNativeMethods
{

    [LibraryImport("DivisionEngine.Editor.Native.dll")]
    public static partial uint SetSwapChain(IntPtr swapChainPanel, IntPtr swapChain);
}
