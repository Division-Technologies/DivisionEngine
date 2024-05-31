using System.Runtime.InteropServices;

namespace DivisionEngine.Editor;

internal static partial class EditorNativeMethods
{
    [LibraryImport("DivisionEngine.Editor.Native.dll")]
    public static partial uint SetSwapChain(IntPtr swapChainPanel, IntPtr swapChain);
}