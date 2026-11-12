using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// Which links in a ticket the board may photograph for the fleet, per project:
/// <c>prototypeCapture</c> in <c>project.json</c> / <c>project.defaults.json</c>.
/// <code>
/// "prototypeCapture": { "hosts": ["design.example.com", "localhost:5399"], "linkText": ["prototype"], "maxLinks": 5 }
/// </code>
/// <para>
/// <c>hosts</c> is the whole permission. A link whose host is not on it is never fetched, whatever
/// it says about itself - the text around a link is written by whoever edits the tracker, so it can
/// narrow what is fetched but never widen it. An entry matches that host and any subdomain of it;
/// with a port, only that port. No hosts means off, which is the default.
/// </para>
/// <para>
/// <c>linkText</c>, when set, narrows further: a link on an allowed host counts only when its own
/// text or the line it sits on names one of these words. Left empty, every link to an allowed host
/// counts - right for a host that serves nothing but designs.
/// </para>
/// </summary>
public sealed record PrototypeCaptureConfig(string[] Hosts, string[] LinkText, int MaxLinks)
{
    public const int DefaultMaxLinks = 5;

    public static readonly PrototypeCaptureConfig Off = new(Array.Empty<string>(), Array.Empty<string>(), DefaultMaxLinks);

    public bool IsOn => Hosts.Length > 0;

    public static PrototypeCaptureConfig From(IReadOnlyDictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("prototypeCapture", out var p) || p.ValueKind != JsonValueKind.Object) return Off;

        static string[] Strs(JsonElement o, string name) =>
            o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToArray()
                : Array.Empty<string>();

        var hosts = Strs(p, "hosts")
            // A scheme or a path pasted in by habit is forgiven; anything else that is not a host is dropped.
            .Select(h => Regex.Replace(h, @"^[a-z][a-z0-9+.-]*://", "", RegexOptions.IgnoreCase).Split('/')[0].TrimStart('*', '.').ToLowerInvariant())
            .Where(h => Regex.IsMatch(h, @"^([a-z0-9-]+\.)+[a-z0-9-]+(:\d{1,5})?$") || Regex.IsMatch(h, @"^localhost(:\d{1,5})?$"))
            .Distinct()
            .ToArray();
        var max = p.TryGetProperty("maxLinks", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 20) : DefaultMaxLinks;
        return new PrototypeCaptureConfig(hosts, Strs(p, "linkText"), max);
    }
}

/// <summary>Finds the links in a ticket's text that <see cref="PrototypeCaptureConfig"/> lets the board capture.</summary>
public static class PrototypeLinks
{
    /// <summary>A piece of text a link can be found in.</summary>
    /// <param name="Code">Short and filename-safe, for the capture's name: <c>description</c>,
    /// <c>comment-10023</c>, <c>parent-PROJ-1</c>.</param>
    /// <param name="FoundIn">What a person reads: "PROJ-2's description", "a comment by Someone".</param>
    public sealed record Source(string Code, string FoundIn, string? Text);

    public sealed record Link(string Url, string Code, string FoundIn, string? LinkText);

    // [text](url) - markdown, which is what both trackers' text is flattened to.
    private static readonly Regex Markdown = new(@"\[(?<text>[^\]]*)\]\((?<url>https?://[^)\s]+)\)", RegexOptions.IgnoreCase);
    // [text|url] - Jira wiki markup, which survives in older issues and in comments pasted from them.
    private static readonly Regex Wiki = new(@"\[(?<text>[^\]|\r\n]+)\|(?<url>https?://[^\]\s|]+)\]", RegexOptions.IgnoreCase);
    // <a href="url">text</a> - HTML that reached us unflattened.
    private static readonly Regex Anchor = new(@"<a\s[^>]*href\s*=\s*[""'](?<url>https?://[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    // Anything else that looks like a link.
    private static readonly Regex Bare = new(@"https?://[^\s<>()\[\]""'`|]+", RegexOptions.IgnoreCase);

    /// <summary>
    /// Every link in <paramref name="sources"/> the config allows, once each, in the order found -
    /// a link the description and a comment both carry is credited to the first. Empty when the
    /// config is off.
    /// </summary>
    public static IReadOnlyList<Link> Find(IEnumerable<Source> sources, PrototypeCaptureConfig cfg)
    {
        var found = new List<Link>();
        if (!cfg.IsOn) return found;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.Text)) continue;
            var text = s.Text;

            var candidates = new List<(int At, string Url, string? Text)>();
            foreach (var rx in new[] { Markdown, Wiki, Anchor })
                foreach (Match m in rx.Matches(text))
                    candidates.Add((m.Index, m.Groups["url"].Value, StripTags(m.Groups["text"].Value)));
            foreach (Match m in Bare.Matches(text))
                candidates.Add((m.Index, m.Value, null));

            // Labelled forms first at any position, so a bare match inside [text](url) never wins.
            foreach (var c in candidates.OrderBy(c => c.Text is null).ThenBy(c => c.At))
            {
                if (Normalize(c.Url) is not { } uri) continue;
                if (!HostAllowed(uri, cfg.Hosts)) continue;
                if (!seen.Add(uri.AbsoluteUri)) continue;
                if (cfg.LinkText.Length > 0 && !Mentions(c.Text, cfg.LinkText) && !Mentions(LineAt(text, c.At), cfg.LinkText))
                    continue;
                found.Add(new Link(uri.AbsoluteUri, s.Code, s.FoundIn, string.IsNullOrWhiteSpace(c.Text) ? null : c.Text.Trim()));
            }
        }

        return found;
    }

    /// <summary>
    /// The link as the board would fetch it, or null when it is not one it would: http or https
    /// only, and never one carrying a user name or password.
    /// </summary>
    public static Uri? Normalize(string raw)
    {
        var s = raw.Trim().TrimEnd('.', ',', ';', ':', '!', '?', '*', '_', '~');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(u.UserInfo)) return null;
        return u;
    }

    /// <summary>Whether <paramref name="uri"/>'s host is one of <paramref name="hosts"/> or a subdomain of one; a port in the entry must match.</summary>
    public static bool HostAllowed(Uri uri, IReadOnlyCollection<string> hosts)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var entry in hosts)
        {
            var parts = entry.ToLowerInvariant().Split(':');
            if (parts.Length > 1 && (!int.TryParse(parts[1], out var port) || port != uri.Port)) continue;
            if (host == parts[0] || host.EndsWith("." + parts[0], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Whether a link points at this machine - the only kind an isolated QA copy may capture.</summary>
    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static bool Mentions(string? text, string[] words) =>
        !string.IsNullOrEmpty(text) && words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string LineAt(string text, int at)
    {
        var start = at <= 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        var end = text.IndexOf('\n', at);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", "");
}
