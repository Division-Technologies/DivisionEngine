
using System.Diagnostics;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
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

    public NativeGraphicsPanelHandler() : base(Mapper, CommandMapper)
    {
    }

    protected override SwapChainPanel CreatePlatformView()
    {
        return new SwapChainPanel();
    }

    protected override void ConnectHandler(SwapChainPanel platformView)
    {
        base.ConnectHandler(platformView);

        IntPtr swapChainPanelPtr = ((IWinRTObject)platformView).NativeObject.ThisPtr;
        IntPtr swapChain = NativeMethods.GetSwapChain();
        var result = EditorNativeMethods.SetSwapChain(swapChainPanelPtr, swapChain);

        if (result != 0)
        {
            Debug.Fail("Failed to set swap chain.");
        }
    }
}
