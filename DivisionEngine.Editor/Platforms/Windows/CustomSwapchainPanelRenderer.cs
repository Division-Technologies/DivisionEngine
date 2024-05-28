
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using WinRT;
// [assembly: ExportHandler(typeof(CustomSwapChainPanelControl), typeof(CustomSwapChainPanelControlHandler))]

namespace DivisionEngine.Editor;

public partial class CustomSwapChainPanelHandler : ViewHandler<CustomSwapChainPanel, SwapChainPanel>
{
    public static IPropertyMapper<CustomSwapChainPanel, CustomSwapChainPanelHandler> Mapper =
        new PropertyMapper<CustomSwapChainPanel, CustomSwapChainPanelHandler>(ViewMapper)
        {
            [nameof(CustomSwapChainPanel.Background)] = (handler, view) => { },
        };

    public static CommandMapper<CustomSwapChainPanel, CustomSwapChainPanelHandler> CommandMapper =
        new(ViewCommandMapper);

    public CustomSwapChainPanelHandler() : base(Mapper, CommandMapper)
    {
    }

    protected override SwapChainPanel CreatePlatformView()
    {
        return new SwapChainPanel();
    }

    protected override void ConnectHandler(SwapChainPanel platformView)
    {
        base.ConnectHandler(platformView);
        InitializeDirectX(platformView);
    }

    private void InitializeDirectX(SwapChainPanel swapChainPanel)
    {
        // Implement your DirectX initialization and rendering logic here
        // ((System.Runtime.InteropServices.ICustomQueryInterface)swapChainPanel).GetInterface()
        IntPtr swapChainPanelPtr = ((IWinRTObject)swapChainPanel).NativeObject.ThisPtr;

        Debug.WriteLine(swapChainPanelPtr);
        

    }

    protected override void DisconnectHandler(SwapChainPanel platformView)
    {
        base.DisconnectHandler(platformView);
        // Cleanup any resources if needed
    }
}
