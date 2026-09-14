using Velopack;
using Velopack.Sources;

namespace DiscordVpn;

internal sealed record AvailableAppUpdate(string Version, UpdateInfo NativeInfo);

internal sealed class AppUpdateService
{
    internal const string RepositoryUrl = "https://github.com/NunesNicolas/discSapo";
    private readonly UpdateManager manager;

    public AppUpdateService() => manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));

    public async Task<AvailableAppUpdate?> CheckAsync()
    {
        var info = await manager.CheckForUpdatesAsync();
        return info is null ? null : new(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AvailableAppUpdate update, Action<int> progress)
        => manager.DownloadUpdatesAsync(update.NativeInfo, progress);

    public void ApplyAndRestart(AvailableAppUpdate update) => manager.ApplyUpdatesAndRestart(update.NativeInfo.TargetFullRelease);
}
