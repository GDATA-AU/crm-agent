using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CrmAgent.Tray;

/// <summary>
/// Periodically checks GitHub Releases for a newer MSI and downloads it.
/// Raises <see cref="UpdateReady"/> when a new version has been downloaded
/// and is ready to install.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string GitHubOwner = "GDATA-AU";
    private const string GitHubRepo = "crm-agent";
    private const string ReleaseUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private const int CheckDelayMs = 30_000;         // first check 30s after startup
    private const int CheckIntervalMs = 4 * 60 * 60 * 1000; // then every 4 hours

    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _http;
    private volatile bool _checking;

    /// <summary>Fired on the UI thread when a new MSI has been downloaded and is ready to install.</summary>
    public event Action<string, string>? UpdateReady;

    /// <summary>The latest version available on GitHub, or null if not yet checked / up-to-date.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Local path to the downloaded MSI, or null if no update is pending.</summary>
    public string? DownloadedMsiPath { get; private set; }

    public UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"CrmAgentTray/{CurrentVersion}");
        _http.Timeout = TimeSpan.FromSeconds(30);

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += async (_, _) => await CheckForUpdateAsync();
    }

    /// <summary>Start the background check timer. First check runs after 30 seconds.</summary>
    public void Start()
    {
        _timer.Interval = CheckDelayMs;
        _timer.Start();
    }

    /// <summary>Manually trigger an update check (e.g. from a "Check for Updates" button).</summary>
    public async Task CheckNowAsync()
    {
        await CheckForUpdateAsync();
    }

    /// <summary>Launch the downloaded MSI as an elevated silent install.</summary>
    public void ApplyUpdate()
    {
        if (DownloadedMsiPath is null || !File.Exists(DownloadedMsiPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/qn /i \"{DownloadedMsiPath}\"",
                Verb = "runas",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined UAC prompt — nothing to do.
        }
    }

    private async Task CheckForUpdateAsync()
    {
        if (_checking) return;
        _checking = true;

        try
        {
            // After the first check, switch to the long interval
            if (_timer.Interval == CheckDelayMs)
                _timer.Interval = CheckIntervalMs;

            var release = await _http.GetFromJsonAsync<GitHubRelease>(ReleaseUrl);
            if (release?.TagName is null) return;

            var remoteVersion = ParseVersion(release.TagName);
            if (remoteVersion is null || remoteVersion <= CurrentVersion) return;

            // Find the MSI asset
            var msiAsset = release.Assets?.FirstOrDefault(a =>
                a.BrowserDownloadUrl is not null &&
                a.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);
            if (msiAsset?.BrowserDownloadUrl is null) return;

            // Download to temp
            var tempPath = Path.Combine(Path.GetTempPath(), $"GDATACrmAgent-{release.TagName}.msi");
            if (!File.Exists(tempPath))
            {
                using var response = await _http.GetAsync(msiAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
            }

            AvailableVersion = release.TagName;
            DownloadedMsiPath = tempPath;
            UpdateReady?.Invoke(release.TagName, tempPath);
        }
        catch
        {
            // Network errors, rate limits, etc. — silently ignore and retry next cycle.
        }
        finally
        {
            _checking = false;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var v) ? v : null;
    }

    internal static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    // DTOs for the GitHub API response (minimal)
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
