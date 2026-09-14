using Microsoft.Web.WebView2.Core;

namespace DiscordVpn;

internal static class BrowserLoader
{
    public static async Task NavigateAsync(CoreWebView2 core, string url, TimeSpan timeout, CancellationToken token)
    {
        var completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            // Client-side redirects can abort the outgoing navigation.
            if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                completion.TrySetResult(args);
        }
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(url);
            var result = await completion.Task.WaitAsync(timeout, token);
            if (!result.IsSuccess || result.HttpStatusCode >= 400)
                throw new InvalidOperationException($"O Discord não carregou (HTTP {result.HttpStatusCode}; {result.WebErrorStatus}). Tente outro servidor VPN ou conecte novamente.");
        }
        finally { core.NavigationCompleted -= Completed; }
    }

    public static async Task WaitForContentAsync(CoreWebView2 core, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            // An HTTP 200 with an empty SPA shell is not a rendered application.
            var content = await core.ExecuteScriptAsync("""
                (() => {
                    const body = document.body;
                    if (!body) return false;
                    if ((body.innerText || '').trim().length > 0) return true;
                    return [...body.querySelectorAll('input, button')].some(element => {
                        const rect = element.getBoundingClientRect();
                        const style = getComputedStyle(element);
                        return rect.width >= 16 && rect.height >= 16 && style.visibility !== 'hidden' && style.display !== 'none' && style.opacity !== '0';
                    });
                })()
                """).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (content == "true") return;
            await Task.Delay(500, token);
        }
        throw new TimeoutException("O Discord respondeu, mas não exibiu conteúdo.");
    }
}
