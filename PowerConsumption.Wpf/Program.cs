using Velopack;

namespace PowerConsumption.Wpf;

/// <summary>Entry point. Replaces WPF's generated Main so Velopack can run first.</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must be the first thing to run: handles Velopack's install/update/uninstall hooks
        // and may exit the process. Don't swallow exceptions here.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
