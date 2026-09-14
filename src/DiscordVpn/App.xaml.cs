using System.IO;
using System.Windows;
using Velopack;
namespace DiscordVpn;
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow());
        }
        catch (Exception exception)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn");
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "startup.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {exception}\n\n");
            MessageBox.Show($"O discSapo não conseguiu iniciar.\n\nO diagnóstico foi salvo em:\n{logPath}", "discSapo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
