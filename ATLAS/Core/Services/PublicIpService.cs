using System.Net.Http;
using System.Text.RegularExpressions;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>
/// Resolves the machine's public (WAN) IP address for the Steam public-reachability check. A profile may
/// pin an explicit value (VPS / static IP / multi-homed box); otherwise the address is auto-detected once
/// via a lightweight HTTPS echo service and cached for the session.
/// </summary>
public interface IPublicIpService
{
    /// <summary>Returns the public IP to query, or null if it can't be determined. When
    /// <paramref name="overrideValue"/> is non-blank it wins and no network call is made.</summary>
    Task<string?> GetPublicIpAsync(string? overrideValue, CancellationToken ct = default);
}

/// <inheritdoc cref="IPublicIpService"/>
public sealed class PublicIpService : IPublicIpService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Plain-text IP echoes, tried in order. Kept to well-known, no-key, HTTPS endpoints.
    private static readonly string[] Echoes = { "https://api.ipify.org", "https://icanhazip.com" };
    private static readonly Regex IpV4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

    private string? _cached;   // auto-detected address, resolved at most once per session

    public async Task<string?> GetPublicIpAsync(string? overrideValue, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue.Trim();
        if (_cached is not null) return _cached;

        foreach (var url in Echoes)
        {
            try
            {
                var body = (await Http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
                if (IpV4.IsMatch(body)) return _cached = body;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug(ex, "Public-IP echo {Url} failed.", url); }
        }

        Log.Warning("Could not auto-detect the public IP for the reachability check.");
        return null;
    }
}
