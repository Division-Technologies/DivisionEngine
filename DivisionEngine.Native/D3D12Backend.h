#pragma once

#include "pch.h";

#include <d3d12.h>
#include <dxgi1_6.h>

struct D3D12Backend
{
    D3D12Backend();
    ~D3D12Backend();

    void Init(HWND hwnd, UINT width, UINT height);
    IDXGISwapChain3* GetSwapChain() const;
    void Render();

private:
    static constexpr UINT FrameCount = 2;

    ComPtr<ID3D12Device> _dev = nullptr;
    ComPtr<IDXGISwapChain3> _swapChain = nullptr;

    ComPtr<ID3D12CommandAllocator> _cmdAllocator = nullptr;
    ComPtr<ID3D12GraphicsCommandList> _cmdList = nullptr;

    ComPtr<ID3D12CommandQueue> _cmdQueue = nullptr;

    ComPtr<ID3D12DescriptorHeap> _rtvHeaps = nullptr;

    ComPtr<ID3D12Resource> _renderTargets[FrameCount];

    ComPtr<ID3D12Fence> _fence = nullptr;
    UINT64 _fenceVal = 0;

    void WaitForLastFrame();
};

DIVISION_EXPORT D3D12Backend* D3D12Backend_New()
{
    return new D3D12Backend();
}

DIVISION_EXPORT void D3D12Backend_Delete(D3D12Backend* ptr)
{
    delete ptr;
}

DIVISION_EXPORT void D3D12Backend_Init(D3D12Backend* ptr, HWND hwnd, UINT width, UINT height)
{
    ptr->Init(hwnd, width, height);
}

DIVISION_EXPORT void D3D12Backend_Render(D3D12Backend* ptr)
{
    ptr->Render();
}

DIVISION_EXPORT IDXGISwapChain3* D3D12Backend_GetSwapChain(D3D12Backend* ptr)
{
    return ptr->GetSwapChain();
}
