using System.Net.Http;
using System.Text.Json;

namespace DiscordVpn;

internal static class ProtonProfile
{
    public static async Task<string?> GetNameAsync(JsonElement session)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://vpn-api.proton.me/core/v4/users");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.GetProperty("AccessToken").GetString());
        request.Headers.TryAddWithoutValidation("x-pm-uid", session.GetProperty("UID").GetString());
        request.Headers.TryAddWithoutValidation("x-pm-appversion", "linux-vpn@4.18.1");
        request.Headers.TryAddWithoutValidation("User-Agent", "ProtonVPN/4.18.1 (Linux; Ubuntu)");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.TryGetProperty("User", out var user)) return null;
        return user.TryGetProperty("Name", out var name) ? name.GetString() : null;
    }
}
