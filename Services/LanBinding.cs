using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// The one extra address the board listens on when a worker host needs to reach it
/// (docs/worker-hosts-plan.md, piece 7): <c>lanUrl</c> in the board host's global config, added
/// beside the addresses the host was already given. Off unless it is set, so a machine with no
/// worker host listens on exactly what it always has.
/// <para>
/// An explicit address only - <c>http://192.168.1.20:5338</c> - never a wildcard. The API has no
/// authentication, so what keeps other machines out is the firewall rule for this one address and
/// one worker host, and a wildcard would put it on every interface the machine has, including ones
/// that rule was never written for.
/// </para>
/// </summary>
public static class LanBinding
{
    /// <summary>
    /// The addresses to listen on, or null to leave them as they are - with <paramref name="why"/>
    /// saying why when a <c>lanUrl</c> was set but is not used.
    /// </summary>
    public static string[]? Urls(string? currentUrls, IReadOnlyDictionary<string, JsonElement> global, bool isolated, out string? why)
    {
        why = null;
        if (!global.TryGetValue("lanUrl", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var lan = v.GetString()!.Trim().TrimEnd('/');
        if (lan.Length == 0) return null;

        if (isolated) { why = "an isolated QA copy never listens off this machine"; return null; }
        if (string.IsNullOrWhiteSpace(currentUrls)) { why = "the app was not given its own address to add it beside"; return null; }
        if (!Uri.TryCreate(lan, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/" || uri.IsDefaultPort && !lan.Contains(':' + uri.Port.ToString()))
        {
            why = $"'{lan}' is not an address of the form http://<lan-ip>:<port>";
            return null;
        }
        if (uri.Host is "0.0.0.0" or "*" or "+" or "[::]" || lan.Contains("://*") || lan.Contains("://+"))
        {
            why = $"'{lan}' is a wildcard; name the one LAN address the firewall rule allows";
            return null;
        }

        var urls = currentUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!urls.Contains(lan, StringComparer.OrdinalIgnoreCase)) urls.Add(lan);
        return urls.ToArray();
    }
}
