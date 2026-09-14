using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace DiscordVpn;

internal sealed class MediaPermissions : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly Window owner;
    private readonly Func<string, CoreWebView2PermissionKind, Task<bool>> ask;
    private readonly SemaphoreSlim queue = new(1);
    private readonly Dictionary<(string, CoreWebView2PermissionKind), bool> decisions = new();
    private Window? prompt;
    private bool disposed;

    public MediaPermissions(CoreWebView2 core, Window owner, Func<string, CoreWebView2PermissionKind, Task<bool>>? ask = null)
    {
        this.core = core;
        this.owner = owner;
        this.ask = ask ?? AskAsync;
    }

    public async Task InitializeAsync()
    {
        // Migrate the automatic denials written by earlier app versions.
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        foreach (var setting in settings)
            if (IsMedia(setting.PermissionKind))
                await core.Profile.SetPermissionStateAsync(setting.PermissionKind, setting.PermissionOrigin, CoreWebView2PermissionState.Default);
        if (!disposed) core.PermissionRequested += Requested;
    }

    private static bool IsMedia(CoreWebView2PermissionKind kind) => kind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone;

    private async void Requested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.SavesInProfile = false;
        args.State = CoreWebView2PermissionState.Deny;
        if (disposed || !IsMedia(args.PermissionKind) || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var deferral = args.GetDeferral();
        await queue.WaitAsync();
        try
        {
            if (disposed) return;
            var key = (origin, args.PermissionKind);
            if (!decisions.TryGetValue(key, out var allow))
            {
                allow = await ask(origin, args.PermissionKind);
                if (disposed) return;
                decisions[key] = allow;
            }
            args.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        catch (Exception ex) { BrowserDiagnostics.Write($"Permission failure type={ex.GetType().Name}"); }
        finally
        {
            queue.Release();
            try { deferral.Dispose(); }
            catch (Exception ex) { BrowserDiagnostics.Write($"Permission completion type={ex.GetType().Name}"); }
        }
    }

    private Task<bool> AskAsync(string origin, CoreWebView2PermissionKind kind)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = kind == CoreWebView2PermissionKind.Camera ? "câmera" : "microfone";
        var dialog = new Window { Owner = owner, Title = "Permissão de dispositivo", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"Permitir acesso ao seu {device}?", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = origin, Margin = new Thickness(0, 14, 0, 8), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Sua escolha vale até desconectar. Reconecte para alterar a permissão.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var deny = new Button { Content = "Não permitir", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 12, 0) };
        var allow = new Button { Content = "Permitir", Padding = new Thickness(12, 8, 12, 8) };
        deny.Click += (_, _) => dialog.Close();
        allow.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Closed += (_, _) => { if (prompt == dialog) prompt = null; result.TrySetResult(false); };
        prompt = dialog;
        dialog.Show();
        return result.Task;
    }

    public void Dispose()
    {
        disposed = true;
        core.PermissionRequested -= Requested;
        prompt?.Close();
        decisions.Clear();
    }
}
