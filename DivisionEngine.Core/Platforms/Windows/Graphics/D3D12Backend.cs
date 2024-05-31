#if WINDOWS

using System.Runtime.InteropServices;

namespace DivisionEngine.Graphics
{
    internal partial class D3D12Backend : GraphicsBackend
    {
        private int disposeLock;

        private IntPtr ptr;

        public D3D12Backend()
        {
            ptr = D3D12Backend_New();
        }

        [LibraryImport(NativeMethods.NativeLibrary)]
        private static partial IntPtr D3D12Backend_New();

        [LibraryImport(NativeMethods.NativeLibrary)]
        private static partial void D3D12Backend_Delete(IntPtr ptr);

        [LibraryImport(NativeMethods.NativeLibrary)]
        private static partial void D3D12Backend_Init(IntPtr ptr, IntPtr hwnd, uint width, uint height);

        [LibraryImport(NativeMethods.NativeLibrary)]
        private static partial void D3D12Backend_Render(IntPtr ptr);

        [LibraryImport(NativeMethods.NativeLibrary)]
        private static partial IntPtr D3D12Backend_GetSwapChain(IntPtr ptr);

        private void DisposeCore()
        {
            if (Interlocked.CompareExchange(ref disposeLock, 1, 0) != 0) return;

            D3D12Backend_Delete(ptr);
            ptr = IntPtr.Zero;
        }

        public override void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        ~D3D12Backend()
        {
            DisposeCore();
        }

        public override void Initialize()
        {
            D3D12Backend_Init(ptr, IntPtr.Zero, 800, 600);
        }

        public override void Render()
        {
            D3D12Backend_Render(ptr);
        }

        public IntPtr GetSwapChain()
        {
            return D3D12Backend_GetSwapChain(ptr);
        }
    }
}

#endif