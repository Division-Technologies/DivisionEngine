#include "pch.h"
#include <cstdint>
#include "window.h"
#include "d3d12_entrypoint.h"

BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD ul_reason_for_call,
                      LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

DIVISION_EXPORT int32_t Add(int32_t a, int32_t b)
{
    return a + b;
}


DIVISION_EXPORT void TestMain()
{
	auto hwnd = InitWindow(TEXT("aaa"), 1280, 720);

    InitD3D12(hwnd, 1280, 720);

	MSG msg = {};
	while (WM_QUIT != msg.message)
	{
		if (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE == TRUE))
		{
			TranslateMessage(&msg);
			DispatchMessage(&msg);
		}
		else
		{
            if(Render() != S_OK)
            {
                return;
            }
		}
	}
}