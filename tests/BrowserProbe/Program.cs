using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using System.Text;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Provide a WireGuard config path.");
        var application = new Application();
        var window = new DiscordVpn.MainWindow { ShowActivated = false };
        window.Loaded += async (_, _) => {
            try
            {
                var type = window.GetType();
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                type.GetField("configPath", flags)!.SetValue(window, args[0]);
                type.GetMethod("Connect_Click", flags)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                var deadline = DateTime.UtcNow.AddMinutes(4);
                string last = "";
                bool observing = false;
                while (DateTime.UtcNow < deadline)
                {
                    var status = ((TextBlock)window.FindName("StatusText")).Text;
                    if (status != last) { Console.WriteLine(status); last = status; }
                    var browser = (WebView2?)type.GetField("browser", flags)!.GetValue(window);
                    if (browser?.CoreWebView2 is { } core)
                    {
                        if (!observing)
                        {
                            observing = true;
                            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) => {
                                using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                                var details = data.RootElement.GetProperty("exceptionDetails");
                                if (details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("className", out var className))
                                    Console.WriteLine("Script error type: " + className.GetString());
                            };
                            core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) => {
                                using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                                Console.WriteLine("Network error: " + data.RootElement.GetProperty("errorText").GetString());
                            };
                            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                        }
                        // Record only aggregate rendering data, never page text or credentials.
                        var metadata = await core.ExecuteScriptAsync("JSON.stringify({host:location.hostname,ready:document.readyState,bodyChildren:document.body?.children.length,textLength:document.body?.innerText.length,scripts:document.scripts.length,loginForm:!!document.querySelector('input[type=password]')})");
                        Console.WriteLine(metadata);
                    }
                    if (((DispatcherTimer)type.GetField("monitor", flags)!.GetValue(window)!).IsEnabled)
                    {
                        Console.WriteLine("PASS: browser content loaded through verified tunnel.");
                        await CheckLoadingFailuresAsync(browser!.CoreWebView2);
                        return;
                    }
                    if (type.GetField("connection", flags)!.GetValue(window) is null)
                    {
                        Console.WriteLine(((TextBlock)window.FindName("DetailText")).Text);
                        Environment.ExitCode = 1;
                        return;
                    }
                    await Task.Delay(5000);
                }
                Console.WriteLine("FAIL: probe timed out.");
                Environment.ExitCode = 1;
            }
            catch (Exception ex) { Console.WriteLine(ex.GetType().Name); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        application.Run(window);
    }

    private static async Task CheckLoadingFailuresAsync(CoreWebView2 core)
    {
        const string url = "https://browser-probe.invalid/";
        string html = "<html><body><iframe style='width:100px;height:100px'></iframe></body></html>";
        int status = 200;
        void Intercept(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), status, status == 200 ? "OK" : "Unavailable", "Content-Type: text/html");
        }
        core.AddWebResourceRequestedFilter(url, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Intercept;
        try
        {
            await DiscordVpn.BrowserLoader.NavigateAsync(core, url, TimeSpan.FromSeconds(5), CancellationToken.None);
            try
            {
                await DiscordVpn.BrowserLoader.WaitForContentAsync(core, TimeSpan.FromSeconds(1), CancellationToken.None);
                throw new Exception("Empty shell accepted.");
            }
            catch (TimeoutException) { Console.WriteLine("PASS: blank page with iframe times out."); }
            html = "<html><body><script>setTimeout(()=>{document.body.innerHTML='<button>Ready</button>'},500)</script></body></html>";
            await DiscordVpn.BrowserLoader.NavigateAsync(core, url, TimeSpan.FromSeconds(5), CancellationToken.None);
            await DiscordVpn.BrowserLoader.WaitForContentAsync(core, TimeSpan.FromSeconds(5), CancellationToken.None);
            Console.WriteLine("PASS: delayed JavaScript content detected.");
            status = 503;
            try
            {
                await DiscordVpn.BrowserLoader.NavigateAsync(core, url, TimeSpan.FromSeconds(5), CancellationToken.None);
                throw new Exception("HTTP error accepted.");
            }
            catch (InvalidOperationException) { Console.WriteLine("PASS: HTTP failure reported."); }
        }
        finally
        {
            core.WebResourceRequested -= Intercept;
            core.RemoveWebResourceRequestedFilter(url, CoreWebView2WebResourceContext.All);
        }
    }
}
