using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace DiscordVpn;

public partial class MainWindow : Window
{
    private string? configPath;
    private readonly ConfigurationStore configurationStore;
    private readonly ProtonAccountStore accountStore;
    private bool useDirectProton;
    private long certificateExpires;
    private bool navigationReady;
    private readonly Action? connectionRequested;
    private bool useSavedConfiguration;
    private string? sessionConfigPath;
    private TunnelSession? tunnel;
    private WebView2? browser;
    private MediaPermissions? mediaPermissions;
    private CancellationTokenSource? connection;
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool verifying;
    private bool closed;
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long? hoverHideAt;
    private readonly AppUpdateService updateService = new();
    private AvailableAppUpdate? availableUpdate;

    public MainWindow() : this(new ConfigurationStore()) { }

    internal MainWindow(ConfigurationStore configurationStore, Action? connectionRequested = null)
    {
        this.configurationStore = configurationStore;
        accountStore = new ProtonAccountStore(configurationStore.AccountPath);
        this.connectionRequested = connectionRequested;
        InitializeComponent();
        UpdatePopup.CustomPopupPlacementCallback = (_, _, _) => [new System.Windows.Controls.Primitives.CustomPopupPlacement(
            new Point(Math.Max(12, BrowserHost.ActualWidth - 356), Math.Max(12, BrowserHost.ActualHeight - 150)),
            System.Windows.Controls.Primitives.PopupPrimaryAxis.None)];
        WindowTheme.Apply(this, WindowTheme.IsDark());
        SystemEvents.UserPreferenceChanged += ThemePreferenceChanged;
        Activated += (_, _) => WindowTheme.Apply(this, WindowTheme.IsDark());
        using (var stream = Application.GetResourceStream(new Uri("/DiscordVpn;component/Assets/Logo.xaml", UriKind.Relative)).Stream)
        {
            var logo = (System.Windows.Media.DrawingImage)System.Windows.Markup.XamlReader.Load(stream);
            logo.Freeze();
            HeaderLogo.Source = WelcomeLogo.Source = logo;
        }
        try { if (configurationStore.Exists) SelectSavedConfiguration(); }
        catch (Exception) { DetailText.Text = "Não foi possível ler a configuração salva. Configure o Proton novamente ou importe um arquivo .conf."; }
        monitor.Tick += Monitor_Tick;
        UpdateDirectAccount();
        Loaded += async (_, _) => { await UpdateAccountProfileAsync(); await CheckForUpdatesAsync(); };
        hoverTimer.Tick += (_, _) => PollHoverControls();
        Deactivated += (_, _) => HoverControls.IsOpen = false;
        LocationChanged += (_, _) => HoverControls.IsOpen = false;
        SizeChanged += (_, _) => HoverControls.IsOpen = false;
        PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.H && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) {
                SetControlsVisible(AppHeader.Visibility != Visibility.Visible); e.Handled = true;
            }
        };
        Closed += (_, _) => { closed = true; SystemEvents.UserPreferenceChanged -= ThemePreferenceChanged; Disconnect(); hoverTimer.Stop(); HoverControls.IsOpen = UpdatePopup.IsOpen = false; };
    }

    private void SelectSavedConfiguration()
    {
        configurationStore.VerifySaved();
        useSavedConfiguration = true;
        configPath = null;
        ConfigText.Text = "Configuração Proton salva neste usuário do Windows.";
        DetailText.Text = "Configuração importada e selecionada automaticamente. Clique em Conectar para abrir o Discord.";
        StatusText.Text = "Configuração Proton pronta · clique em Conectar";
        ConnectButton.IsEnabled = true;
    }

    private void Proton_Click(object sender, RoutedEventArgs e)
    {
        var login = new ProtonLogin(accountStore) { Owner = this };
        login.ShowDialog();
        UpdateDirectAccount();
        _ = UpdateAccountProfileAsync();
        if (login.OpenPortal) Portal_Click(sender, e);
        else if (login.Ready) Connect_Click(sender, e);
    }

    private void UpdateDirectAccount()
    {
        var wasDirect = useDirectProton;
        try
        {
            var saved = accountStore.Load();
            SetAccountCard(saved);
            useDirectProton = saved?.Active == true;
            if (useDirectProton)
            {
                ConfigText.Text = "Proton · " + (string.IsNullOrEmpty(saved!.Country) ? "melhor servidor disponível" : saved.Country);
                DetailText.Text = "Sua sessão está protegida neste usuário do Windows. A configuração será gerada ao conectar.";
                StatusText.Text = "Proton pronta · clique em Conectar";
            }
            else if (wasDirect)
            {
                if (useSavedConfiguration) SelectSavedConfiguration();
                else
                {
                    ConfigText.Text = "Pronto para começar";
                    DetailText.Text = "Use Entrar na Proton ou importe uma configuração.";
                    StatusText.Text = "Desconectado · configure sua conexão";
                }
            }
        }
        catch (Exception) { useDirectProton = false; DetailText.Text = "Não foi possível recuperar sua sessão. Use Entrar na Proton para entrar novamente."; }
        ConnectButton.IsEnabled = true;
    }

    private void Portal_Click(object sender, RoutedEventArgs e)
    {
        var portal = CreateProtonPortal();
        portal.ShowDialog();
    }

    internal ProtonPortal CreateProtonPortal(string? profilePath = null)
    {
        var portal = new ProtonPortal(configurationStore, profilePath) { Owner = this };
        portal.ConfigurationImported += (_, _) => {
            accountStore.Deactivate();
            useDirectProton = false;
            SelectSavedConfiguration();
            Dispatcher.BeginInvoke(new Action(() => {
                if (closed) return;
                portal.Close();
                if (connectionRequested is not null) connectionRequested();
                else Connect_Click(this, new RoutedEventArgs());
            }));
        };
        return portal;
    }

    private void ToggleControls_Click(object sender, RoutedEventArgs e) => SetControlsVisible(AppHeader.Visibility != Visibility.Visible);
    private void ThemePreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => {
            if (!closed) WindowTheme.Apply(this, WindowTheme.IsDark());
        }));
    }
    internal void SetControlsVisible(bool visible)
    {
        AppHeader.Visibility = AppFooter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HoverControls.IsOpen = false;
        hoverHideAt = null;
        if (visible) hoverTimer.Stop(); else hoverTimer.Start();
    }
    private void AccountMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = AccountMenuButton.ContextMenu;
        menu.PlacementTarget = AccountMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private void PollHoverControls()
    {
        // WebView2 owns an HWND: WPF MouseMove/overlays cannot cover its airspace.
        // A native cursor check and Popup keep the entire content area usable.
        if (closed || !IsActive || AppHeader.Visibility == Visibility.Visible || !GetCursorPos(out var cursor)) { HoverControls.IsOpen = false; return; }
        var point = BrowserHost.PointFromScreen(new Point(cursor.X, cursor.Y));
        UpdateHoverRegion(point.X, point.Y);
    }
    internal void UpdateHoverRegion(double x, double y, long? timestamp = null)
    {
        if (AppHeader.Visibility == Visibility.Visible) { HoverControls.IsOpen = false; hoverHideAt = null; return; }
        var inside = x >= 0 && x < BrowserHost.ActualWidth && y >= 0 && y <= (HoverControls.IsOpen ? 52 : 14);
        if (inside) { HoverControls.IsOpen = true; hoverHideAt = null; return; }
        if (!HoverControls.IsOpen) { hoverHideAt = null; return; }
        var now = timestamp ?? Environment.TickCount64;
        hoverHideAt ??= now + 900;
        if (now >= hoverHideAt) { HoverControls.IsOpen = false; hoverHideAt = null; }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    private void SetAccountCard(ProtonAccount? account)
    {
        var name = account?.UserName;
        AccountName.Text = account is null ? "Conta Proton" : string.IsNullOrWhiteSpace(name) ? "Conta conectada" : name;
        AccountInitial.Text = string.IsNullOrWhiteSpace(name) ? "" : System.Globalization.StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant();
        AccountInitial.Visibility = string.IsNullOrWhiteSpace(name) ? Visibility.Collapsed : Visibility.Visible;
        AccountIcon.Visibility = AccountInitial.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task UpdateAccountProfileAsync()
    {
        try {
            var saved = accountStore.Load();
            if (saved is null || !string.IsNullOrWhiteSpace(saved.UserName)) return;
            var name = await ProtonProfile.GetNameAsync(saved.Session);
            var latest = accountStore.Load();
            if (closed || string.IsNullOrWhiteSpace(name) || latest is null || latest.Session.GetProperty("UID").GetString() != saved.Session.GetProperty("UID").GetString()) return;
            latest = latest with { UserName = name };
            accountStore.Save(latest); SetAccountCard(latest);
        } catch (Exception) { /* A profile lookup must not block connecting. */ }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            availableUpdate = await updateService.CheckAsync();
            if (closed || availableUpdate is null) return;
            UpdateDescription.Text = $"A versão {availableUpdate.Version} do discSapo está pronta para instalar.";
            UpdatePopup.IsOpen = true;
        }
        catch (Velopack.Exceptions.NotInstalledException) { }
        catch (Exception ex) { BrowserDiagnostics.Write($"Update check failure type={ex.GetType().Name}; HRESULT={ex.HResult}"); }
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => UpdatePopup.IsOpen = false;

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null) return;
        UpdateButton.IsEnabled = DismissUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateDescription.Text = "Baixando a atualização… 0%";
        try
        {
            await updateService.DownloadAsync(availableUpdate, value => Dispatcher.BeginInvoke(new Action(() => {
                UpdateProgress.Value = value;
                UpdateDescription.Text = $"Baixando a atualização… {value}%";
            })));
            UpdateDescription.Text = "Instalando e reiniciando…";
            Disconnect();
            updateService.ApplyAndRestart(availableUpdate);
        }
        catch (Exception ex)
        {
            BrowserDiagnostics.Write($"Update failure type={ex.GetType().Name}; HRESULT={ex.HResult}");
            UpdateDescription.Text = "Não foi possível atualizar agora. Verifique sua conexão e tente novamente.";
            UpdateButton.IsEnabled = DismissUpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (navigationReady && browser?.CoreWebView2.CanGoBack == true) browser.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (navigationReady && browser?.CoreWebView2.CanGoForward == true) browser.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (navigationReady) browser?.CoreWebView2.Reload(); }
    private void UpdateNavigation()
    {
        BackButton.IsEnabled = navigationReady && browser?.CoreWebView2.CanGoBack == true;
        ForwardButton.IsEnabled = navigationReady && browser?.CoreWebView2.CanGoForward == true;
        ReloadButton.IsEnabled = navigationReady;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "WireGuard (*.conf)|*.conf", Title = "Selecione a configuração WireGuard" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            ConfigurationStore.ReadValidated(picker.FileName);
            accountStore.Deactivate();
            useDirectProton = false;
            useSavedConfiguration = false;
            configPath = picker.FileName;
            ConfigText.Text = $"Configuração selecionada: {Path.GetFileName(configPath)}";
            DetailText.Text = "A configuração será lida do arquivo original, sem copiar a chave para o aplicativo. Câmera e microfone dependem da sua autorização.";
            ConnectButton.IsEnabled = true;
        }
        catch (Exception) { DetailText.Text = "Não foi possível ler uma configuração WireGuard válida. Verifique o arquivo e suas permissões."; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (connection is not null) return;
        if (!useDirectProton && !useSavedConfiguration && configPath is null) { Proton_Click(sender, e); return; }
        connection = new CancellationTokenSource();
        var current = connection;
        var token = current.Token;
        ProtonButton.IsEnabled = PortalButton.IsEnabled = ImportButton.IsEnabled = ConnectButton.IsEnabled = false;
        AccountMenuButton.IsEnabled = false;
        DisconnectButton.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = true;
        StatusText.Text = "Conectando · verificando conexão pelo túnel…";
        var stage = "proton-configuration";
        tunnel = new TunnelSession();
        try
        {
            if (useDirectProton)
            {
                StatusText.Text = "Proton · preparando sua conexão…";
                var saved = accountStore.Load() ?? throw new InvalidOperationException("Entre na Proton novamente para conectar.");
                using var provider = new ProtonVpnProvider();
                var session = await provider.RestoreAsync(saved.Session, token);
                token.ThrowIfCancellationRequested();
                accountStore.Save(saved with { Session = session });
                var generated = await provider.GenerateAsync(saved.Country, saved.FreeOnly, token);
                token.ThrowIfCancellationRequested();
                sessionConfigPath = configurationStore.NewTemporaryPath();
                await File.WriteAllTextAsync(sessionConfigPath, generated.Text, token);
                ConfigurationStore.ReadValidated(sessionConfigPath);
                certificateExpires = generated.Expires;
                ConfigText.Text = $"Proton · {generated.Country} · {generated.Server}";
            }
            else if (useSavedConfiguration) sessionConfigPath = configurationStore.Materialize();
            stage = "wireguard-connection";
            StatusText.Text = "Conectando · verificando conexão pelo túnel…";
            var ip = await tunnel.StartAsync(sessionConfigPath ?? configPath!, token);
            token.ThrowIfCancellationRequested();
            StatusText.Text = "VPN conectada · verificando navegador…";
            stage = "discord-browser";
            await OpenDiscordAsync(tunnel.Port, ip, token);
            token.ThrowIfCancellationRequested();
            StatusText.Text = $"Discord carregado · IP de saída: {ip}";
            monitor.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            BrowserDiagnostics.Write($"Connect failure stage={stage}; type={ex.GetType().Name}; HRESULT={ex.HResult}");
            if (connection == current && !closed)
            {
                Disconnect();
                DetailText.Text = (ex is InvalidOperationException ? ex.Message : "O carregamento não terminou. Verifique a conexão ou tente outro servidor VPN.") + $"\nDiagnóstico: {BrowserDiagnostics.LogPath}";
            }
        }
    }

    private async Task OpenDiscordAsync(int port, string expectedIp, CancellationToken token)
    {
        var options = new CoreWebView2EnvironmentOptions(
            $"--proxy-server=socks5://127.0.0.1:{port} --proxy-bypass-list=<-loopback> " +
            "--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE 127.0.0.1\" " +
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --disable-quic");
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
        token.ThrowIfCancellationRequested();
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(17, 19, 24) };
        browser = view;
        BrowserHost.Children.Add(view);
        await view.EnsureCoreWebView2Async(environment);
        token.ThrowIfCancellationRequested();
        var core = view.CoreWebView2;
        core.HistoryChanged += (_, _) => { if (browser == view) UpdateNavigation(); };
        BrowserDiagnostics.Attach(core);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        mediaPermissions = new MediaPermissions(core, this);
        await mediaPermissions.InitializeAsync();
        token.ThrowIfCancellationRequested();
        core.NavigationStarting += (_, args) => {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(uri.AbsoluteUri);
        };
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ProcessFailed += (_, _) => {
            if (browser != view) return;
            Disconnect();
            DetailText.Text = "O navegador foi encerrado após uma falha. Conecte novamente.";
        };
        // Verify the browser's own path as well as the independent tunnel probe.
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Loaded;
        try
        {
            core.Navigate("https://www.cloudflare.com/cdn-cgi/trace");
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), token))
                throw new InvalidOperationException("Não foi possível verificar a conexão do navegador.");
            token.ThrowIfCancellationRequested();
            var json = await core.ExecuteScriptAsync("document.body.innerText");
            token.ThrowIfCancellationRequested();
            var trace = JsonSerializer.Deserialize<string>(json) ?? "";
            var lines = trace.Split('\n').Select(line => line.Trim()).ToHashSet();
            if (!lines.Contains($"ip={expectedIp}"))
                throw new InvalidOperationException("A saída do navegador não corresponde à conexão verificada.");
        }
        finally { core.NavigationCompleted -= Loaded; }
        Welcome.Visibility = Visibility.Collapsed;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            StatusText.Text = attempt == 0 ? "VPN conectada · carregando Discord…" : "VPN conectada · tentando novamente sem cache…";
            try
            {
                await BrowserLoader.NavigateAsync(core, "https://discord.com/app", TimeSpan.FromSeconds(45), token);
                await BrowserLoader.WaitForContentAsync(core, TimeSpan.FromSeconds(30), token);
                await core.CallDevToolsProtocolMethodAsync("Page.resetNavigationHistory", "{}");
                token.ThrowIfCancellationRequested();
                navigationReady = true;
                UpdateNavigation();
                return;
            }
            catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested && (ex is TimeoutException || ex is InvalidOperationException))
            {
                BrowserDiagnostics.Write($"Retry after {ex.GetType().Name}");
                core.Stop();
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            }
        }
    }

    private async void Monitor_Tick(object? sender, EventArgs e)
    {
        if (verifying || tunnel is null || connection is null) return;
        verifying = true;
        var current = connection;
        try
        {
            if (certificateExpires > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= certificateExpires - 60)
                throw new InvalidOperationException("Certificate expires soon");
            await tunnel.VerifyConnectionAsync(current.Token);
        }
        catch (Exception)
        {
            if (connection == current && !closed)
            {
                Disconnect();
                DetailText.Text = "Conexão interrompida: não foi possível confirmar a conexão pelo túnel. Reconecte para continuar.";
            }
        }
        finally { verifying = false; }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

    private void Disconnect()
    {
        monitor.Stop();
        navigationReady = false;
        UpdateNavigation();
        SetControlsVisible(true);
        connection?.Cancel();
        connection?.Dispose();
        connection = null;
        mediaPermissions?.Dispose();
        mediaPermissions = null;
        if (browser is not null)
        {
            var view = browser;
            browser = null;
            BrowserHost.Children.Remove(view);
            view.Dispose();
        }
        tunnel?.Dispose();
        tunnel = null;
        ConfigurationStore.DeleteTemporary(sessionConfigPath);
        sessionConfigPath = null;
        certificateExpires = 0;
        Welcome.Visibility = Visibility.Visible;
        ProtonButton.IsEnabled = PortalButton.IsEnabled = ImportButton.IsEnabled = true;
        AccountMenuButton.IsEnabled = true;
        DisconnectButton.Visibility = Visibility.Collapsed;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        StatusText.Text = useDirectProton || useSavedConfiguration || configPath is not null
            ? "Desconectado · configuração pronta para conectar"
            : "Desconectado · configure sua conexão";
    }
}
