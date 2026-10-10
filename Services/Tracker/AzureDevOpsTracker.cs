using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Whetstone.Services.Tracker;

/// <summary>
/// Azure DevOps Boards (work items), read-only, through the REST API.
/// <para>
/// Everything Azure-shaped lives behind this class. Four differences from Jira matter enough to
/// name, because each one is a place a thinner abstraction would have leaked:
/// </para>
/// <list type="number">
/// <item><b>WIQL returns ids, not issues.</b> There is no single call that filters and returns
/// fields, so every search here is a query followed by a batch read. That is why
/// <see cref="IIssueTracker"/> exposes intents rather than a query string.</item>
/// <item><b>The hierarchy is a link.</b> Jira reports a parent in <c>fields.parent</c> and never as
/// a link; Azure DevOps has no parent field at all and reports it only as
/// <c>System.LinkTypes.Hierarchy-Reverse</c>. Both arrive as <see cref="Issue.ParentKey"/>.</item>
/// <item><b>Long text is HTML.</b> Flattened by <see cref="HtmlText"/>, the counterpart to
/// <see cref="AdfText"/>.</item>
/// <item><b>There is no status category.</b> <c>System.State</c> is per-process free text, so
/// "is it finished?" is answered from <see cref="TrackerConfig.DoneStates"/>.</item>
/// </list>
/// <para>
/// <b>There are no write methods here, by design</b> - the same rule the Jira side follows. Local
/// tickets own all state; the board is read for context and never touched.
/// </para>
/// </summary>
public sealed class AzureDevOpsTracker : IIssueTracker
{
    /// <summary>
    /// 7.0 rather than the newest. It is supported by Azure DevOps Services and by Azure DevOps
    /// Server 2022, and carries everything used here - notably <c>workitemsbatch</c> with an error
    /// policy. Pinning to the newest would drop on-premises collections for no capability gained.
    /// </summary>
    private const string ApiVersion = "7.0";

    /// <summary>Work item comments are still a preview route on every current server.</summary>
    private const string CommentsApiVersion = "7.0-preview.3";

    /// <summary>The documented ceiling on one <c>workitemsbatch</c> request.</summary>
    private const int BatchLimit = 200;

    /// <summary>Fields worth asking for. Anything not listed here is never read.</summary>
    private static readonly string[] ReadFields =
    {
        "System.Id", "System.Title", "System.Description", "System.State", "System.WorkItemType",
        "System.AssignedTo", "System.CreatedDate", "System.ChangedDate", "System.TeamProject",
        "Microsoft.VSTS.Common.Priority", "Microsoft.VSTS.Common.AcceptanceCriteria",
        "Microsoft.VSTS.TCM.ReproSteps",
    };

    private readonly TrackerConfig _config;
    private readonly HttpClient _http;
    private readonly HashSet<string> _doneStates;

    public AzureDevOpsTracker(TrackerConfig config)
    {
        _config = config;
        _doneStates = new HashSet<string>(config.EffectiveDoneStates, StringComparer.OrdinalIgnoreCase);

        _http = new HttpClient();
        if (_config.IsConfigured)
        {
            _http.BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
            // A personal access token authenticates as Basic with an EMPTY username. Putting the
            // user's email in front of it - which looks right, and is right for Jira - fails.
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_config.Token}")));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public TrackerKind Kind => TrackerKind.AzureDevOps;
    public string DisplayName => "Azure Boards";
    public string IssueNoun => "work item";
    public bool IsConfigured => _config.IsConfigured;

    // ---- Identity ----------------------------------------------------------

    public string? IssueUrl(string key)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(key)) return null;
        var org = _config.BaseUrl.TrimEnd('/');
        return $"{org}/{Uri.EscapeDataString(_config.Project)}/_workitems/edit/{Uri.EscapeDataString(key)}";
    }

    /// <summary>
    /// A work item is identified by a bare integer, so this strips the decorations people paste
    /// around one - <c>#1234</c> from the browser, <c>AB#1234</c> from a commit message - and
    /// refuses anything that is not left as digits. Refusing is the point: on an Azure DevOps
    /// project a Jira-style key names nothing, and silently storing it would produce a ticket that
    /// looks linked and resolves to nothing forever.
    /// </summary>
    public string? NormalizeKey(string? candidate)
    {
        var s = (candidate ?? "").Trim();
        if (s.StartsWith("AB#", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        s = s.TrimStart('#').Trim();
        return s.Length > 0 && s.All(char.IsAsciiDigit) ? s : null;
    }

    public string BranchToken(string key) => key;

    /// <summary>
    /// <c>AB#1234</c>. Azure DevOps links a commit or a pull request to a work item only when the
    /// message carries this exact form; a bare <c>1234:</c> prefix reads as noise to a human and
    /// links to nothing at all, which throws away most of the reason to link a ticket to a board.
    /// </summary>
    public string CommitRef(string key) => $"AB#{key}";

    // ---- Reads -------------------------------------------------------------

    public async Task<Issue?> GetIssueAsync(string key, CancellationToken ct = default)
    {
        var wi = await GetWorkItemAsync(key, ct);
        if (wi is not JsonElement item) return null;
        return await MapAsync(key, item, withComments: true, ct);
    }

    public async Task<Issue?> GetAncestorAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var wi = await GetWorkItemAsync(key, ct);
            if (wi is not JsonElement item) return null;
            // No comments for an ancestor: they are the parent's own conversation, and the child is
            // here for its requirements, not its discussion.
            return await MapAsync(key, item, withComments: false, ct);
        }
        catch { return null; }
    }

    /// <summary>One work item with its relations - which is where the parent, the children, the
    /// links and the attachments all live.</summary>
    private async Task<JsonElement?> GetWorkItemAsync(string key, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var response = await _http.GetAsync(
            $"_apis/wit/workitems/{Uri.EscapeDataString(key)}?$expand=relations&api-version={ApiVersion}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<IReadOnlyDictionary<string, Issue>> GetBriefsAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var map = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in await BatchAsync(Ids(keys), relations: false, ct))
        {
            var key = KeyOf(item);
            if (key is not null) map[key] = MapFields(key, item, Array.Empty<IssueRef>(),
                Array.Empty<IssueComment>(), Array.Empty<IssueLink>(), Array.Empty<IssueAttachment>(), null);
        }
        return map;
    }

    public async Task<IReadOnlyList<IssueRef>> GetMyUnfinishedAsync(int max, CancellationToken ct = default)
    {
        // WIQL cannot express "not in a done category" - there is no category - so the configured
        // done states do the filtering, and they are quoted defensively because they come from a
        // config file a person edits.
        var excluded = string.Join(", ", _doneStates.Select(s => $"'{s.Replace("'", "''")}'"));
        var wiql =
            "SELECT [System.Id] FROM WorkItems "
            + "WHERE [System.AssignedTo] = @Me "
            + "AND [System.TeamProject] = @project "
            + $"AND [System.State] NOT IN ({excluded}) "
            + "ORDER BY [System.ChangedDate] DESC";

        var ids = await WiqlAsync(wiql, max, ct);
        if (ids.Count == 0) return Array.Empty<IssueRef>();

        // WIQL hands back ids and nothing else, so the fields it was queried on still have to be
        // read. Order is re-imposed from the query result: a batch read comes back id-ordered.
        var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in await BatchAsync(ids, relations: false, ct))
            if (KeyOf(item) is { } k) byId[k] = item;

        return ids.Where(byId.ContainsKey)
            .Select(id => RefOf(id, byId[id]))
            .ToList();
    }

    public async Task<IReadOnlyList<BlockingLink>> GetBlockingLinksAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var blocking = new HashSet<string>(_config.EffectiveBlockingLinkTypes, StringComparer.OrdinalIgnoreCase);
        if (blocking.Count == 0) return Array.Empty<BlockingLink>();   // this project has said nothing blocks

        // Which issue waits on which - discovered first, resolved second, because the relation
        // carries only a URL and a blocker's state is the thing that decides whether it still counts.
        var pairs = new List<(string Issue, string Blocker)>();
        foreach (var item in await BatchAsync(Ids(keys), relations: true, ct))
        {
            var key = KeyOf(item);
            if (key is null) continue;
            foreach (var rel in Relations(item))
            {
                var relType = Str(rel, "rel");
                if (relType is null || !blocking.Contains(relType)) continue;
                var blocker = WorkItemIdFromUrl(Str(rel, "url"));
                if (blocker is not null) pairs.Add((key, blocker));
            }
        }
        if (pairs.Count == 0) return Array.Empty<BlockingLink>();

        var states = new Dictionary<string, (string? Status, IssueStatusCategory Category)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in await BatchAsync(pairs.Select(p => p.Blocker).Distinct().ToList(), relations: false, ct))
        {
            var key = KeyOf(item);
            if (key is null) continue;
            var state = Field(item, "System.State");
            states[key] = (state, Categorise(state));
        }

        return pairs.Select(p =>
        {
            var known = states.TryGetValue(p.Blocker, out var s) ? s : (null, IssueStatusCategory.Unknown);
            return new BlockingLink(p.Issue, p.Blocker, known.Item1, known.Item2);
        }).ToList();
    }

    public async Task<byte[]?> DownloadAttachmentAsync(IssueAttachment attachment, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            var url = attachment.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url)) return null;
            // The relation URL serves metadata without it; download=true is what returns the bytes.
            if (!url.Contains("download=", StringComparison.OrdinalIgnoreCase))
                url += (url.Contains('?') ? "&" : "?") + "download=true";

            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return null; }
    }

    // ---- Transport ---------------------------------------------------------

    /// <summary>
    /// Read many work items in as few calls as the service allows, skipping any this token cannot
    /// see. <c>errorPolicy: omit</c> matters: without it one deleted or cross-project id fails the
    /// whole batch, and a board render would go blank because of a single stale link.
    /// </summary>
    private async Task<List<JsonElement>> BatchAsync(IReadOnlyCollection<string> ids, bool relations, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        if (!IsConfigured || ids.Count == 0) return result;

        foreach (var chunk in Chunk(ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), BatchLimit))
        {
            // fields and $expand are mutually exclusive on this route - asking for both is a 400.
            var body = new Dictionary<string, object> { ["ids"] = chunk, ["errorPolicy"] = "omit" };
            if (relations) body["$expand"] = "relations";
            else body["fields"] = ReadFields;

            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"_apis/wit/workitemsbatch?api-version={ApiVersion}", content, ct);
            response.EnsureSuccessStatusCode();

            var root = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                result.AddRange(arr.EnumerateArray().Select(e => e.Clone()));
        }
        return result;
    }

    /// <summary>Run a WIQL query and return the ids it selected, in the order it returned them.</summary>
    private async Task<List<string>> WiqlAsync(string query, int max, CancellationToken ct)
    {
        var ids = new List<string>();
        if (!IsConfigured) return ids;

        var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(
            $"{Uri.EscapeDataString(_config.Project)}/_apis/wit/wiql?$top={Math.Clamp(max, 1, 1000)}&api-version={ApiVersion}",
            content, ct);
        response.EnsureSuccessStatusCode();

        var root = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        if (root.TryGetProperty("workItems", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var wi in arr.EnumerateArray())
                if (wi.TryGetProperty("id", out var id))
                    ids.Add(id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString() ?? "");

        return ids.Where(i => i.Length > 0).Take(max).ToList();
    }

    /// <summary>
    /// A work item's comments, which are a route of their own rather than a field - so this is an
    /// extra round-trip on every full read, and is skipped for ancestors and briefs.
    /// </summary>
    private async Task<IReadOnlyList<IssueComment>> CommentsAsync(string key, CancellationToken ct)
    {
        var comments = new List<IssueComment>();
        try
        {
            var url = $"{Uri.EscapeDataString(_config.Project)}/_apis/wit/workItems/{Uri.EscapeDataString(key)}"
                    + $"/comments?$top=200&api-version={CommentsApiVersion}";

            // Bounded rather than "until exhausted": a ticket with thousands of comments is a
            // pathology, and reading all of them would cost more than it could ever tell a reader.
            for (var page = 0; page < 5 && url is not null; page++)
            {
                var response = await _http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) break;
                var root = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));

                if (root.TryGetProperty("comments", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var c in arr.EnumerateArray())
                    {
                        var id = c.TryGetProperty("id", out var i)
                            ? (i.ValueKind == JsonValueKind.Number ? i.GetInt64().ToString() : i.GetString())
                            : null;
                        if (id is null) continue;
                        comments.Add(new IssueComment(id,
                            c.TryGetProperty("createdBy", out var by) ? Str(by, "displayName") : null,
                            Str(c, "createdDate"),
                            HtmlText.ToText(Str(c, "text"))));
                    }

                url = Str(root, "continuationToken") is { Length: > 0 } token
                    ? $"{Uri.EscapeDataString(_config.Project)}/_apis/wit/workItems/{Uri.EscapeDataString(key)}"
                      + $"/comments?$top=200&continuationToken={Uri.EscapeDataString(token)}&api-version={CommentsApiVersion}"
                    : null;
            }
        }
        catch { /* comments are context; losing them must not fail the read */ }
        return comments;
    }

    // ---- Mapping -----------------------------------------------------------

    private async Task<Issue> MapAsync(string key, JsonElement item, bool withComments, CancellationToken ct)
    {
        var attachments = MapAttachments(item);
        var childIds = new List<string>();
        var links = new List<IssueLink>();

        foreach (var rel in Relations(item))
        {
            var relType = Str(rel, "rel");
            var target = WorkItemIdFromUrl(Str(rel, "url"));
            if (relType is null || target is null) continue;

            switch (relType)
            {
                // The parent is read off the relations by MapFields; here we only need to keep it
                // out of the sideways-links list, where it would read as an unrelated reference.
                case "System.LinkTypes.Hierarchy-Reverse":
                    break;
                case "System.LinkTypes.Hierarchy-Forward":
                    childIds.Add(target);
                    break;
                default:
                    if (relType.StartsWith("System.LinkTypes.", StringComparison.Ordinal))
                        // attributes.name is the relation's human phrasing - "Related",
                        // "Predecessor", "Duplicate Of" - which is what a reader wants over the
                        // machine name, and is the same role Jira's inward/outward text plays.
                        links.Add(new IssueLink(
                            (rel.TryGetProperty("attributes", out var at) ? Str(at, "name") : null)
                                ?? PlainRelation(relType),
                            target));
                    break;
            }
        }

        // Children have to be read to be worth listing. A relation carries a URL and nothing else,
        // so an unresolved list would be a row of bare numbers - which tells a reader nothing about
        // where their own ticket's scope stops, and that is the entire job of the sibling list.
        var children = childIds.Count == 0
            ? Array.Empty<IssueRef>()
            : await ResolveRefsAsync(childIds, ct);

        var comments = withComments ? await CommentsAsync(key, ct) : Array.Empty<IssueComment>();

        return MapFields(key, item, children, comments, links, attachments, AttachmentNames(attachments));
    }

    private async Task<IReadOnlyList<IssueRef>> ResolveRefsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        try
        {
            return (await BatchAsync(ids, relations: false, ct))
                .Select(i => KeyOf(i) is { } k ? RefOf(k, i) : null)
                .Where(r => r is not null).Select(r => r!)
                .ToList();
        }
        catch
        {
            // Better a list of bare ids than no list: "there are four siblings and I could not read
            // them" is still a truer picture of the surrounding work than silence.
            return ids.Select(id => new IssueRef(id, null, null, null)).ToList();
        }
    }

    private Issue MapFields(string key, JsonElement item, IReadOnlyList<IssueRef> children,
        IReadOnlyList<IssueComment> comments, IReadOnlyList<IssueLink> links,
        IReadOnlyList<IssueAttachment> attachments, IReadOnlyDictionary<string, string>? names)
    {
        var state = Field(item, "System.State");

        // A Bug keeps its statement of the problem in ReproSteps and leaves System.Description
        // empty. Reading only the description would hand a phase a blank ticket and let it conclude
        // the bug was never written up.
        var description = HtmlText.ToText(Field(item, "System.Description"), names);
        var repro = HtmlText.ToText(Field(item, "Microsoft.VSTS.TCM.ReproSteps"), names);
        if (description is null) description = repro;
        else if (repro is not null) description = $"{description}\n\n## Repro steps\n\n{repro}";

        return new Issue(
            Key: key,
            Summary: Field(item, "System.Title"),
            Description: description,
            AcceptanceCriteria: HtmlText.ToText(Field(item, "Microsoft.VSTS.Common.AcceptanceCriteria"), names),
            Status: state,
            StatusCategory: Categorise(state),
            Type: Field(item, "System.WorkItemType"),
            Priority: Field(item, "Microsoft.VSTS.Common.Priority") is { Length: > 0 } p ? $"Priority {p}" : null,
            Assignee: Identity(item, "System.AssignedTo"),
            Created: Field(item, "System.CreatedDate"),
            Updated: Field(item, "System.ChangedDate"),
            // Id only. A hierarchy relation carries a URL and nothing else, so unlike Jira there is
            // no inline summary to fall back on when the parent itself cannot be read.
            Parent: ParentOf(item) is { } parent ? new IssueRef(parent, null, null, null) : null,
            Children: children,
            Comments: comments,
            Links: links,
            Attachments: attachments);
    }

    /// <summary>The parent read straight off the relations, for the paths that do not walk them all.</summary>
    private static string? ParentOf(JsonElement item) => Relations(item)
        .Where(r => Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse")
        .Select(r => WorkItemIdFromUrl(Str(r, "url")))
        .FirstOrDefault(id => id is not null);

    private static IReadOnlyList<IssueAttachment> MapAttachments(JsonElement item)
    {
        var result = new List<IssueAttachment>();
        foreach (var rel in Relations(item))
        {
            if (Str(rel, "rel") != "AttachedFile") continue;
            var url = Str(rel, "url");
            var id = HtmlText.AttachmentIdFromUrl(url);
            if (url is null || id is null) continue;

            var attrs = rel.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            result.Add(new IssueAttachment(
                id,
                Str(attrs, "name") ?? $"attachment-{id}",
                url,
                attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("resourceSize", out var size)
                    && size.TryGetInt64(out var bytes) ? bytes : 0,
                // Azure DevOps reports no MIME type on an attachment relation; the extension is all
                // there is, and the store keys its decisions off that anyway.
                null,
                // Nor an uploader. Saying nothing beats inventing a name.
                null,
                Str(attrs, "resourceCreatedDate")));
        }
        return result;
    }

    /// <summary>Attachment id (its URL's GUID) → filename, so an embedded image in the HTML flattens
    /// to the name a human uploaded rather than to a URL.</summary>
    private static Dictionary<string, string>? AttachmentNames(IReadOnlyList<IssueAttachment> attachments)
    {
        if (attachments.Count == 0) return null;
        var map = new Dictionary<string, string>();
        foreach (var a in attachments) map[a.Id] = a.FileName;
        return map;
    }

    /// <summary>
    /// Whether a state means finished. Everything not named as done and not obviously unstarted is
    /// reported as in progress - a coarse answer, but the only question the system actually asks of
    /// a status is whether it is done, and that half is exact.
    /// </summary>
    private IssueStatusCategory Categorise(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return IssueStatusCategory.Unknown;
        if (_doneStates.Contains(state)) return IssueStatusCategory.Done;
        return UnstartedStates.Contains(state) ? IssueStatusCategory.Todo : IssueStatusCategory.InProgress;
    }

    /// <summary>The opening state of each stock process template (Agile, Scrum, Basic, CMMI).</summary>
    private static readonly HashSet<string> UnstartedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "New", "To Do", "Proposed", "Approved", "Open",
    };

    private static IssueRef RefOf(string key, JsonElement item) => new(
        key, Field(item, "System.Title"), Field(item, "System.State"), Field(item, "System.WorkItemType"));

    // ---- JSON helpers ------------------------------------------------------

    private static IEnumerable<JsonElement> Relations(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("relations", out var r)
           && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>The work item's own id, as a string - the key everything else is filed under.</summary>
    private static string? KeyOf(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
            : null;

    /// <summary>
    /// One field out of the flat <c>fields</c> dictionary, as a string whatever its JSON type -
    /// Priority arrives as a number and dates as strings, and every consumer here wants text.
    /// </summary>
    private static string? Field(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object
            || !f.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    /// <summary>An identity field. Modern API versions return an object; older ones a bare string.</summary>
    private static string? Identity(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object
            || !f.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object => Str(v, "displayName") ?? Str(v, "uniqueName"),
            _ => null,
        };
    }

    /// <summary>The work item id at the end of an <c>_apis/wit/workItems/{id}</c> URL.</summary>
    private static string? WorkItemIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = url.Split('?')[0].TrimEnd('/');
        var last = path[(path.LastIndexOf('/') + 1)..];
        return last.Length > 0 && last.All(char.IsAsciiDigit) ? last : null;
    }

    /// <summary>"System.LinkTypes.Related" → "related". A fallback for a relation with no
    /// human-readable name attached, which is better than showing the reader a namespace.</summary>
    private static string PlainRelation(string rel)
    {
        var name = rel[(rel.LastIndexOf('.') + 1)..].Replace("-", " ");
        return name.Length == 0 ? "relates to" : name.ToLowerInvariant();
    }

    private static IReadOnlyList<string> Ids(IEnumerable<string> keys)
        => keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.All(char.IsAsciiDigit)).Distinct().ToList();

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
