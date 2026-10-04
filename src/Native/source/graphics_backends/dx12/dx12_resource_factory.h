#pragma once

#include <graphics_backends/resource_factory.h>

#include <memory>


namespace graphics_backends::dx12 {

class Dx12Initializer;

// Dx12InitializerのDeviceを使ってリソースを生成する.
// Initializerの所有権は持たず、Dx12Backendが両者の寿命を揃える.
class Dx12ResourceFactory final : public graphics_backends::ResourceFactory {
  public:
    explicit Dx12ResourceFactory(Dx12Initializer& initializer) noexcept : initializer_(&initializer) {}
    ~Dx12ResourceFactory() override = default;

    Dx12ResourceFactory(const Dx12ResourceFactory&) = delete;
    Dx12ResourceFactory& operator=(const Dx12ResourceFactory&) = delete;
    Dx12ResourceFactory(Dx12ResourceFactory&&) = delete;
    Dx12ResourceFactory& operator=(Dx12ResourceFactory&&) = delete;

    [[nodiscard]] std::unique_ptr<Buffer> CreateBuffer(const BufferDesc& desc) override;

  private:
    Dx12Initializer* initializer_;
};

}  // namespace graphics_backends::dx12
