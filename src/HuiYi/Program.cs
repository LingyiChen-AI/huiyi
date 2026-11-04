using Velopack;

namespace HuiYi.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // First thing: when Setup.exe or an update runs us with its hook arguments, Velopack handles
        // them (shortcuts, uninstall clean-up) and exits before any window or tray icon exists.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
