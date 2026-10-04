#pragma once

#include <graphics_backends/buffer.h>

#include <memory>


namespace graphics_backends {

// GPUリソースの生成を担う.
// Backend::Resources()から取得し、生成されたリソースの所有権は呼び出し側が持つ.
class ResourceFactory {
  public:
    ResourceFactory() = default;
    virtual ~ResourceFactory() = default;

    ResourceFactory(const ResourceFactory&) = delete;
    ResourceFactory& operator=(const ResourceFactory&) = delete;
    ResourceFactory(ResourceFactory&&) = delete;
    ResourceFactory& operator=(ResourceFactory&&) = delete;

    [[nodiscard]] virtual std::unique_ptr<Buffer> CreateBuffer(const BufferDesc& desc) = 0;
};

}  // namespace graphics_backends
