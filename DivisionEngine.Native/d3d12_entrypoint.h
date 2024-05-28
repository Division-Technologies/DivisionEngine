#pragma once
#include <dxgi1_6.h>

DIVISION_EXPORT IDXGISwapChain4* GetSwapChain();

DIVISION_EXPORT HRESULT InitD3D12(HWND hWnd, UINT window_width, UINT window_height);
DIVISION_EXPORT HRESULT Render();