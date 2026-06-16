using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="ISteamCmdService"/>
public sealed class SteamCmdService : ISteamCmdService
{
    // Single shared HttpClient for the lifetime of the app (no per-call disposal).
    private static readonly HttpClient Http = new();

    private const string GetPublishedFileDetailsUrl =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    // Substrings (matched case-insensitively) that indicate SteamCMD is waiting for a Steam Guard code.
    private static readonly string[] SteamGuardPrompts =
    [
        "steam guard",
        "two-factor",
        "two factor",
    ];

    private readonly ISettingsService _settings;

    public SteamCmdService(ISettingsService settings) => _settings = settings;

    // ------------------------------------------------------------------ availability / path

    public async Task<string> GetSteamCmdPathAsync()
    {
        var configured = _settings.Settings.SteamCmdPath;
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;
        return await Task.FromResult(Path.Combine(AppConstants.SteamCmdDirectory, "steamcmd.exe")).ConfigureAwait(false);
    }

    public async Task<bool> IsSteamCmdAvailableAsync()
    {
        var path = await GetSteamCmdPathAsync().ConfigureAwait(false);
        return File.Exists(path);
    }

    // ------------------------------------------------------------------ download / install

    public async Task DownloadSteamCmdAsync(IProgress<(string message, int percent)> progress, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"steamcmd-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report(("Downloading SteamCMD…", 0));

            using (var resp = await Http.GetAsync(AppConstants.SteamCmdDownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tempZip);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total is > 0)
                    {
                        var pct = (int)(received * 100 / total.Value);
                        progress.Report(("Downloading SteamCMD…", pct));
                    }
                }
            }

            progress.Report(("Extracting SteamCMD…", 100));
            Directory.CreateDirectory(AppConstants.SteamCmdDirectory);
            ZipFile.ExtractToDirectory(tempZip, AppConstants.SteamCmdDirectory, overwriteFiles: true);

            var exePath = Path.Combine(AppConstants.SteamCmdDirectory, "steamcmd.exe");
            _settings.Settings.SteamCmdPath = exePath;
            await _settings.SaveAsync().ConfigureAwait(false);

            progress.Report(("SteamCMD installed.", 100));
            Log.Information("SteamCMD installed at {Path}", exePath);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("SteamCMD download cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download/extract SteamCMD.");
            throw;
        }
        finally
        {
            TryDeleteFile(tempZip);
        }
    }

    // ------------------------------------------------------------------ server / mod updates

    public async Task UpdateServerAsync(string installPath, bool profilingBranch, IProgress<string> progress,
        CancellationToken ct, Func<CancellationToken, Task<string?>>? steamGuardProvider = null)
    {
        var login = GetSavedUsername() is { Length: > 0 } user ? user : "anonymous";

        var args = new List<string>
        {
            "+force_install_dir", installPath,
            "+login", login,
            "+app_update", AppConstants.Arma3ServerAppId,
        };
        if (profilingBranch)
        {
            args.Add("-beta");
            args.Add("profiling");
        }
        args.Add("validate");
        args.Add("+quit");

        Log.Information("Updating Arma 3 server (app {AppId}, profiling={Profiling}) into {Path}.",
            AppConstants.Arma3ServerAppId, profilingBranch, installPath);
        await RunSteamCmdAsync(args, progress, ct, steamGuardProvider).ConfigureAwait(false);
    }

    public Task UpdateModAsync(ulong workshopId, string stagingPath, string steamLogin, IProgress<string> progress,
        CancellationToken ct, Func<CancellationToken, Task<string?>>? steamGuardProvider = null)
        => UpdateModsAsync(new[] { workshopId }, stagingPath, steamLogin, progress, ct, steamGuardProvider);

    public async Task UpdateModsAsync(IEnumerable<ulong> workshopIds, string stagingPath, string steamLogin,
        IProgress<string> progress, CancellationToken ct,
        Func<CancellationToken, Task<string?>>? steamGuardProvider = null)
    {
        var ids = workshopIds.ToList();
        if (ids.Count == 0)
        {
            progress.Report("No mods to update.");
            return;
        }

        var login = string.IsNullOrWhiteSpace(steamLogin) ? "anonymous" : steamLogin;

        var args = new List<string>
        {
            "+force_install_dir", stagingPath,
            "+login", login,
        };
        // Batch every item into one session (single login, single client spin-up).
        foreach (var id in ids)
        {
            args.Add("+workshop_download_item");
            args.Add(AppConstants.Arma3WorkshopAppId);
            args.Add(id.ToString());
            args.Add("validate");
        }
        args.Add("+quit");

        Log.Information("Downloading {Count} Workshop item(s) (app {AppId}) into {Path}.",
            ids.Count, AppConstants.Arma3WorkshopAppId, stagingPath);
        await RunSteamCmdAsync(args, progress, ct, steamGuardProvider).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ staleness

    public async Task<bool> IsModUpToDateAsync(ArmaModEntry mod, string stagingPath)
    {
        var info = await GetWorkshopModInfoAsync(mod.WorkshopId).ConfigureAwait(false);
        if (info is null) return true; // cannot determine — assume up to date
        return mod.LastUpdated >= info.TimeUpdated;
    }

    // ------------------------------------------------------------------ credentials

    public string? GetSavedUsername()
    {
        var user = _settings.Settings.SteamUsername;
        return string.IsNullOrWhiteSpace(user) ? null : user;
    }

    public void SaveUsername(string username)
    {
        _settings.Settings.SteamUsername = username ?? string.Empty;
        // Fire-and-forget persist; SaveAsync logs and never throws.
        _ = _settings.SaveAsync();
    }

    public void ClearSavedCredentials()
    {
        _settings.Settings.SteamUsername = string.Empty;
        _ = _settings.SaveAsync();

        try
        {
            var dir = AppConstants.SteamCmdDirectory;
            if (!Directory.Exists(dir)) return;

            // SteamCMD caches the session token under config/ and as *.vdf / ssfn* token files.
            var configDir = Path.Combine(dir, "config");
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);

            foreach (var file in Directory.EnumerateFiles(dir, "*.vdf", SearchOption.AllDirectories))
                TryDeleteFile(file);
            foreach (var file in Directory.EnumerateFiles(dir, "ssfn*", SearchOption.AllDirectories))
                TryDeleteFile(file);

            Log.Information("Cleared saved Steam credentials and cached login tokens.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Best-effort clear of SteamCMD cached login files failed.");
        }
    }

    // ------------------------------------------------------------------ Steam Web API

    public async Task<WorkshopModInfo?> GetWorkshopModInfoAsync(ulong workshopId, CancellationToken ct = default)
    {
        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("itemcount", "1"),
                new("publishedfileids[0]", workshopId.ToString()),
            };
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync(GetPublishedFileDetailsUrl, content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("publishedfiledetails", out var details) ||
                details.ValueKind != JsonValueKind.Array ||
                details.GetArrayLength() == 0)
            {
                Log.Warning("Workshop metadata response had no details for item {Id}.", workshopId);
                return null;
            }

            var item = details[0];

            // result 1 = OK; 9 = missing/private/deleted.
            if (item.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Number && result.GetInt32() != 1)
            {
                Log.Warning("Workshop item {Id} unavailable (result={Result}).", workshopId, result.GetInt32());
                return null;
            }

            var info = new WorkshopModInfo
            {
                WorkshopId = workshopId,
                Title = GetString(item, "title"),
                Description = GetString(item, "description"),
                PreviewUrl = GetString(item, "preview_url"),
            };

            // file_size is a STRING on this endpoint.
            if (item.TryGetProperty("file_size", out var sizeEl))
            {
                var sizeStr = sizeEl.ValueKind == JsonValueKind.String ? sizeEl.GetString() : sizeEl.GetRawText();
                if (ulong.TryParse(sizeStr, out var size)) info.FileSize = size;
            }

            // time_updated is a numeric unix-epoch (seconds).
            if (item.TryGetProperty("time_updated", out var timeEl) && timeEl.ValueKind == JsonValueKind.Number)
                info.TimeUpdated = DateTimeOffset.FromUnixTimeSeconds(timeEl.GetInt64()).UtcDateTime;

            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch Workshop metadata for item {Id}.", workshopId);
            return null;
        }
    }

    // ------------------------------------------------------------------ diagnostics / tests

    public async Task<(bool ok, string message)> TestSteamCmdAsync(CancellationToken ct = default)
    {
        try
        {
            var exePath = await GetSteamCmdPathAsync().ConfigureAwait(false);
            if (!File.Exists(exePath))
                return (false, "SteamCMD is not installed. Use Download to install it.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppConstants.SteamCmdDirectory,
            };
            psi.ArgumentList.Add("+quit");

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, "Failed to start SteamCMD.");

            // First run self-updates, which can take a little while; cap the wait so the UI never hangs.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));

            // Drain stdout/stderr so the child never blocks on a full pipe; capture the tasks so they are
            // always awaited before `proc` is disposed (no use-after-dispose race / unobserved exceptions).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return (true, $"SteamCMD ran successfully (exit code {proc.ExitCode}).");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillTree(proc);
                return (false, "SteamCMD did not exit within 2 minutes (still updating?). Try again shortly.");
            }
            finally
            {
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
                catch { /* read errors/cancellation are non-fatal for a diagnostic test */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Test SteamCMD failed.");
            return (false, $"SteamCMD test failed: {ex.Message}");
        }
    }

    public async Task<bool> TestApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        try
        {
            // GetSupportedAPIList returns the full API surface only when called with a valid key; an
            // invalid key yields 403 Forbidden.
            var url = "https://api.steampowered.com/ISteamWebAPIUtil/GetSupportedAPIList/v1/?key="
                      + Uri.EscapeDataString(apiKey.Trim());
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Test Steam API key failed.");
            return false;
        }
    }

    // ------------------------------------------------------------------ SteamCMD process

    /// <summary>
    /// Launches <c>steamcmd.exe</c> with the given argument list, streaming stdout/stderr line-by-line to
    /// <paramref name="progress"/>. When SteamCMD prints a Steam Guard prompt (which arrives without a
    /// trailing newline), the prompt is detected from a rolling buffer of raw stdout and the code returned
    /// by <paramref name="steamGuardProvider"/> is written to stdin.
    /// </summary>
    private async Task RunSteamCmdAsync(IReadOnlyList<string> args, IProgress<string> progress, CancellationToken ct,
        Func<CancellationToken, Task<string?>>? steamGuardProvider)
    {
        var exePath = await GetSteamCmdPathAsync().ConfigureAwait(false);
        if (!File.Exists(exePath))
            throw new FileNotFoundException("SteamCMD is not installed.", exePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppConstants.SteamCmdDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("Failed to start SteamCMD.");

        // Read stderr line-by-line (Guard prompts come on stdout, so line buffering is fine here).
        var stderrTask = PumpStderrAsync(proc, progress, ct);
        // Read stdout as a raw char stream so the no-newline Guard prompt is detected.
        var stdoutTask = PumpStdoutAsync(proc, progress, ct, steamGuardProvider);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            throw;
        }
        finally
        {
            // Always await the reader pumps before `proc` is disposed — even on cancellation — so they
            // never read from disposed stream handles (use-after-dispose race / unobserved exceptions).
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* pump exceptions are already logged; cancellation is expected on the cancel path */ }
        }

        // Exit code is unreliable across SteamCMD versions — callers corroborate with on-disk verification.
        Log.Information("SteamCMD exited with code {Code}.", proc.ExitCode);
    }

    private static async Task PumpStdoutAsync(Process proc, IProgress<string> progress, CancellationToken ct,
        Func<CancellationToken, Task<string?>>? steamGuardProvider)
    {
        var reader = proc.StandardOutput;
        var buffer = new char[1024];
        var line = new StringBuilder();
        var rolling = new StringBuilder();

        try
        {
            // If no Guard-code provider was supplied, signal EOF on stdin up front so SteamCMD never
            // blocks waiting for interactive input it will not receive (which would stall WaitForExit).
            if (steamGuardProvider is null) TryCloseStdin(proc);

            int read;
            while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        ReportLine(line.ToString());
                        line.Clear();
                    }
                    else if (c != '\r')
                    {
                        line.Append(c);
                    }
                }

                // Maintain a small rolling buffer to catch a prompt emitted without a newline.
                rolling.Append(buffer, 0, read);
                if (rolling.Length > 4096) rolling.Remove(0, rolling.Length - 4096);

                if (steamGuardProvider is not null && IsSteamGuardPrompt(rolling.ToString()))
                {
                    rolling.Clear();
                    line.Clear();
                    progress.Report("Steam Guard code required…");
                    var code = await steamGuardProvider(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(code))
                    {
                        await proc.StandardInput.WriteLineAsync(code).ConfigureAwait(false);
                        await proc.StandardInput.FlushAsync().ConfigureAwait(false);
                    }
                }
            }

            if (line.Length > 0) ReportLine(line.ToString());
        }
        catch (OperationCanceledException)
        {
            // Cancellation handled by the caller (process is killed there).
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error reading SteamCMD stdout.");
        }
        finally
        {
            // Reading is done → no more input will be written; send EOF so a stdin-blocked child can exit.
            TryCloseStdin(proc);
        }

        void ReportLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Defense-in-depth: SteamCMD can echo the typed Steam Guard code back on stdout. A bare
            // 5-character alphanumeric line is the two-factor code format — never forward it to the
            // progress stream (a caller may persist progress). See the remarks on ISteamCmdService.
            if (IsLikelySteamGuardCode(text)) { progress.Report("[redacted Steam Guard code]"); return; }
            progress.Report(text);
        }

        static void TryCloseStdin(Process p)
        {
            try { p.StandardInput.Close(); } catch { /* already closed/disposed */ }
        }

        static bool IsLikelySteamGuardCode(string text)
        {
            var t = text.Trim();
            if (t.Length != 5) return false;
            foreach (var c in t) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }
    }

    private static async Task PumpStderrAsync(Process proc, IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line)) progress.Report(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation handled by the caller.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error reading SteamCMD stderr.");
        }
    }

    private static bool IsSteamGuardPrompt(string text)
    {
        foreach (var token in SteamGuardPrompts)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void TryKillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill SteamCMD process tree.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete file {Path}.", path);
        }
    }
}
