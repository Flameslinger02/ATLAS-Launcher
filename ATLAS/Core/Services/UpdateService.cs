using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Atlas.Core.Models;
using Octokit;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IUpdateService"/>
public sealed class UpdateService : IUpdateService
{
    private readonly ISettingsService _settings;
    private readonly GitHubClient _github = new(new ProductHeaderValue(AppConstants.AppName));

    public UpdateService(ISettingsService settings) => _settings = settings;

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion();
        var result = new UpdateCheckResult { CurrentVersion = VersionToDisplay(current) };

        var owner = string.IsNullOrWhiteSpace(_settings.Settings.GitHubOwner)
            ? AppConstants.GitHubOwner : _settings.Settings.GitHubOwner.Trim();
        var repo = string.IsNullOrWhiteSpace(_settings.Settings.GitHubRepo)
            ? AppConstants.GitHubRepo : _settings.Settings.GitHubRepo.Trim();

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            result.Error = "GitHub owner/repo is not configured.";
            return result;
        }

        try
        {
            // GetLatest excludes drafts and pre-releases. If the repo has only pre-releases (or none),
            // it 404s — fall back to the newest non-draft from the full list.
            Release? release = null;
            try
            {
                release = await _github.Repository.Release.GetLatest(owner, repo).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                var all = await _github.Repository.Release.GetAll(owner, repo).ConfigureAwait(false);
                release = all
                    .Where(r => !r.Draft)
                    .OrderByDescending(r => r.PublishedAt ?? r.CreatedAt)
                    .FirstOrDefault();
            }

            if (release is null)
            {
                // No releases published yet — not an error, just nothing to update to.
                Log.Information("Update check: no releases found for {Owner}/{Repo}.", owner, repo);
                return result;
            }

            result.ReleaseUrl = release.HtmlUrl;
            result.PublishedAt = (release.PublishedAt ?? release.CreatedAt).UtcDateTime;
            result.ReleaseNotesSummary = Summarize(release.Body);

            // Locate the portable single-file asset for self-update (the CI publishes a bare "ATLAS.exe").
            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, "ATLAS.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
            {
                result.AssetDownloadUrl = asset.BrowserDownloadUrl;
                result.AssetSize = asset.Size;
            }

            var latest = ParseVersion(release.TagName);
            result.LatestVersion = latest is null ? CleanTag(release.TagName) : VersionToDisplay(latest);

            if (latest is null)
                // We found a release but couldn't compare its tag — don't silently claim "up to date".
                result.Error = $"Could not parse the latest release tag '{release.TagName}'.";
            else if (latest > current)
                result.UpdateAvailable = true;

            // Persist for the next-launch banner regardless of comparison outcome.
            _settings.Settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Settings.LastKnownLatestVersion = result.LatestVersion;
            await _settings.SaveAsync().ConfigureAwait(false);

            Log.Information("Update check: current {Current}, latest {Latest}, updateAvailable={Avail}.",
                result.CurrentVersion, result.LatestVersion, result.UpdateAvailable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Rate limits, offline, DNS, etc. — surface as a non-fatal error.
            Log.Warning(ex, "Update check failed for {Owner}/{Repo}.", owner, repo);
            result.Error = ex.Message;
            // Record that an attempt was made so the UI doesn't read "Never checked" after repeated failures.
            try
            {
                _settings.Settings.LastUpdateCheck = DateTime.UtcNow;
                await _settings.SaveAsync().ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }

        return result;
    }

    public void OpenReleasePage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open release page {Url}.", url);
        }
    }

    public async Task<string> DownloadUpdateAsync(UpdateCheckResult result, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(result.AssetDownloadUrl))
            throw new InvalidOperationException("The latest release has no downloadable ATLAS.exe asset.");

        var dir = Path.Combine(Path.GetTempPath(), "ATLAS_update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "ATLAS.exe");

        // NOTE: the portable build is unsigned, so there is no code-signature to verify; we verify the
        // downloaded byte length against the GitHub-published asset size instead (best-effort integrity).
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.AppName);

        using var resp = await http
            .GetAsync(result.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? result.AssetSize;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(dest, System.IO.FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                readTotal += n;
                if (total is > 0) progress?.Report(Math.Clamp((double)readTotal / total.Value, 0, 1));
            }
        }

        if (result.AssetSize is > 0)
        {
            var actual = new FileInfo(dest).Length;
            if (actual != result.AssetSize.Value)
            {
                try { File.Delete(dest); } catch { /* best effort */ }
                throw new IOException(
                    $"Downloaded update is {actual:N0} bytes but the release lists {result.AssetSize.Value:N0}. Aborting.");
            }
        }

        progress?.Report(1);
        Log.Information("Downloaded update to {Dest} ({Bytes:N0} bytes).", dest, new FileInfo(dest).Length);
        return dest;
    }

    public void ApplyUpdateAndRestart(string downloadedExePath)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Could not resolve the running ATLAS executable path.");
        if (!File.Exists(downloadedExePath))
            throw new FileNotFoundException("The downloaded update was not found.", downloadedExePath);

        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), "ATLAS_update", "apply-update.ps1");
        File.WriteAllText(scriptPath, HelperScript);

        // Detached, hidden helper: waits for us to exit, swaps the exe, relaunches ATLAS, then self-deletes.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,          // independent of this process so it survives our shutdown
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                        $"-TargetPid {pid} -New \"{downloadedExePath}\" -Target \"{target}\"",
        };
        Process.Start(psi);
        Log.Information("Launched update helper (pid {Pid} → swap {Target}).", pid, target);

        // Let the current call unwind, then shut down so the helper can replace the locked exe.
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => System.Windows.Application.Current!.Shutdown());
    }

    /// <summary>
    /// PowerShell helper that waits for ATLAS to exit, copies the new exe over the running one (retrying past
    /// any lingering file lock), relaunches it, then removes the temp exe and itself. Best-effort: if the copy
    /// fails (e.g. the exe sits in a write-protected dir) the temp exe is left in place.
    /// </summary>
    private const string HelperScript = """
        param([int]$TargetPid, [string]$New, [string]$Target)
        try { Wait-Process -Id $TargetPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
        $ok = $false
        for ($i = 0; $i -lt 30; $i++) {
            try {
                Copy-Item -LiteralPath $New -Destination $Target -Force -ErrorAction Stop
                $ok = $true
                break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        try { Start-Process -FilePath $Target } catch {}
        if ($ok) { try { Remove-Item -LiteralPath $New -Force -ErrorAction SilentlyContinue } catch {} }
        try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch {}
        """;

    // ----------------------------------------------------------------- helpers

    private static Version CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Parses a release tag like "v1.2.3" / "1.2.3" / "v1.2.3-beta.1" into a <see cref="Version"/>.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        var cleaned = CleanTag(tag);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        // Drop any pre-release / build suffix (e.g. "-beta", "+meta") before parsing.
        var core = cleaned.Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>Strips a leading "v"/"V" and surrounding whitespace from a tag.</summary>
    internal static string CleanTag(string? tag)
    {
        var t = (tag ?? string.Empty).Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        return t.Trim();
    }

    /// <summary>Renders a version for display, dropping a trailing ".0" revision when present (1.2.3.0 → 1.2.3).</summary>
    private static string VersionToDisplay(Version v) =>
        v.Revision <= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    private static string? Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var lines = body.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(6)
            .ToArray();
        var summary = string.Join(Environment.NewLine, lines);
        return summary.Length > 600 ? summary[..600] + "…" : summary;
    }
}
