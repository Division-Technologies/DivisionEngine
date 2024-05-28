using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DivisionEngine;

internal static partial class NativeMethods
{
    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial int Add(int a, int b);

    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial void TestMain();


    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial IntPtr GetSwapChain();

    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial uint InitD3D12(IntPtr hwnd, uint width, uint height);


    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial uint Render();

}
