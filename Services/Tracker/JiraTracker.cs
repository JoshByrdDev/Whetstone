using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Services.Tracker;

/// <summary>
/// Jira Cloud, read-only, through the v3 REST API.
/// <para>
/// Everything Jira-shaped lives behind this class: ADF flattening, JQL, the cloudId gateway, and
/// the fact that Jira reports a parent as a field rather than a link. Consumers get
/// <see cref="Issue"/> records and never learn any of it.
/// </para>
/// <para>
/// <b>There are no write methods here, by design.</b> The dashboard never modifies Jira - local
/// tickets own all state.
/// </para>
/// </summary>
public sealed class JiraTracker : IIssueTracker
{
    private readonly TrackerConfig _config;
    private readonly HttpClient _http;

    // BaseAddress is resolved lazily (it needs a network round-trip for the cloudId), guarded so the
    // resolution happens exactly once per instance even under concurrent first calls.
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public JiraTracker(TrackerConfig config)
    {
        _config = config;
        _http = new HttpClient();

        if (_config.IsConfigured)
        {
            // Auth header is fixed up-front; only the base URL is deferred.
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_config.Email}:{_config.Token}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public TrackerKind Kind => TrackerKind.Jira;
    public string DisplayName => "Jira";
    public string IssueNoun => "issue";
    public bool IsConfigured => _config.IsConfigured;

    // ---- Identity ----------------------------------------------------------

    public string? IssueUrl(string key)
    {
        var site = _config.BaseUrl.TrimEnd('/');
        return string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(key)
            ? null : $"{site}/browse/{key}";
    }

    /// <summary>Uppercase and trim. Jira keys are case-insensitive upstream but we compare ours
    /// ordinally against the database, so one canonical casing is what keeps them matching.</summary>
    public string? NormalizeKey(string? candidate)
    {
        var s = (candidate ?? "").Trim().TrimStart('#').Trim();
        return s.Length == 0 ? null : s.ToUpperInvariant();
    }

    public string BranchToken(string key) => key;

    /// <summary>The bare key. <c>PROJ-123: what it is</c> is both what a Jira user reads and what
    /// Jira's own smart-commit parser looks for.</summary>
    public string CommitRef(string key) => key;

    // ---- Field sets --------------------------------------------------------

    // attachment + issuelinks are here because a phase working from the description alone cannot tell
    // that a mockup was attached or that a blocking ticket exists - it just plans around a hole.
    //
    // parent + subtasks are here for a sharper reason: Jira does NOT report the parent as an issue
    // link. A sub-task's parent lives in fields.parent and nowhere else, so an issue whose issuelinks
    // are empty looks orphaned even when it sits under a fully-specified story. One sub-task blocked
    // itself on "no reachable parent story" while its parent carried the entire spec.
    private const string FullFields =
        "summary,status,assignee,priority,issuetype,description,created,updated,comment,attachment,issuelinks,parent,subtasks";

    // What an ancestor is worth fetching for: its description (the requirements the child omits),
    // its own parent (so the chain can be walked up), and its subtasks (the child's siblings, which
    // are what says "the CSV export is someone else's ticket, not yours"). attachment is requested
    // both for filenames and because a parent's mockup is usually the one that settles the question.
    private const string AncestorFields =
        "summary,status,issuetype,description,parent,subtasks,attachment";

    // A brief is one hop sideways, or one row on the board: enough to say what it is and what it
    // asks for, without dragging in comments and links too. attachment rides along for filenames
    // only - without it an embedded image in the description flattens to an opaque id.
    private const string BriefFields = "summary,status,issuetype,description,attachment";

    // ---- Reads -------------------------------------------------------------

    public async Task<Issue?> GetIssueAsync(string key, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        await EnsureBaseAddressAsync(ct);
        var response = await _http.GetAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={FullFields}", ct);
        // A key that names nothing is an answer, not a failure - the caller says so in its own
        // words. Everything else throws, because "Jira is down" and "there is no such issue" must
        // not reach a reader as the same sentence.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        return MapIssue(key, json);
    }

    public Task<Issue?> GetAncestorAsync(string key, CancellationToken ct = default)
        => TryGetAsync(key, AncestorFields, ct);

    private async Task<Issue?> TryGetAsync(string key, string fields, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        try
        {
            await EnsureBaseAddressAsync(ct);
            var response = await _http.GetAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={fields}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            return MapIssue(key, json);
        }
        catch { return null; }
    }

    public async Task<IReadOnlyDictionary<string, Issue>> GetBriefsAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var map = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
        var safe = SafeKeys(keys);
        if (!IsConfigured || safe.Count == 0) return map;

        var search = await SearchAsync($"key in ({string.Join(",", safe)})", safe.Count, BriefFields.Split(','), ct);
        foreach (var (key, issue) in Issues(search))
            map[key] = MapIssue(key, issue);
        return map;
    }

    public async Task<IReadOnlyList<IssueRef>> GetMyUnfinishedAsync(int max, CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<IssueRef>();
        var search = await SearchAsync(
            "assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC",
            max, new[] { "summary", "status", "issuetype" }, ct);

        return Issues(search)
            .Select(pair => new IssueRef(pair.Key,
                Str(Fields(pair.Issue), "summary"),
                Nested(Fields(pair.Issue), "status", "name"),
                Nested(Fields(pair.Issue), "issuetype", "name")))
            .ToList();
    }

    public async Task<IReadOnlyList<BlockingLink>> GetBlockingLinksAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var result = new List<BlockingLink>();
        var safe = SafeKeys(keys);
        if (!IsConfigured || safe.Count == 0) return result;

        var blocking = new HashSet<string>(_config.EffectiveBlockingLinkTypes, StringComparer.OrdinalIgnoreCase);
        if (blocking.Count == 0) return result;   // this project has said nothing blocks

        var search = await SearchAsync($"key in ({string.Join(",", safe)})",
            Math.Max(50, safe.Count), new[] { "issuelinks" }, ct);

        foreach (var (key, issue) in Issues(search))
        {
            var fields = Fields(issue);
            if (!fields.TryGetProperty("issuelinks", out var links) || links.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var link in links.EnumerateArray())
            {
                var typeName = link.TryGetProperty("type", out var ty) ? Str(ty, "name") : null;
                if (typeName is null || !blocking.Contains(typeName)) continue;

                // Only the INWARD side is a dependency: "is blocked by". The outward side means this
                // issue blocks something else, which is that issue's problem, not this one's.
                if (!link.TryGetProperty("inwardIssue", out var other) || other.ValueKind != JsonValueKind.Object)
                    continue;
                var blockerKey = Str(other, "key");
                if (blockerKey is null) continue;

                // The link payload embeds the other issue's status whatever fields we asked for, so
                // the blocker's state costs no extra call.
                string? status = null;
                var category = IssueStatusCategory.Unknown;
                if (other.TryGetProperty("fields", out var of) && of.TryGetProperty("status", out var st))
                {
                    status = Str(st, "name");
                    category = MapCategory(st);
                }

                result.Add(new BlockingLink(key, blockerKey, status, category));
            }
        }
        return result;
    }

    /// <summary>
    /// Download an attachment's bytes. The URL comes from the issue's own attachment.content field,
    /// so it already points at whichever base we authenticated against.
    /// </summary>
    public async Task<byte[]?> DownloadAttachmentAsync(IssueAttachment attachment, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            await EnsureBaseAddressAsync(ct);
            var response = await _http.GetAsync(attachment.DownloadUrl, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return null; }
    }

    // ---- Transport ---------------------------------------------------------

    // Scoped Atlassian API tokens (the modern default) only authenticate through the gateway
    // https://api.atlassian.com/ex/jira/{cloudId}/... — hitting the site URL directly returns 401.
    // We look up the site's cloudId (a public, unauthenticated endpoint) and route through the
    // gateway; if that lookup fails we fall back to the site URL (legacy unscoped-token behaviour).
    // NOTE: the gateway base carries a path prefix, so request URIs MUST be relative with no leading
    // slash — a leading "/" would discard the "/ex/jira/{cloudId}" prefix.
    private async Task EnsureBaseAddressAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var site = _config.BaseUrl.TrimEnd('/');
            var apiBase = $"{site}/";
            try
            {
                using var probe = new HttpClient();
                var info = await probe.GetFromJsonAsync<TenantInfo>($"{site}/_edge/tenant_info", ct);
                if (!string.IsNullOrEmpty(info?.CloudId))
                    apiBase = $"https://api.atlassian.com/ex/jira/{info.CloudId}/";
            }
            catch { /* cloudId unavailable — keep the site URL */ }
            _http.BaseAddress = new Uri(apiBase);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private async Task<JsonElement?> SearchAsync(string jql, int maxResults, string[] fields, CancellationToken ct)
    {
        await EnsureBaseAddressAsync(ct);
        var payload = JsonSerializer.Serialize(new { jql, maxResults, fields });
        var body = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("rest/api/3/search/jql", body, ct);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Keys, filtered to what may safely be interpolated into a JQL <c>IN</c> clause. Keys reach us
    /// from a text box, and a key carrying a quote or a paren would otherwise be a JQL injection
    /// against the user's own Jira. Anything else is dropped rather than escaped - a malformed key
    /// has no issue behind it anyway.
    /// </summary>
    private static List<string> SafeKeys(IEnumerable<string> keys) => keys
        .Where(k => !string.IsNullOrWhiteSpace(k) && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IEnumerable<(string Key, JsonElement Issue)> Issues(JsonElement? search)
    {
        if (search is not JsonElement root || !root.TryGetProperty("issues", out var arr)
            || arr.ValueKind != JsonValueKind.Array) yield break;

        foreach (var issue in arr.EnumerateArray())
        {
            var key = Str(issue, "key");
            if (!string.IsNullOrEmpty(key)) yield return (key, issue);
        }
    }

    // Shape of GET {site}/_edge/tenant_info — used only to discover the cloudId for gateway routing.
    private sealed record TenantInfo([property: JsonPropertyName("cloudId")] string? CloudId);

    // ---- Mapping -----------------------------------------------------------

    /// <summary>
    /// The whole of Jira's shape, converted once. Every consumer downstream reads
    /// <see cref="Issue"/> and none of them has ever heard of ADF.
    /// </summary>
    private static Issue MapIssue(string key, JsonElement issue)
    {
        var f = Fields(issue);
        if (f.ValueKind != JsonValueKind.Object) return Issue.Empty(key);

        var names = AttachmentNames(f);

        return new Issue(
            Key: Str(issue, "key") ?? key,
            Summary: Str(f, "summary"),
            Description: f.TryGetProperty("description", out var d) ? AdfText.ToText(d, names) : null,
            // Jira has no acceptance-criteria field; on a Jira project the criteria live in the
            // description, or - far more often on a sub-task - in the parent's.
            AcceptanceCriteria: null,
            Status: Nested(f, "status", "name"),
            StatusCategory: f.TryGetProperty("status", out var st) ? MapCategory(st) : IssueStatusCategory.Unknown,
            Type: Nested(f, "issuetype", "name"),
            Priority: Nested(f, "priority", "name"),
            Assignee: Nested(f, "assignee", "displayName"),
            Created: Str(f, "created"),
            Updated: Str(f, "updated"),
            Parent: MapParent(f),
            Children: MapChildren(f),
            Comments: MapComments(f, names),
            Links: MapLinks(f),
            Attachments: MapAttachments(f));
    }

    /// <summary>Jira's category keys are <c>new</c> / <c>indeterminate</c> / <c>done</c>.</summary>
    private static IssueStatusCategory MapCategory(JsonElement status)
        => (status.TryGetProperty("statusCategory", out var sc) ? Str(sc, "key") : null) switch
        {
            "new" => IssueStatusCategory.Todo,
            "indeterminate" => IssueStatusCategory.InProgress,
            "done" => IssueStatusCategory.Done,
            _ => IssueStatusCategory.Unknown,
        };

    /// <summary>
    /// The inline parent object. Jira puts a summary, a status and a type in it but never a
    /// description - which is exactly the trap: a chain built from this alone would look populated
    /// while holding none of the requirements, so the walk re-fetches every hop. What this carries
    /// is the fallback for a hop the walk cannot fetch.
    /// </summary>
    private static IssueRef? MapParent(JsonElement fields)
    {
        if (Child(fields, "parent") is not JsonElement p) return null;
        var key = Str(p, "key");
        if (key is null) return null;
        var pf = Child(p, "fields") ?? default;
        return new IssueRef(key, Str(pf, "summary"), Nested(pf, "status", "name"), Nested(pf, "issuetype", "name"));
    }

    private static IReadOnlyList<IssueRef> MapChildren(JsonElement fields)
    {
        if (!fields.TryGetProperty("subtasks", out var subs) || subs.ValueKind != JsonValueKind.Array)
            return Array.Empty<IssueRef>();

        return subs.EnumerateArray()
            .Select(s => (Key: Str(s, "key"), Fields: Child(s, "fields") ?? default))
            .Where(s => s.Key is not null)
            .Select(s => new IssueRef(s.Key!, Str(s.Fields, "summary"),
                Nested(s.Fields, "status", "name"), Nested(s.Fields, "issuetype", "name")))
            .ToList();
    }

    private static IReadOnlyList<IssueComment> MapComments(JsonElement fields, IReadOnlyDictionary<string, string>? names)
    {
        if (!fields.TryGetProperty("comment", out var c) || c.ValueKind != JsonValueKind.Object
            || !c.TryGetProperty("comments", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<IssueComment>();

        var result = new List<IssueComment>();
        foreach (var cm in list.EnumerateArray())
        {
            var id = Str(cm, "id");
            if (id is null) continue;
            result.Add(new IssueComment(id,
                cm.TryGetProperty("author", out var a) ? Str(a, "displayName") : null,
                Str(cm, "created"),
                cm.TryGetProperty("body", out var b) ? AdfText.ToText(b, names) : null));
        }
        return result;
    }

    private static IReadOnlyList<IssueLink> MapLinks(JsonElement fields)
    {
        if (!fields.TryGetProperty("issuelinks", out var links) || links.ValueKind != JsonValueKind.Array)
            return Array.Empty<IssueLink>();

        var result = new List<IssueLink>();
        foreach (var link in links.EnumerateArray())
        {
            // Jira reports direction by which side the other issue sits on, and the human-readable
            // relationship differs per direction ("blocks" vs "is blocked by") - keep the right one.
            string relationship;
            JsonElement other;
            if (link.TryGetProperty("outwardIssue", out other) && other.ValueKind == JsonValueKind.Object)
                relationship = link.TryGetProperty("type", out var t) ? Str(t, "outward") ?? "relates to" : "relates to";
            else if (link.TryGetProperty("inwardIssue", out other) && other.ValueKind == JsonValueKind.Object)
                relationship = link.TryGetProperty("type", out var t2) ? Str(t2, "inward") ?? "relates to" : "relates to";
            else continue;

            var key = Str(other, "key");
            if (key is not null) result.Add(new IssueLink(relationship, key));
        }
        return result;
    }

    private static IReadOnlyList<IssueAttachment> MapAttachments(JsonElement fields)
    {
        if (!fields.TryGetProperty("attachment", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<IssueAttachment>();

        var result = new List<IssueAttachment>();
        foreach (var a in list.EnumerateArray())
        {
            var id = Str(a, "id");
            var url = Str(a, "content");
            if (id is null || url is null) continue;
            result.Add(new IssueAttachment(
                id,
                Str(a, "filename") ?? $"attachment-{id}",
                url,
                a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                Str(a, "mimeType"),
                a.TryGetProperty("author", out var au) ? Str(au, "displayName") : null,
                Str(a, "created")));
        }
        return result;
    }

    /// <summary>Attachment id → filename, so an embedded image flattens to a name instead of an id.</summary>
    private static Dictionary<string, string>? AttachmentNames(JsonElement fields)
    {
        if (!fields.TryGetProperty("attachment", out var att) || att.ValueKind != JsonValueKind.Array) return null;
        var map = new Dictionary<string, string>();
        foreach (var a in att.EnumerateArray())
        {
            var id = Str(a, "id");
            var name = Str(a, "filename");
            if (id is not null && name is not null) map[id] = name;
        }
        return map.Count > 0 ? map : null;
    }

    // ---- JSON helpers ------------------------------------------------------

    private static JsonElement Fields(JsonElement issue)
        => issue.ValueKind == JsonValueKind.Object && issue.TryGetProperty("fields", out var f) ? f : default;

    private static JsonElement? Child(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? Nested(JsonElement e, string obj, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
            ? Str(o, prop) : null;
}
