using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DiscordVpn;

internal static class Program
{
    private static readonly JsonElement Session = JsonSerializer.SerializeToElement(new { AccessToken = "fixture-access", RefreshToken = "fixture-refresh", UID = "fixture-uid" });
    [STAThread]
    private static void Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("DISCSAPO_PROTOCOL_FIXTURE") == "1") { ProtocolFixture(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            var exitCode = 0;
            try { if (args.Contains("--live")) await LiveCheckAsync(); else await ChecksAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(exitCode); }
        };
        Environment.ExitCode = app.Run();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task ChecksAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "account.bin");
        var store = new ProtonAccountStore(path);
        store.Save(new(Session, "JP", true, true));
        Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("fixture-refresh"), "Token persisted in plaintext");
        Assert(store.Load()?.Country == "JP", "DPAPI session round-trip failed");
        store.Deactivate(); Assert(store.Load()?.Active == false, "Manual fallback didn't deactivate account");
        store.Forget(); Assert(store.Load() is null, "Forget failed");
        Console.WriteLine("PASS: DPAPI token storage, fallback selection and local forget.");

        var fake = new FakeProvider();
        var login = new ProtonLogin(store, () => fake) { ShowActivated = false };
        login.Show();
        await UntilAsync(() => login.LoginButton.IsEnabled);
        SavePreview(login, "proton-login-preview.png");
        login.Username.Text = "fixture-user"; login.Password.Password = "fixture-password";
        login.LoginButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => login.TwoFactorPanel.Visibility == Visibility.Visible);
        Assert(login.Password.Password.Length == 0, "Password retained in UI after submit");
        login.TwoFactor.Password = "123456";
        ((Button)login.TwoFactorPanel.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => login.UseButton.IsEnabled && login.Selection.Visibility == Visibility.Visible);
        login.Countries.SelectedValue = "JP";
        login.UseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(login.Ready && store.Load()?.Country == "JP" && store.Load()?.Active == true, "Login selection not saved");
        Assert(fake.Disposed && fake.Code == "123456", "TOTP or disposal failed");
        Assert(store.Load()?.UserName == "fixture-user", "User name not preserved for account card");
        Console.WriteLine("PASS: native password login, TOTP prompt and country selection.");

        var restore = new ProtonLogin(store, () => new FakeProvider()) { ShowActivated = false };
        restore.Show();
        await UntilAsync(() => restore.Selection.Visibility == Visibility.Visible && restore.UseButton.IsEnabled);
        Assert(store.Load()!.Session.GetProperty("RefreshToken").GetString() == "rotated-refresh", "Rotating token not immediately saved");
        Assert((string)restore.Countries.SelectedValue == "JP", "Country not restored");
        Assert(restore.FreeOnly.IsChecked == true && !restore.FreeOnly.IsEnabled && restore.PlanHint.Text.Contains("Plano gratuito"), "Free plan controls permit paid selection");
        SavePreview(restore, "proton-country-preview.png");
        restore.Close();
        var header = new MainWindow(new ConfigurationStore(root)) { ShowActivated = false };
        header.Show(); header.UpdateLayout();
        Assert(header.AccountInitial.Text == "F" && header.AccountIcon.Visibility == Visibility.Collapsed, "Saved account initial is missing");
        SavePreview(header, "header-account-preview.png");
        header.Width = 850; header.UpdateLayout();
        SavePreview(header, "header-compact-preview.png");
        WindowTheme.Apply(header, true);
        header.UpdateLayout();
        Assert(((System.Windows.Media.SolidColorBrush)header.AppHeader.Background).Color == System.Windows.Media.Color.FromRgb(43,45,49), "Header did not switch to dark theme");
        SavePreview(header, "header-dark-preview.png");
        WindowTheme.Apply(header, false);
        header.UpdateLayout();
        Assert(((System.Windows.Media.SolidColorBrush)header.AppHeader.Background).Color == System.Windows.Media.Colors.White, "Header did not switch to light theme");
        SavePreview(header, "header-light-preview.png");
        header.Close();
        Console.WriteLine("PASS: restore refreshes session and preserves selected country.");

        Environment.SetEnvironmentVariable("DISCSAPO_PROTOCOL_FIXTURE", "1");
        using var provider = new ProtonVpnProvider(Environment.ProcessPath!);
        Environment.SetEnvironmentVariable("DISCSAPO_PROTOCOL_FIXTURE", null);
        var session = await provider.LoginAsync("fixture-user", "fixture-password", () => Task.FromResult("123456"), CancellationToken.None);
        Assert(session.GetProperty("UID").GetString() == "fixture-uid", "Pipe login failed");
        var countries = await provider.CountriesAsync(true, CancellationToken.None);
        Assert(countries.Codes.SequenceEqual(new[] { "JP", "NL" }) && !countries.CanUsePaidServers, "Pipe countries or entitlement failed");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await provider.GenerateAsync("JP", true, cancellation.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: private IPC handles TOTP and cancellation terminates stalled helper.");
        store.Forget(); Directory.Delete(root);
    }
    private static void ProtocolFixture()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var json = JsonDocument.Parse(line);
            switch (json.RootElement.GetProperty("op").GetString())
            {
                case "login": Console.WriteLine("{\"type\":\"twoFactor\"}"); break;
                case "twoFactor": Console.WriteLine(JsonSerializer.Serialize(new { type = "session", session = Session })); break;
                case "countries": Console.WriteLine("{\"type\":\"countries\",\"countries\":[\"JP\",\"NL\"],\"canUsePaidServers\":false}"); break;
                case "generate": Thread.Sleep(30000); break;
            }
        }
    }

    // Opt-in authenticated smoke test. Credentials stay in DPAPI/private pipes;
    // only selected country, plan capability and pass/fail are printed.
    private static async Task LiveCheckAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var store = new ProtonAccountStore();
        var saved = store.Load() ?? throw new InvalidOperationException("No saved account");
        using var provider = new ProtonVpnProvider();
        var session = await provider.RestoreAsync(saved.Session, timeout.Token);
        store.Save(saved with { Session = session });
        var countries = await provider.CountriesAsync(false, timeout.Token);
        Console.WriteLine($"Plan allows paid servers: {countries.CanUsePaidServers}; available countries: {countries.Codes.Length}");
        var generated = await provider.GenerateAsync("", false, timeout.Token);
        Console.WriteLine($"Selected server: {generated.Server}; country: {generated.Country}");
        var temporary = Path.Combine(AppContext.BaseDirectory, "live-" + Guid.NewGuid().ToString("N") + ".conf");
        Microsoft.Web.WebView2.Wpf.WebView2? view = null;
        Window? window = null;
        try
        {
            await File.WriteAllTextAsync(temporary, generated.Text, timeout.Token);
            ConfigurationStore.ReadValidated(temporary);
            using var tunnel = new TunnelSession();
            var ip = await tunnel.StartAsync(temporary, timeout.Token);
            Console.WriteLine("PASS: automatically generated configuration verified through WireGuard.");
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
                $"--proxy-server=socks5://127.0.0.1:{tunnel.Port} --proxy-bypass-list=<-loopback> --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE 127.0.0.1\" --force-webrtc-ip-handling-policy=disable_non_proxied_udp --disable-quic");
            var profile = Path.Combine(AppContext.BaseDirectory, "live-profile-" + Guid.NewGuid().ToString("N"));
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, profile, options);
            view = new Microsoft.Web.WebView2.Wpf.WebView2();
            window = new Window { Width = 900, Height = 650, Content = view, ShowActivated = false };
            window.Show();
            await view.EnsureCoreWebView2Async(environment);
            await BrowserLoader.NavigateAsync(view.CoreWebView2, "https://www.cloudflare.com/cdn-cgi/trace", TimeSpan.FromSeconds(25), timeout.Token);
            var trace = JsonSerializer.Deserialize<string>(await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText")) ?? "";
            Assert(trace.Split('\n').Any(line => line.Trim() == "ip=" + ip), "Browser IP does not match tunnel");
            await BrowserLoader.NavigateAsync(view.CoreWebView2, "https://discord.com/app", TimeSpan.FromSeconds(45), timeout.Token);
            await BrowserLoader.WaitForContentAsync(view.CoreWebView2, TimeSpan.FromSeconds(30), timeout.Token);
            Console.WriteLine("PASS: Discord displayed content in WebView2 through the same verified tunnel.");
        }
        finally { view?.Dispose(); window?.Close(); ConfigurationStore.DeleteTemporary(temporary); }
    }
    private static void SavePreview(Window window, string filename)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", filename));
        encoder.Save(output);
    }
    private sealed class FakeProvider : IVpnProvider
    {
        public string? Code;
        public bool Disposed;
        public async Task<JsonElement> LoginAsync(string username, string password, Func<Task<string>> twoFactor, CancellationToken token) { Code = await twoFactor(); return Session; }
        public Task<JsonElement> RestoreAsync(JsonElement session, CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(new { AccessToken = "rotated-access", RefreshToken = "rotated-refresh", UID = "fixture-uid" }));
        public Task<VpnCountries> CountriesAsync(bool freeOnly, CancellationToken token) => Task.FromResult(new VpnCountries(["JP", "NL"], false));
        public Task<VpnConfiguration> GenerateAsync(string country, bool freeOnly, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
