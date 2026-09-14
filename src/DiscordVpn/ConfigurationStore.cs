using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiscordVpn;

internal sealed class ConfigurationStore
{
    private readonly string root;
    public ConfigurationStore(string? root = null) => this.root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVpn", "Configuration");
    private string SavedPath => Path.Combine(root, "proton.bin");
    internal string AccountPath => Path.Combine(root, "account.bin");
    public bool Exists => File.Exists(SavedPath);
    public string NewTemporaryPath()
    {
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Guid.NewGuid():N}.conf");
    }
    public static string ReadValidated(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("Configuração muito grande.");
        var text = File.ReadAllText(path);
        Validate(text);
        return text;
    }
    private static void Validate(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new InvalidOperationException("Configuração muito grande.");
        if (!text.Contains("[Interface]") || !text.Contains("[Peer]") || !text.Contains("PrivateKey") || !text.Contains("Endpoint") || !text.Contains("DNS"))
            throw new InvalidOperationException("O arquivo baixado não é uma configuração WireGuard completa.");
    }
    public void Save(string downloadedPath) => SaveText(ReadValidated(downloadedPath));

    public void SaveText(string text)
    {
        Validate(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(root);
            var temporary = Path.Combine(root, $"{Guid.NewGuid():N}.bin");
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, SavedPath, true); }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Materialize()
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(SavedPath), null, DataProtectionScope.CurrentUser);
        var path = NewTemporaryPath();
        try { File.WriteAllBytes(path, bytes); return path; }
        catch { DeleteTemporary(path); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void VerifySaved()
    {
        var temporary = Materialize();
        try { ReadValidated(temporary); }
        finally { DeleteTemporary(temporary); }
    }
    public static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
