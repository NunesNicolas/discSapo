using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace DiscordVpn;

public partial class ProtonLogin : Window
{
    private readonly ProtonAccountStore store;
    private readonly Func<IVpnProvider> factory;
    private IVpnProvider? provider;
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource<string>? pendingCode;
    private JsonElement session;
    private string userName = "";
    private bool busy;
    private bool closed;
    internal bool OpenPortal { get; private set; }
    internal bool Ready { get; private set; }
    internal ProtonLogin(ProtonAccountStore store, Func<IVpnProvider>? factory = null)
    {
        this.store = store;
        this.factory = factory ?? (() => new ProtonVpnProvider());
        InitializeComponent();
        Loaded += async (_, _) => await RestoreAsync();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); pendingCode?.TrySetCanceled(); provider?.Dispose(); };
    }
    private async Task RestoreAsync()
    {
        await RunAsync(async () => {
            var saved = store.Load();
            if (saved is null) return;
            userName = saved.UserName ?? "";
            Status.Text = "Recuperando sua sessão…";
            provider = factory();
            session = await provider.RestoreAsync(saved.Session, lifetime.Token);
            store.Save(saved with { Session = session }); // Persist rotating refresh token immediately.
            FreeOnly.IsChecked = saved.FreeOnly;
            await ShowCountriesAsync(saved.Country);
        });
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Username.Text) || Password.Password.Length == 0) { Status.Text = "Preencha usuário e senha."; return; }
        await RunAsync(async () => {
            provider?.Dispose(); provider = factory();
            Status.Text = "Entrando na Proton…";
            var password = Password.Password; Password.Clear();
            userName = Username.Text.Trim();
            try { session = await provider.LoginAsync(Username.Text.Trim(), password, RequestCodeAsync, lifetime.Token); }
            finally { password = ""; TwoFactorPanel.Visibility = Visibility.Collapsed; }
            store.Save(new(session, "", true, false, userName));
            await ShowCountriesAsync("");
        });
    }
    private async Task ShowCountriesAsync(string country)
    {
        Credentials.Visibility = Visibility.Collapsed;
        Selection.Visibility = Visibility.Visible;
        Hint.Text = "Escolha onde deseja se conectar. A configuração é automática.";
        Status.Text = "Buscando países disponíveis…";
        Countries.ItemsSource = null;
        var locations = await provider!.CountriesAsync(FreeOnly.IsChecked == true, lifetime.Token);
        var codes = locations.Codes;
        FreeOnly.IsEnabled = locations.CanUsePaidServers;
        if (!locations.CanUsePaidServers) FreeOnly.IsChecked = true;
        PlanHint.Text = locations.CanUsePaidServers
            ? "Sua conta permite servidores Plus. A lista respeita o filtro de servidores gratuitos."
            : "Plano gratuito · mostrando apenas países e servidores incluídos na sua conta.";
        Countries.ItemsSource = new[] { new Country("", "Automático · melhor servidor") }.Concat(codes.Select(code => new Country(code, CountryName(code))).OrderBy(c => c.Label)).ToArray();
        Countries.SelectedValue = codes.Contains(country) ? country : "";
        Status.Text = "Pronto para conectar.";
    }
    private static string CountryName(string code)
    {
        var name = code switch {
            "BR" => "Brasil", "US" => "Estados Unidos", "NL" => "Países Baixos", "JP" => "Japão",
            "PL" => "Polônia", "RO" => "Romênia", "CH" => "Suíça", "DE" => "Alemanha",
            "FR" => "França", "GB" => "Reino Unido", "CA" => "Canadá", "ES" => "Espanha",
            "PT" => "Portugal", "IT" => "Itália", "AU" => "Austrália", "SG" => "Singapura",
            "SE" => "Suécia", "NO" => "Noruega", "AR" => "Argentina", "MX" => "México", _ => ""
        };
        if (name.Length == 0) { try { name = new RegionInfo(code).EnglishName; } catch (ArgumentException) { return code; } }
        return $"{name} ({code})";
    }
    private record Country(string Code, string Label) { public override string ToString() => Label; }
    private Task<string> RequestCodeAsync()
    {
        pendingCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TwoFactorPanel.Visibility = Visibility.Visible; Credentials.Visibility = Visibility.Collapsed;
        Status.Text = "Confirme a autenticação em duas etapas.";
        TwoFactor.Focus();
        return pendingCode.Task;
    }
    private void Code_Click(object sender, RoutedEventArgs e)
    {
        var code = TwoFactor.Password.Trim();
        if (code.Length != 6 || code.Any(c => c < '0' || c > '9')) { Status.Text = "Digite os 6 dígitos do autenticador."; return; }
        TwoFactor.Clear(); TwoFactorPanel.Visibility = Visibility.Collapsed;
        Status.Text = "Verificando código…";
        pendingCode?.TrySetResult(code);
    }
    private async void Filter_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ShowCountriesAsync(""));
    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Countries.SelectedValue is not string country) return;
        try { store.Save(new(session, country, FreeOnly.IsChecked == true, true, userName)); Ready = true; Close(); }
        catch (Exception) { Status.Text = "Não foi possível salvar a sessão protegida neste usuário do Windows."; }
    }
    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { store.Forget(); provider?.Dispose(); provider = null; session = default; Selection.Visibility = Visibility.Collapsed; Credentials.Visibility = Visibility.Visible; Status.Text = "Conta removida deste PC. Entre novamente para conectar."; }
        catch (Exception) { Status.Text = "Não foi possível remover a sessão salva."; }
    }
    private void Portal_Click(object sender, RoutedEventArgs e) { OpenPortal = true; Close(); }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; LoginButton.IsEnabled = Selection.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!closed) {
                Status.Text = ex is InvalidOperationException ? ex.Message : "Não foi possível acessar a Proton. Tente novamente ou use o portal.";
                Credentials.Visibility = Visibility.Visible; Selection.Visibility = TwoFactorPanel.Visibility = Visibility.Collapsed;
            }
        }
        finally { busy = false; if (!closed) { LoginButton.IsEnabled = Selection.IsEnabled = true; UseButton.IsEnabled = Countries.SelectedItem is not null; } }
    }
}
