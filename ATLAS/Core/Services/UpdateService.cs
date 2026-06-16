using System.Diagnostics;
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
