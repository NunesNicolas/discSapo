using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DiscordVpn;

internal sealed class TunnelSession : IDisposable
{
    private Process? process;
    private IntPtr job;
    private HttpClient? client;
    public int Port { get; private set; }

    public async Task<string> StartAsync(string config, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "wiresocks.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Motor WireGuard ausente. Execute scripts/build.ps1 para gerar o aplicativo completo.");
        // Choose an ephemeral loopback port; startup also checks child liveness.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--config", config, "--socks-addr", $"127.0.0.1:{Port}", "--silent" })
            start.ArgumentList.Add(argument);
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new InvalidOperationException("Não foi possível criar o supervisor do túnel.");
        var limits = new JobLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            throw new InvalidOperationException("Não foi possível configurar o supervisor do túnel.");
        process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o túnel.");
        if (!AssignProcessToJobObject(job, process.Handle))
            throw new InvalidOperationException("Não foi possível supervisionar o túnel.");
        // Drain without logging: upstream diagnostics may contain configuration data.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        client = new HttpClient(new HttpClientHandler {
            Proxy = new WebProxy($"socks5://127.0.0.1:{Port}"), UseProxy = true,
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(10) };
        for (var attempt = 0; attempt < 6; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("O túnel encerrou. Verifique o arquivo WireGuard e sua validade.");
            try { return await VerifyConnectionAsync(token); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            if (attempt < 5) await Task.Delay(1000, token);
        }
        throw new InvalidOperationException("Não foi possível estabelecer a conexão com o servidor VPN. Escolha outro país em Entrar na Proton e tente novamente.");
    }

    public async Task<string> VerifyConnectionAsync(CancellationToken token)
    {
        if (process is null || process.HasExited || client is null)
            throw new InvalidOperationException("O túnel foi encerrado.");
        using var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", token);
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadAsStringAsync(token)).Split('\n')
            .Select(line => line.Trim().Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);
        if (!fields.TryGetValue("ip", out var ip) || !IPAddress.TryParse(ip, out _))
            throw new InvalidOperationException("Não foi possível verificar o IP de saída.");
        if (process.HasExited) throw new InvalidOperationException("O túnel foi encerrado.");
        return ip;
    }

    public void Dispose()
    {
        client?.Dispose();
        client = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); process = null; }
        }
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JobLimits info, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
