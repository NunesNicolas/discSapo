using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using DiscordVpn;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private const string Configuration = "[Interface]\nPrivateKey = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n[Peer]\nPublicKey = AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=\nEndpoint = 127.0.0.1:9\nAllowedIPs = 0.0.0.0/0\n";

    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigurationStore(root);
        if (!ProtonPortal.IsProtonPage("https://account.proton.me/u/0/vpn") || ProtonPortal.IsProtonPage("https://account.proton.me.evil.test/") || ProtonPortal.IsProtonPage("http://account.protonvpn.com/"))
            throw new Exception("Portal origin policy failed.");
        Console.WriteLine("PASS: official HTTPS account origins only.");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int connectionRequests = 0;
        int result = 0;
        var main = new MainWindow(store, () => connectionRequests++) { ShowActivated = false };
        main.Show();
        main.UpdateLayout();
        var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        preview.Render(main);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
        using (var output = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-preview.png"))) encoder.Save(output);
        if (main.HeaderLogo.Source is null || main.WelcomeLogo.Source is null || main.Icon is null) throw new Exception("Logo resources missing.");
        main.SetControlsVisible(false);
        if (main.AppHeader.Visibility != Visibility.Collapsed || main.AppFooter.Visibility != Visibility.Collapsed || main.HoverControls.IsOpen)
            throw new Exception("Controls did not hide correctly.");
        main.UpdateLayout();
        main.UpdateHoverRegion(30, 5);
        if (!main.HoverControls.IsOpen) throw new Exception("Top hover did not reveal controls.");
        main.UpdateHoverRegion(30, 30);
        if (!main.HoverControls.IsOpen) throw new Exception("Controls closed while pointer was over them.");
        main.UpdateHoverRegion(30, 90, 0);
        main.UpdateHoverRegion(30, 90, 899);
        if (!main.HoverControls.IsOpen) throw new Exception("Controls disappeared before the delay.");
        main.UpdateHoverRegion(30, 30, 900);
        main.UpdateHoverRegion(30, 90, 1000);
        main.UpdateHoverRegion(30, 90, 1899);
        if (!main.HoverControls.IsOpen) throw new Exception("Reentering did not reset the hide delay.");
        main.UpdateHoverRegion(30, 90, 1900);
        if (main.HoverControls.IsOpen) throw new Exception("Controls remain visible after the hide delay.");
        main.SetControlsVisible(true);
        if (main.AppHeader.Visibility != Visibility.Visible || main.HoverControls.IsOpen) throw new Exception("Controls did not return.");
        Console.WriteLine("PASS: logo loads; header/footer hide and restore.");
        if (args.Contains("--header-only")) {
            Console.WriteLine("PASS: hover stays visible for 900 ms; reentering resets the delay.");
            main.Close(); application.Shutdown(0); return;
        }
        var portal = main.CreateProtonPortal(Path.Combine(root, "profile"));
        portal.ShowActivated = false;
        portal.Loaded += async (_, _) => {
            try
            {
                await UntilAsync(() => portal.PortalView.CoreWebView2 is not null && !string.IsNullOrEmpty(portal.PortalView.CoreWebView2.Source), 20);
                var core = portal.PortalView.CoreWebView2;
                await BrowserLoader.WaitForContentAsync(core, TimeSpan.FromSeconds(45), CancellationToken.None);
                if (!ProtonPortal.IsProtonPage(core.Source)) throw new Exception("Unexpected live portal origin.");
                Console.WriteLine("PASS: official portal displayed content inside WebView2 (no login performed).");
                const string fixtureUrl = "https://account.protonvpn.com/integration-fixture";
                core.AddWebResourceRequestedFilter(fixtureUrl + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => {
                    var nativeDownload = args.Request.Uri.EndsWith("/native.conf");
                    args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(nativeDownload ? "invalid configuration" : "<html><body>Download test</body></html>")), 200, "OK", nativeDownload ? "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=native.conf" : "Content-Type: text/html");
                };
                await BrowserLoader.NavigateAsync(core, fixtureUrl, TimeSpan.FromSeconds(10), CancellationToken.None);
                await BrowserLoader.NavigateAsync(core, fixtureUrl + "-next", TimeSpan.FromSeconds(10), CancellationToken.None);
                if (!portal.PortalBack.IsEnabled) throw new Exception("Back button not enabled.");
                portal.PortalBack.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await UntilAsync(() => core.Source == fixtureUrl, 5);
                await UntilAsync(() => portal.PortalForward.IsEnabled, 5);
                portal.PortalForward.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await UntilAsync(() => core.Source == fixtureUrl + "-next", 5);
                Console.WriteLine("PASS: back and forward buttons navigate browser history.");
                await core.ExecuteScriptAsync($"(()=>{{const a=document.createElement('a');a.href='{fixtureUrl}/native.conf';a.download='native.conf';document.body.append(a);a.click();}})()");
                await UntilAsync(() => portal.PortalStatus.Text.Contains("não contém"), 10);
                if (store.Exists || connectionRequests != 0) throw new Exception("Invalid native download accepted.");
                Console.WriteLine("PASS: native HTTP download validated and rejected without connecting.");
                portal.PortalStatus.Text = "Waiting for bridge";
                await DownloadAsync(core, "invalid contents", "test.conf");
                await UntilAsync(() => portal.PortalStatus.Text.Contains("não contém"), 10);
                if (store.Exists || portal.Imported) throw new Exception("Invalid config accepted.");
                Console.WriteLine("PASS: invalid blob download refused.");
                await DownloadAsync(core, Configuration, "test.conf");
                await UntilAsync(() => portal.Imported, 10);
                await UntilAsync(() => connectionRequests == 1 && !portal.IsVisible, 10);
                if (!main.ConnectButton.IsEnabled || !main.ConfigText.Text.Contains("salva") || !main.StatusText.Text.Contains("pronta")) throw new Exception("Main window did not select the imported config.");
                Console.WriteLine("PASS: config selected, portal closed and connection requested automatically exactly once.");
                if (!store.Exists) throw new Exception("Downloaded configuration not saved.");
                var encrypted = File.ReadAllBytes(Path.Combine(root, "proton.bin"));
                if (Encoding.UTF8.GetString(encrypted).Contains("PrivateKey")) throw new Exception("Plaintext persisted.");
                var restoredStore = new ConfigurationStore(root);
                var sessionPath = restoredStore.Materialize();
                if (File.ReadAllText(sessionPath) != Configuration) throw new Exception("Configuration round trip failed.");
                ConfigurationStore.DeleteTemporary(sessionPath);
                if (Directory.EnumerateFiles(root, "*.conf").Any()) throw new Exception("Temporary config left behind.");
                Console.WriteLine("PASS: valid blob captured, encrypted, restored and temporary files cleaned.");
                var restoredMain = new MainWindow(restoredStore);
                if (!restoredMain.ConnectButton.IsEnabled) throw new Exception("Saved configuration not selected after restart.");
                restoredMain.Close();
                Console.WriteLine("PASS: imported configuration selected after restart.");
            }
            catch (Exception ex) { Console.WriteLine(ex); result = 1; }
            finally { portal.Close(); main.Close(); application.Shutdown(result); }
        };
        application.Run(portal);
        Environment.ExitCode = result;
    }

    private static Task<string> DownloadAsync(CoreWebView2 core, string content, string name) => core.ExecuteScriptAsync($"(() => {{ const a=document.createElement('a'); a.href=URL.createObjectURL(new Blob([{JsonSerializer.Serialize(content)}],{{type:'application/octet-stream'}})); a.download={JsonSerializer.Serialize(name)}; a.target='_blank'; document.body.append(a); a.click(); URL.revokeObjectURL(a.href); a.remove(); }})()");

    private static async Task UntilAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(100); }
    }
}
