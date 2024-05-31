#if WINDOWS
using System.Runtime.InteropServices;

namespace DivisionEngine;

partial class NativeMethods
{
    public const string NativeLibrary = "DivisionEngine.Native.dll";
}
#endif