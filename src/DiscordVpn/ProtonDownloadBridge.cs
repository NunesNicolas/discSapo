namespace DiscordVpn;

internal static class ProtonDownloadBridge
{
    // Only generated configuration downloads are inspected, never forms or login data.
    internal const string Script = """
        (() => {
            if (window.top !== window || location.protocol !== 'https:' ||
                !['account.protonvpn.com', 'account.proton.me'].includes(location.hostname)) return;
            const nativeClick = HTMLAnchorElement.prototype.click;
            const capture = anchor => {
                if (!anchor || !/\.conf$/i.test(anchor.download || '') ||
                    !/^(blob:|data:)/i.test(anchor.href)) return false;
                const name = anchor.download;
                const href = anchor.href;
                // Start reading before the page can revoke its temporary blob URL.
                fetch(href).then(response => response.blob()).then(async blob => {
                    if (blob.size > 65536) throw new Error('oversized');
                    const content = await blob.text();
                    window.chrome.webview.postMessage({type:'wireguard-config', name, content});
                }).catch(() => window.chrome.webview.postMessage({type:'wireguard-error'}));
                return true;
            };
            HTMLAnchorElement.prototype.click = function() {
                if (!capture(this)) return nativeClick.apply(this, arguments);
            };
            document.addEventListener('click', event => {
                const anchor = event.target.closest?.('a[download]');
                if (capture(anchor)) { event.preventDefault(); event.stopImmediatePropagation(); }
            }, true);
        })();
        """;
}
