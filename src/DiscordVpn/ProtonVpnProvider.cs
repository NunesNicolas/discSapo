using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DiscordVpn;

internal record VpnConfiguration(string Text, string Server, string Country, long Expires);
internal record VpnCountries(string[] Codes, bool CanUsePaidServers);

internal interface IVpnProvider : IDisposable
{
    Task<JsonElement> LoginAsync(string username, string password, Func<Task<string>> twoFactor, CancellationToken token);
    Task<JsonElement> RestoreAsync(JsonElement session, CancellationToken token);
    Task<VpnCountries> CountriesAsync(bool freeOnly, CancellationToken token);
    Task<VpnConfiguration> GenerateAsync(string country, bool freeOnly, CancellationToken token);
}

internal sealed class ProtonVpnProvider : IVpnProvider
{
    private readonly Process process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public ProtonVpnProvider(string? executable = null)
    {
        var path = executable ?? Path.Combine(AppContext.BaseDirectory, "proton-bridge.exe");
        if (!File.Exists(path)) throw new InvalidOperationException("O módulo Proton não foi encontrado. Execute a versão completa do aplicativo.");
        process = new Process { StartInfo = new ProcessStartInfo(path) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        }};
        process.Start();
        // Discard any unexpected diagnostics. Never log protocol data or API responses.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    public async Task<JsonElement> LoginAsync(string username, string password, Func<Task<string>> twoFactor, CancellationToken token)
        => (await RequestAsync(new { op = "login", username, password }, "session", token, twoFactor)).GetProperty("session").Clone();
    public async Task<JsonElement> RestoreAsync(JsonElement session, CancellationToken token)
        => (await RequestAsync(new { op = "restore", session }, "session", token)).GetProperty("session").Clone();
    public async Task<VpnCountries> CountriesAsync(bool freeOnly, CancellationToken token)
    {
        var response = await RequestAsync(new { op = "countries", freeOnly }, "countries", token);
        return new(response.GetProperty("countries").Deserialize<string[]>() ?? [], response.GetProperty("canUsePaidServers").GetBoolean());
    }
    public async Task<VpnConfiguration> GenerateAsync(string country, bool freeOnly, CancellationToken token)
    {
        var response = await RequestAsync(new { op = "generate", country, freeOnly }, "configuration", token);
        return new(response.GetProperty("configuration").GetString()!, response.GetProperty("server").GetString()!,
            response.GetProperty("country").GetString()!, response.GetProperty("expires").GetInt64());
    }

    private async Task<JsonElement> RequestAsync(object request, string expected, CancellationToken token, Func<Task<string>>? twoFactor = null)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var cancel = timeout.Token.Register(Kill);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null || line.Length > 262144) throw new InvalidOperationException("O módulo Proton encerrou a operação. Tente entrar novamente.");
                using var document = JsonDocument.Parse(line);
                var result = document.RootElement;
                var type = result.GetProperty("type").GetString();
                if (type == "twoFactor" && twoFactor is not null)
                {
                    var code = await twoFactor().WaitAsync(timeout.Token);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { op = "twoFactor", code }).AsMemory(), timeout.Token);
                    await process.StandardInput.FlushAsync(timeout.Token);
                    continue;
                }
                if (type == "error") throw new InvalidOperationException(ErrorMessage(result.GetProperty("code").GetString()));
                if (type != expected) throw new InvalidOperationException("Resposta inesperada do módulo Proton. Tente entrar novamente.");
                return result.Clone();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("A Proton demorou para responder. Tente entrar novamente."); }
        finally { gate.Release(); }
    }
    internal static string ErrorMessage(string? code) => code switch {
        "credentials" => "Usuário ou senha incorretos. Use sua conta Proton, não as credenciais OpenVPN.",
        "twoFactor" => "Não foi possível validar o código. Entre novamente e use um código atual do autenticador.",
        "portalRequired" => "Sua conta exige uma verificação adicional. Use Abrir portal Proton para concluir a configuração.",
        "clientVersion" => "A Proton exige uma atualização da integração. Use o portal Proton por enquanto.",
        "session" => "Sua sessão Proton expirou ou foi revogada. Abra Entrar na Proton para entrar novamente.",
        "servers" => "Esse país não tem servidores disponíveis para o seu plano. Abra Entrar na Proton e escolha um dos países disponíveis ou Automático.",
        _ => "A Proton não concluiu a solicitação. Verifique sua conexão e seu plano, tente novamente ou use o portal Proton."
    };
    private void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    public void Dispose() { if (disposed) return; disposed = true; Kill(); process.Dispose(); }
}
