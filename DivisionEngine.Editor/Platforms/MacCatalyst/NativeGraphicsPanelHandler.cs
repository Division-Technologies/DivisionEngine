using DivisionEngine.Graphics;
using MetalKit;
using Microsoft.Maui.Handlers;

namespace DivisionEngine.Editor;

public partial class NativeGraphicsPanelHandler : ViewHandler<NativeGraphicsPanel, MTKView>
{
    public static IPropertyMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler> Mapper =
        new PropertyMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler>(ViewMapper);

    public static CommandMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler> CommandMapper =
        new(ViewCommandMapper);

    private readonly IGraphicsBackend graphicsBackend;

    public NativeGraphicsPanelHandler(IGraphicsBackend graphicsBackend) : base(Mapper, CommandMapper)
    {
        this.graphicsBackend = graphicsBackend;
    }

    protected override MTKView CreatePlatformView()
    {
        return new MTKView();
    }

    protected override void ConnectHandler(MTKView platformView)
    {
        base.ConnectHandler(platformView);
    }
}