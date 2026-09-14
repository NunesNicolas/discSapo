using System.IO;
using Microsoft.Web.WebView2.Core;

namespace DiscordVpn;

internal static class BrowserDiagnostics
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn", "browser.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Attach(CoreWebView2 core)
    {
        Write($"Browser started; runtime={core.Environment.BrowserVersionString}");
        core.NavigationCompleted += (_, args) => Write($"Navigation HTTP={args.HttpStatusCode}; success={args.IsSuccess}; error={args.WebErrorStatus}");
        core.WebResourceResponseReceived += (_, args) => {
            if (args.Response.StatusCode >= 400 && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                Write($"Resource host={uri.Host}; HTTP={args.Response.StatusCode}");
        };
        core.ProcessFailed += (_, args) => Write($"Process failure={args.ProcessFailedKind}");
    }
}
