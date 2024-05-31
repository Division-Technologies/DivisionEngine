
using DivisionEngine.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using WinRT;

namespace DivisionEngine.Editor;

public partial class NativeGraphicsPanelHandler : ViewHandler<NativeGraphicsPanel, SwapChainPanel>
{
    public static IPropertyMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler> Mapper =
        new PropertyMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler>(ViewMapper)
        {
            [nameof(NativeGraphicsPanel.Background)] = (handler, view) => { }, // workaround: Background property is not supported by SwapChainPanel
        };

    public static CommandMapper<NativeGraphicsPanel, NativeGraphicsPanelHandler> CommandMapper =
        new(ViewCommandMapper);
    private readonly GraphicsBackend graphicsBackend;

    public NativeGraphicsPanelHandler(GraphicsBackend graphicsBackend) : base(Mapper, CommandMapper)
    {
        this.graphicsBackend = graphicsBackend;
    }

    protected override SwapChainPanel CreatePlatformView()
    {
        return new SwapChainPanel();
    }

    protected override void ConnectHandler(SwapChainPanel platformView)
    {
        base.ConnectHandler(platformView);

        if (graphicsBackend is not D3D12Backend d3d12Backend)
        {
            Debug.Fail("Unsupported graphics backend.");
            return;
        }

        IntPtr swapChainPanelPtr = ((IWinRTObject)platformView).NativeObject.ThisPtr;
        IntPtr swapChain = d3d12Backend.GetSwapChain();
        var result = EditorNativeMethods.SetSwapChain(swapChainPanelPtr, swapChain);

        if (result != 0)
        {
            Debug.Fail("Failed to set swap chain.");
        }
    }
}
