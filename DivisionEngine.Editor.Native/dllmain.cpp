// dllmain.cpp : DLL アプリケーションのエントリ ポイントを定義します。
#include "pch.h"

#include <d3d12.h>
#include <dxgi1_6.h>

#include <microsoft.ui.xaml.media.dxinterop.h>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD ul_reason_for_call,
                      LPVOID lpReserved
)
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

using namespace winrt;
using namespace Microsoft::UI::Xaml::Controls;

DIVISION_EXPORT HRESULT SetSwapChain(SwapChainPanel* swapChainPanel, IDXGISwapChain* swapChain)
{
    com_ptr<ISwapChainPanelNative> nativeSwapChainPanel;

    HRESULT result;

    auto swapChainPanelUnknown = reinterpret_cast<IUnknown*>(swapChainPanel);

    result = swapChainPanelUnknown->QueryInterface(IID_PPV_ARGS(&nativeSwapChainPanel));
    if (result != S_OK) return result;

    result = nativeSwapChainPanel->SetSwapChain(swapChain);
    if (result != S_OK) return result;

    return S_OK;
}
