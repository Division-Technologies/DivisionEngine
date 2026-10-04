#include <graphics_backends/dx12/dx12_buffer.h>
#include <graphics_backends/dx12/dx12_initializer.h>
#include <graphics_backends/dx12/dx12_resource_factory.h>

#include <memory>
#include <utility>


namespace graphics_backends::dx12 {

std::unique_ptr<Buffer> Dx12ResourceFactory::CreateBuffer(const BufferDesc& desc) {
    // CPUから直接書き込めるアップロードヒープに置く.
    // D3D12_HEAP_TYPEは0に対応する列挙子を持たないため、{}でゼロ初期化せず全メンバを明示する.
    const D3D12_HEAP_PROPERTIES heap_properties{
        .Type = D3D12_HEAP_TYPE_UPLOAD,
        .CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_UNKNOWN,
        .MemoryPoolPreference = D3D12_MEMORY_POOL_UNKNOWN,
        .CreationNodeMask = 0,
        .VisibleNodeMask = 0,
    };

    D3D12_RESOURCE_DESC resource_desc{};
    resource_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    resource_desc.Width = desc.size_in_bytes;
    resource_desc.Height = 1;
    resource_desc.DepthOrArraySize = 1;
    resource_desc.MipLevels = 1;
    resource_desc.Format = DXGI_FORMAT_UNKNOWN;
    resource_desc.SampleDesc.Count = 1;
    resource_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    resource_desc.Flags = D3D12_RESOURCE_FLAG_NONE;

    winrt::com_ptr<ID3D12Resource> resource;
    winrt::check_hresult(initializer_->Device()->CreateCommittedResource(
        &heap_properties,
        D3D12_HEAP_FLAG_NONE,
        &resource_desc,
        D3D12_RESOURCE_STATE_GENERIC_READ,
        nullptr,
        IID_PPV_ARGS(resource.put())
    ));

    return std::make_unique<Dx12Buffer>(std::move(resource), desc.size_in_bytes);
}

}  // namespace graphics_backends::dx12
