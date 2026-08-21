using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ICloudCalendar.Web.Services;

public sealed record SoftwareUpdate(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? ArchiveUrl,
    string? ChecksumsUrl,
    string? Runtime);

public interface ISoftwareUpdateService
{
    Task<SoftwareUpdate> CheckAsync(CancellationToken cancellationToken = default);
    Task<SoftwareUpdate> StartAsync(CancellationToken cancellationToken = default);
}

public sealed class SoftwareUpdateService : ISoftwareUpdateService, IDisposable
{
    private const string Repository = "azeem-yousaf/linux-ical";
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public async Task<SoftwareUpdate> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxICloudCalendar/" + current);
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
            if (!response.IsSuccessStatusCode) return new(current, null, false, null, null, null, null);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return ParseRelease(current, document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(current, null, false, null, null, null, null);
        }
    }

    public async Task<SoftwareUpdate> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!await _startLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("An update is already being prepared.");
        try
        {
            var update = await CheckAsync(cancellationToken);
            if (!update.UpdateAvailable || update.LatestVersion is null || update.ArchiveUrl is null || update.ChecksumsUrl is null || update.Runtime is null)
                throw new InvalidOperationException("No compatible update is currently available.");
            var helper = Path.Combine(AppContext.BaseDirectory, "update-calendar.sh");
            if (!File.Exists(helper)) throw new InvalidOperationException("The update helper is not installed. Download this release manually once to enable in-app updates.");
            var startInfo = new ProcessStartInfo("systemd-run") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--user", "--collect", "--no-block", "--unit=linux-icloud-calendar-update", helper, update.LatestVersion, update.Runtime, update.ArchiveUrl, update.ChecksumsUrl })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The update process could not be started.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException("The update process could not be started.");
            return update;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public static SoftwareUpdate ParseRelease(string current, JsonElement release)
    {
        var latest = (release.GetProperty("tag_name").GetString() ?? string.Empty).TrimStart('v', 'V');
        var releaseUrl = release.GetProperty("html_url").GetString();
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null
        };
        var available = runtime is not null && Version.TryParse(latest, out var latestVersion)
            && Version.TryParse(current, out var currentVersion) && latestVersion > currentVersion;
        string? archiveUrl = null;
        string? checksumsUrl = null;
        if (available && release.TryGetProperty("assets", out var assets))
        {
            var expectedArchive = $"linux-icloud-calendar-v{latest}-{runtime}.tar.gz";
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var download = asset.GetProperty("browser_download_url").GetString();
                if (StringComparer.Ordinal.Equals(name, expectedArchive)) archiveUrl = ValidateDownloadUrl(download, latest, expectedArchive);
                if (StringComparer.Ordinal.Equals(name, "SHA256SUMS")) checksumsUrl = ValidateDownloadUrl(download, latest, "SHA256SUMS");
            }
        }
        available = available && archiveUrl is not null && checksumsUrl is not null;
        return new(current, latest, available, releaseUrl, archiveUrl, checksumsUrl, runtime);
    }

    private static string? ValidateDownloadUrl(string? value, string version, string asset)
    {
        var expected = $"https://github.com/{Repository}/releases/download/v{version}/{asset}";
        return StringComparer.Ordinal.Equals(value, expected) ? value : null;
    }

    public void Dispose() => _startLock.Dispose();
}
