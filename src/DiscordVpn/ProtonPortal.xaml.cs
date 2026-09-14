using System.IO;
using System.Windows;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DiscordVpn;

public partial class ProtonPortal : Window
{
    internal const string PortalUrl = "https://account.protonvpn.com/";
    private readonly ConfigurationStore store;
    private readonly string? profilePath;
    private CoreWebView2DownloadOperation? download;
    private string? pendingPath;
    private string? downloadName;
    private bool closed;
    public bool Imported { get; private set; }
    public event EventHandler? ConfigurationImported;

    internal ProtonPortal(ConfigurationStore store, string? profilePath = null)
    {
        InitializeComponent();
        this.store = store;
        this.profilePath = profilePath;
        Loaded += Initialize;
        Closed += (_, _) => {
            closed = true;
            if (download is not null)
            {
                download.StateChanged -= DownloadChanged;
                download.BytesReceivedChanged -= ProgressChanged;
                if (download.State == CoreWebView2DownloadState.InProgress) download.Cancel();
            }
            PortalView.Dispose();
            ConfigurationStore.DeleteTemporary(pendingPath);
        };
    }

    internal static bool IsProtonPage(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
            (uri.Host == "account.protonvpn.com" || uri.Host == "account.proton.me");
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = profilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn", "ProtonPortal");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions("--no-proxy-server"));
            if (closed) return;
            await PortalView.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = PortalView.CoreWebView2;
            core.HistoryChanged += (_, _) => {
                PortalBack.IsEnabled = core.CanGoBack;
                PortalForward.IsEnabled = core.CanGoForward;
            };
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.WebMessageReceived += ConfigurationMessage;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ProtonDownloadBridge.Script);
            if (closed) return;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, args) => {
                args.SavesInProfile = false;
                // The download handler still validates every file and its origin.
                args.State = args.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads && IsProtonPage(args.Uri)
                    ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            core.NavigationStarting += (_, args) => {
                // Blob downloads are delivered by DownloadStarting, not rendered as pages.
                if (!IsProtonPage(args.Uri) && !(args.Uri.StartsWith("blob:") && IsProtonPage(args.Uri[5..])))
                { args.Cancel = true; PortalStatus.Text = "Este endereço não pertence ao portal de conta Proton."; }
            };
            core.NavigationCompleted += (_, args) => {
                if (!args.IsSuccess && !Imported) PortalStatus.Text = $"O portal não carregou ({args.WebErrorStatus}). Clique em Reabrir portal para tentar novamente.";
            };
            core.NewWindowRequested += (_, args) => { args.Handled = true; if (IsProtonPage(args.Uri)) core.Navigate(args.Uri); };
            core.DownloadStarting += DownloadStarting;
            core.Navigate(PortalUrl);
        }
        catch (Exception) { if (!closed) PortalStatus.Text = "Não foi possível abrir o portal. Feche esta janela e tente novamente."; }
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Handled = true;
        var uri = args.DownloadOperation.Uri;
        var trustedDownload = IsProtonPage(uri) || (uri.StartsWith("blob:") && IsProtonPage(uri[5..]));
        if (Imported) { args.Cancel = true; return; }
        if (closed || download is not null || !IsProtonPage(PortalView.CoreWebView2.Source) || !trustedDownload ||
            !Path.GetExtension(args.ResultFilePath).Equals(".conf", StringComparison.OrdinalIgnoreCase))
        { args.Cancel = true; PortalStatus.Text = "Baixe uma configuração WireGuard (.conf) pelo portal Proton."; return; }
        try
        {
            Imported = false;
            UseConfiguration.Visibility = Visibility.Collapsed;
            downloadName = Path.GetFileName(args.ResultFilePath);
            pendingPath = store.NewTemporaryPath();
            args.ResultFilePath = pendingPath;
            download = args.DownloadOperation;
            download.StateChanged += DownloadChanged;
            download.BytesReceivedChanged += ProgressChanged;
            PortalStatus.Text = "Recebendo configuração WireGuard…";
            Dispatcher.BeginInvoke(new Action(() => DownloadChanged(download, EventArgs.Empty)));
        }
        catch (Exception) { args.Cancel = true; PortalStatus.Text = "Não foi possível salvar a configuração."; }
    }

    private void ProgressChanged(object? sender, object args)
    {
        if (download?.BytesReceived > 65536) download.Cancel();
    }

    private void DownloadChanged(object? sender, object args)
    {
        if (closed || download is null || download.State == CoreWebView2DownloadState.InProgress) return;
        var operation = download;
        download = null;
        operation.StateChanged -= DownloadChanged;
        operation.BytesReceivedChanged -= ProgressChanged;
        try
        {
            if (operation.State != CoreWebView2DownloadState.Completed) throw new InvalidOperationException();
            store.Save(operation.ResultFilePath);
            CompleteImport();
        }
        catch (Exception) { PortalStatus.Text = "O download falhou ou não contém uma configuração WireGuard válida. Gere e baixe novamente."; }
        finally { ConfigurationStore.DeleteTemporary(pendingPath); pendingPath = null; }
    }

    private void ConfigurationMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closed || Imported || !IsProtonPage(args.Source) || !IsProtonPage(PortalView.CoreWebView2.Source)) return;
        try
        {
            if (args.WebMessageAsJson.Length > 400000) throw new InvalidOperationException();
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var message = json.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "wireguard-error") throw new InvalidOperationException();
            if (type != "wireguard-config") return;
            var name = message.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) return;
            Imported = false;
            downloadName = Path.GetFileName(name);
            PortalStatus.Text = "Configurando sua conexão…";
            store.SaveText(message.GetProperty("content").GetString() ?? "");
            CompleteImport();
        }
        catch (Exception)
        {
            PortalStatus.Text = "O arquivo não contém uma configuração WireGuard válida ou não pôde ser importado. Gere e baixe novamente.";
        }
    }

    private void CompleteImport()
    {
        store.VerifySaved();
        ConfigurationImported?.Invoke(this, EventArgs.Empty);
        Imported = true;
        PortalStatus.Text = "Configuração pronta. Voltando ao app para conectar…";
        UseConfiguration.Visibility = Visibility.Visible;
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => PortalView.CoreWebView2?.Navigate(PortalUrl);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Back_Click(object sender, RoutedEventArgs e) { if (PortalView.CoreWebView2?.CanGoBack == true) PortalView.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (PortalView.CoreWebView2?.CanGoForward == true) PortalView.CoreWebView2.GoForward(); }
}
