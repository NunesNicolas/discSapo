using System.Windows;
using Velopack;
namespace DiscordVpn;
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
