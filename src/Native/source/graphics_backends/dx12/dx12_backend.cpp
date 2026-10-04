#include <graphics_backends/dx12/dx12_backend.h>
#include <graphics_backends/dx12/dx12_initializer.h>
#include <graphics_backends/dx12/dx12_renderer.h>

#include <memory>


namespace graphics_backends::dx12 {

Dx12Backend::Dx12Backend()
    : initializer_(std::make_unique<Dx12Initializer>()),
      renderer_(std::make_unique<Dx12Renderer>(*initializer_)) {}

Dx12Backend::~Dx12Backend() = default;

void Dx12Backend::Initialize(HWND hwnd) {
    initializer_->Initialize(hwnd);
}

void Dx12Backend::RenderFrame() {
    renderer_->Render();
}

void Dx12Backend::SetClearColor(const std::array<float, 4>& color) {
    renderer_->SetClearColor(color);
}

void Dx12Backend::WaitForGpu() {
    initializer_->WaitForGpu();
}

}  // namespace graphics_backends::dx12
