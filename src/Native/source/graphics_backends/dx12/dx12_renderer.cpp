#include <graphics_backends/dx12/dx12_renderer.h>


namespace graphics_backends::dx12 {

void Dx12Renderer::Render() {
    auto* command_list = initializer_->CommandList();
    auto* command_allocator = initializer_->CommandAllocator();

    // 描画先として使えるようRENDER_TARGETへ遷移させる
    TransitionBackBuffer(D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);

    const auto rtv_handle = initializer_->CurrentRenderTargetView();
    command_list->OMSetRenderTargets(1, &rtv_handle, true, nullptr);
    command_list->ClearRenderTargetView(rtv_handle, clear_color_.data(), 0, nullptr);

    // Presentできる状態へ戻す
    TransitionBackBuffer(D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);

    winrt::check_hresult(command_list->Close());

    const std::array<ID3D12CommandList*, 1> command_lists{command_list};
    initializer_->CommandQueue()->ExecuteCommandLists(
        static_cast<UINT>(command_lists.size()), command_lists.data()
    );

    // アロケータを再利用する前にGPUの完了を待つ
    // 毎フレームCPUとGPUを同期させているため並列には動かない、フレームを重ねて回すにはアロケータをフレーム数だけ用意する必要がある
    initializer_->WaitForGpu();

    winrt::check_hresult(command_allocator->Reset());
    winrt::check_hresult(command_list->Reset(command_allocator, nullptr));

    winrt::check_hresult(initializer_->SwapChain()->Present(1, 0));
}

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters)
void Dx12Renderer::TransitionBackBuffer(D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER desc{};
    desc.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    desc.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;

    // Transitionは共用体メンバだが、D3D12のAPI仕様上ここを埋める以外にない.
    // NOLINTBEGIN(cppcoreguidelines-pro-type-union-access)
    desc.Transition.pResource = initializer_->CurrentBackBuffer();
    desc.Transition.Subresource = 0;
    desc.Transition.StateBefore = before;
    desc.Transition.StateAfter = after;
    // NOLINTEND(cppcoreguidelines-pro-type-union-access)

    initializer_->CommandList()->ResourceBarrier(1, &desc);
}

}  // namespace graphics_backends::dx12
