#include "pch.h"
#include <d3d12.h>
#include <dxgi1_6.h>
#include <vector>
#include <iostream>

#include "d3d12_entrypoint.h"

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

ID3D12Device *_dev = nullptr;
IDXGIFactory6 *_dxgiFactory = nullptr;
IDXGISwapChain4 *_swapchain = nullptr;

ID3D12CommandAllocator *_cmdAllocator = nullptr;
ID3D12GraphicsCommandList *_cmdList = nullptr;

ID3D12CommandQueue *_cmdQueue = nullptr;

ID3D12DescriptorHeap *_rtvHeaps = nullptr;

ID3D12Fence *_fence = nullptr;
UINT64 _fenceVal = 0;

std::vector<ID3D12Resource *> _backBuffers;

HRESULT InitD3D12(HWND hWnd, UINT window_width, UINT window_height)
{

    HRESULT result;

    ID3D12Debug *debugLayer = nullptr;
    result = D3D12GetDebugInterface(IID_PPV_ARGS(&debugLayer));
    debugLayer->EnableDebugLayer();
    debugLayer->Release();

    result = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&_dev));
    if (result != S_OK)
        return result;

    result = CreateDXGIFactory1(IID_PPV_ARGS(&_dxgiFactory));
    if (result != S_OK)
        return result;

    _dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&_cmdAllocator));
    _dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, _cmdAllocator, nullptr, IID_PPV_ARGS(&_cmdList));

    result = _cmdList->Close();
    if (result != S_OK)
        return result;

    D3D12_COMMAND_QUEUE_DESC cmdQueueDesc = {};

    cmdQueueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
    cmdQueueDesc.NodeMask = 0;
    cmdQueueDesc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
    cmdQueueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    result = _dev->CreateCommandQueue(&cmdQueueDesc, IID_PPV_ARGS(&_cmdQueue));

    if (result != S_OK)
        return result;

    DXGI_SWAP_CHAIN_DESC1 swapchainDesc = {};

    swapchainDesc.Width = window_width;
    swapchainDesc.Height = window_height;
    swapchainDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapchainDesc.Stereo = false;
    swapchainDesc.SampleDesc.Count = 1;
    swapchainDesc.SampleDesc.Quality = 0;
    swapchainDesc.BufferUsage = DXGI_USAGE_BACK_BUFFER;
    swapchainDesc.BufferCount = 2;

    swapchainDesc.Scaling = DXGI_SCALING_STRETCH;
    swapchainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    swapchainDesc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
    swapchainDesc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

    result = _dxgiFactory->CreateSwapChainForHwnd(_cmdQueue, hWnd, &swapchainDesc, nullptr, nullptr, (IDXGISwapChain1 **)&_swapchain);

    if (result != S_OK)
        return result;

    D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};

    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    heapDesc.NodeMask = 0;
    heapDesc.NumDescriptors = 2;
    heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

    result = _dev->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&_rtvHeaps));
    if (result != S_OK)
        return result;

    _backBuffers = std::vector<ID3D12Resource *>(swapchainDesc.BufferCount);

    D3D12_CPU_DESCRIPTOR_HANDLE handle = _rtvHeaps->GetCPUDescriptorHandleForHeapStart();

    for (int i = 0; i < swapchainDesc.BufferCount; i++)
    {
        result = _swapchain->GetBuffer(i, IID_PPV_ARGS(&_backBuffers[i]));
        if (result != S_OK)
            return result;

        _dev->CreateRenderTargetView(_backBuffers[i], nullptr, handle);

        handle.ptr += _dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    }

    result = _dev->CreateFence(_fenceVal, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&_fence));
    if (result != S_OK)
        return result;

    return S_OK;
}

HRESULT Render()
{
    HRESULT result;

    result = _cmdAllocator->Reset();
    if (result != S_OK)
        return result;

    result = _cmdList->Reset(_cmdAllocator, nullptr);
    if (result != S_OK)
        return result;

    auto bbIdx = _swapchain->GetCurrentBackBufferIndex();

    auto rtvH = _rtvHeaps->GetCPUDescriptorHandleForHeapStart();

    rtvH.ptr += bbIdx * _dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

    // enqueue commands

    D3D12_RESOURCE_BARRIER barrierDesc;
    barrierDesc.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrierDesc.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    barrierDesc.Transition.pResource = _backBuffers[bbIdx];
    barrierDesc.Transition.Subresource = 0;
    barrierDesc.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrierDesc.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;

    _cmdList->ResourceBarrier(1, &barrierDesc);

    _cmdList->OMSetRenderTargets(1, &rtvH, true, nullptr);

    float clearColor[] = {1.0f, 1.0f, 0.0f, 1.0f};

    _cmdList->ClearRenderTargetView(rtvH, clearColor, 0, nullptr);

    barrierDesc.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrierDesc.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;

    _cmdList->ResourceBarrier(1, &barrierDesc);

    _cmdList->Close();

    ID3D12CommandList *cmdLists[] = {_cmdList};

    _cmdQueue->ExecuteCommandLists(1, cmdLists);

    _cmdQueue->Signal(_fence, ++_fenceVal);

    // wait for signal
    if (_fence->GetCompletedValue() != _fenceVal)
    {
        auto event = CreateEvent(nullptr, false, false, nullptr);

        _fence->SetEventOnCompletion(_fenceVal, event);

        WaitForSingleObject(event, INFINITE);

        CloseHandle(event);
    }

    _swapchain->Present(1, 0);

    return S_OK;
}