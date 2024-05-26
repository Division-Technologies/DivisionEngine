using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DivisionEngine.Core;
internal static partial class NativeMethods
{
    [LibraryImport("DivisionEngine.Native.dll")]
    public static partial int Add(int a, int b);
}
