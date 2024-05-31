#include "pch.h"
#include "D3D12Backend.h"


#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")


D3D12Backend::D3D12Backend()
= default;

D3D12Backend::~D3D12Backend()
= default;

void D3D12Backend::Init(HWND hwnd, UINT width, UINT height)
{
#ifdef _DEBUG
    {
        ComPtr<ID3D12Debug> debugLayer;
        if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debugLayer))))
        {
            debugLayer->EnableDebugLayer();
        }
    }
#endif

    ThrowIfFailed(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&_dev)));

    ComPtr<IDXGIFactory4> factory;
    ThrowIfFailed(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));

    ThrowIfFailed(_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&_cmdAllocator)));
    ThrowIfFailed(_dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, _cmdAllocator.Get(), nullptr,
                                          IID_PPV_ARGS(&_cmdList)));

    ThrowIfFailed(_cmdList->Close());

    D3D12_COMMAND_QUEUE_DESC cmdQueueDesc = {};

    cmdQueueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
    cmdQueueDesc.NodeMask = 0;
    cmdQueueDesc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
    cmdQueueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ThrowIfFailed(_dev->CreateCommandQueue(&cmdQueueDesc, IID_PPV_ARGS(&_cmdQueue)));

    DXGI_SWAP_CHAIN_DESC1 swapChainDesc = {};

    swapChainDesc.Width = width;
    swapChainDesc.Height = height;
    swapChainDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapChainDesc.Stereo = false;
    swapChainDesc.SampleDesc.Count = 1;
    swapChainDesc.SampleDesc.Quality = 0;
    swapChainDesc.BufferUsage = DXGI_USAGE_BACK_BUFFER;
    swapChainDesc.BufferCount = 2;

    swapChainDesc.Scaling = DXGI_SCALING_STRETCH;
    swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    swapChainDesc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
    swapChainDesc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

    ComPtr<IDXGISwapChain1> swapChain;

    if (hwnd == nullptr)
    {
        ThrowIfFailed(factory->CreateSwapChainForComposition(_cmdQueue.Get(), &swapChainDesc, nullptr, &swapChain));
    }
    else
    {
        ThrowIfFailed(
            factory->CreateSwapChainForHwnd(_cmdQueue.Get(), hwnd, &swapChainDesc, nullptr, nullptr, &swapChain));
    }

    ThrowIfFailed(swapChain.As(&_swapChain));


    D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};

    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    heapDesc.NodeMask = 0;
    heapDesc.NumDescriptors = FrameCount;
    heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

    ThrowIfFailed(_dev->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&_rtvHeaps)));

    D3D12_CPU_DESCRIPTOR_HANDLE handle = _rtvHeaps->GetCPUDescriptorHandleForHeapStart();

    for (int i = 0; i < swapChainDesc.BufferCount; i++)
    {
        ThrowIfFailed(_swapChain->GetBuffer(i, IID_PPV_ARGS(&_renderTargets[i])));

        _dev->CreateRenderTargetView(_renderTargets[i].Get(), nullptr, handle);

        handle.ptr += _dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    }

    ThrowIfFailed(_dev->CreateFence(_fenceVal, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&_fence)));
}

void D3D12Backend::Render()
{
    ThrowIfFailed(_cmdAllocator->Reset());

    ThrowIfFailed(_cmdList->Reset(_cmdAllocator.Get(), nullptr));

    auto bbIdx = _swapChain->GetCurrentBackBufferIndex();

    auto rtvH = _rtvHeaps->GetCPUDescriptorHandleForHeapStart();

    rtvH.ptr += bbIdx * _dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

    // enqueue commands

    D3D12_RESOURCE_BARRIER barrierDesc;
    barrierDesc.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrierDesc.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    barrierDesc.Transition.pResource = _renderTargets[bbIdx].Get();
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

    ThrowIfFailed(_cmdList->Close());

    ID3D12CommandList* cmdLists[] = {_cmdList.Get()};

    _cmdQueue->ExecuteCommandLists(1, cmdLists);

    WaitForLastFrame();
    ThrowIfFailed(_swapChain->Present(1, 0));
}

void D3D12Backend::WaitForLastFrame()
{
    ThrowIfFailed(_cmdQueue->Signal(_fence.Get(), ++_fenceVal));

    // wait for signal
    if (_fence->GetCompletedValue() != _fenceVal)
    {
        auto event = CreateEvent(nullptr, false, false, nullptr);

        ThrowIfFailed(_fence->SetEventOnCompletion(_fenceVal, event));

        WaitForSingleObject(event, INFINITE);

        CloseHandle(event);
    }
}

IDXGISwapChain3* D3D12Backend::GetSwapChain() const
{
    return _swapChain.Get();
}
