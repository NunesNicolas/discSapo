using System.IO;
using System.Text;
using System.Windows;
using DiscordVpn;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var view = new WebView2();
        var window = new Window { Content = view, Width = 600, Height = 400, ShowActivated = false };
        window.Loaded += async (_, _) => {
            try { await CheckAsync(window, view); }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            finally { view.Dispose(); window.Close(); }
        };
        app.Run(window);
    }

    private static async Task CheckAsync(Window window, WebView2 view)
    {
        // Synthetic devices only; no access to the user's microphone or camera.
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppContext.BaseDirectory, "test-profile"), new CoreWebView2EnvironmentOptions("--use-fake-device-for-media-stream --proxy-server=http://127.0.0.1:9"));
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        core.AddWebResourceRequestedFilter("https://permission.test/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) => args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body>Permission test</body></html>")), 200, "OK", "Content-Type: text/html");
        await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, "https://permission.test", CoreWebView2PermissionState.Deny);
        int prompts = 0;
        using (var permissions = new MediaPermissions(core, window, (origin, kind) => {
            if (origin != "https://permission.test") throw new Exception("Wrong requesting origin.");
            prompts++;
            return Task.FromResult(kind == CoreWebView2PermissionKind.Microphone);
        }))
        {
            await permissions.InitializeAsync();
            await NavigateAsync(core);
            await CaptureAsync(core, "audio", "allowed");
            Console.WriteLine("PASS: saved denial cleared; microphone granted after approval.");
            await CaptureAsync(core, "audio", "allowed");
            if (prompts != 1) throw new Exception("Session decision was not reused.");
            Console.WriteLine("PASS: session decision reused.");
            await CaptureAsync(core, "video", "NotAllowedError");
            if (prompts != 2) throw new Exception("Camera request did not ask separately.");
            Console.WriteLine("PASS: camera denied independently.");
        }
        using (var permissions = new MediaPermissions(core, window, (_, _) => { prompts++; return Task.FromResult(false); }))
        {
            await permissions.InitializeAsync();
            await NavigateAsync(core);
            await CaptureAsync(core, "audio", "NotAllowedError");
            if (prompts != 3) throw new Exception("A previous grant survived the session.");
            Console.WriteLine("PASS: new session asks again and can deny microphone.");
        }
    }

    private static async Task NavigateAsync(CoreWebView2 core)
    {
        var loaded = new TaskCompletionSource();
        void Complete(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        core.NavigationCompleted += Complete;
        try { core.Navigate("https://permission.test/"); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { core.NavigationCompleted -= Complete; }
    }

    private static async Task CaptureAsync(CoreWebView2 core, string kind, string expected)
    {
        await core.ExecuteScriptAsync($"window.captureResult='pending'; navigator.mediaDevices.getUserMedia({{{kind}:true}}).then(s=>{{s.getTracks().forEach(t=>t.stop());window.captureResult='allowed'}}).catch(e=>window.captureResult=e.name)");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var result = await core.ExecuteScriptAsync("window.captureResult");
            if (result == $"\"{expected}\"") return;
            if (result != "\"pending\"") throw new Exception($"Expected {expected}, got {result}.");
            await Task.Delay(100);
        }
        throw new Exception("Permission request did not complete.");
    }
}
