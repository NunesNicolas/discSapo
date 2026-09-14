using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiscordVpn;

var testFile = Path.Combine(Path.GetTempPath(), $"discordvpn-test-{Guid.NewGuid():N}.conf");
var before = Process.GetProcessesByName("wiresocks").Select(p => p.Id).ToHashSet();
try
{
    File.WriteAllText(testFile, "invalid configuration");
    using (var session = new TunnelSession())
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
    {
        try { await session.StartAsync(testFile, timeout.Token); throw new Exception("Invalid configuration accepted."); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: invalid configuration refused."); }
    }
    File.WriteAllText(testFile, """
        [Interface]
        PrivateKey = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=
        Address = 10.2.0.2/32
        DNS = 10.2.0.1
        [Peer]
        PublicKey = AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=
        AllowedIPs = 0.0.0.0/0
        Endpoint = 127.0.0.1:9
        """);
    // An unreachable local WireGuard peer must never yield a verified IP.
    int port;
    using (var session = new TunnelSession())
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
    {
        try { await session.StartAsync(testFile, timeout.Token); throw new Exception("Unreachable tunnel accepted."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Console.WriteLine("PASS: unreachable tunnel cancels without direct fallback."); }
        port = session.Port;
    }
    using (var probe = new TcpClient())
    {
        try { await probe.ConnectAsync(IPAddress.Loopback, port); throw new Exception("Proxy still listening after disposal."); }
        catch (SocketException) { Console.WriteLine("PASS: proxy closes on disposal."); }
    }
    await Task.Delay(500);
    if (Process.GetProcessesByName("wiresocks").Any(p => !before.Contains(p.Id)))
        throw new Exception("Orphan tunnel process detected.");
    Console.WriteLine("PASS: no orphan tunnel process.");
}
finally { File.Delete(testFile); }
