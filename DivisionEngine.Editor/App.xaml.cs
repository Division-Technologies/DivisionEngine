namespace DivisionEngine.Editor;

public partial class App : Application
{
    private readonly IServiceProvider serviceProvider;
    private ICoreLoopRunner? coreLoopRunner;

    public App(IServiceProvider serviceProvider)
    {
        InitializeComponent();

        MainPage = new AppShell();
        this.serviceProvider = serviceProvider;
    }

    protected override void OnStart()
    {
        coreLoopRunner = serviceProvider.GetService<ICoreLoopRunner>();
    }
}