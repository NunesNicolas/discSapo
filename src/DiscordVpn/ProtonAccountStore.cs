using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiscordVpn;

internal record ProtonAccount(JsonElement Session, string Country, bool FreeOnly, bool Active, string UserName = "");

internal sealed class ProtonAccountStore
{
    private readonly string path;
    public ProtonAccountStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn", "Configuration", "account.bin");
    public ProtonAccount? Load()
    {
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<ProtonAccount>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(ProtonAccount account)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(account);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Deactivate() { var account = Load(); if (account is not null) Save(account with { Active = false }); }
    public void Forget() => File.Delete(path);
}
