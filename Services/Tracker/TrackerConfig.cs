using System.Text.Json;

namespace Whetstone.Services.Tracker;

/// <summary>
/// One project's tracker settings, read from its <c>project.json</c> / <c>project.defaults.json</c>.
/// <para>
/// Deliberately one flat record rather than a per-provider hierarchy. The two trackers need almost
/// the same things - a base URL, a secret, and a couple of vocabulary decisions - and a shape that
/// mirrored their APIs would put a discriminated union in the settings UI to save four unused
/// strings in memory. Each provider reads the fields it needs and ignores the rest.
/// </para>
/// <para>
/// JSON shape (the <c>tracker</c> block):
/// <code>
/// "tracker": {
///   "kind": "jira",
///   "baseUrl": "https://your-org.atlassian.net",
///   "email": "you@example.com",
///   "token": "your-api-token"
/// }
/// "tracker": {
///   "kind": "azureDevOps",
///   "baseUrl": "https://dev.azure.com/your-org",
///   "project": "YourTeamProject",
///   "token": "your-personal-access-token"
/// }
/// </code>
/// The legacy <c>"jira": { siteUrl, email, apiToken }</c> block is still read, and still means what
/// it always did, so an existing project keeps working without being touched.
/// </para>
/// </summary>
public class TrackerConfig
{
    public TrackerKind Kind { get; init; } = TrackerKind.None;

    /// <summary>
    /// The tenant root. Jira: <c>https://your-org.atlassian.net</c>. Azure DevOps: the organisation,
    /// <c>https://dev.azure.com/your-org</c> (or a Server/on-prem collection URL).
    /// </summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The account the token belongs to. Jira only - Azure DevOps authenticates a PAT with
    /// an empty username and ignores whatever is put here.</summary>
    public string Email { get; init; } = "";

    /// <summary>The secret: a Jira API token, or an Azure DevOps personal access token.</summary>
    public string Token { get; init; } = "";

    /// <summary>The Azure DevOps team project. Jira does not need one - the issue key names it.</summary>
    public string Project { get; init; } = "";

    /// <summary>
    /// Which link types mean "this cannot start yet".
    /// <para>
    /// <b>Null and empty mean different things.</b> Null (the key is absent) means "use the
    /// provider's default". An empty-but-present list means "nothing upstream blocks anything here",
    /// which is a real answer a team may want and could not otherwise give.
    /// </para>
    /// <para>
    /// Configurable because the two trackers disagree about what blocking even is. Jira has a
    /// "Blocks" link type that means impossibility, and its other link types (Gantt ordering, "Has
    /// predecessor") deliberately do not count - they express a preferred sequence, and gating
    /// unattended work on a scheduling hint would strand tickets for reasons their author never
    /// intended. Azure DevOps has no impossibility link at all: its nearest relation is
    /// Predecessor/Successor, which is exactly the scheduling hint Jira users are told to ignore. So
    /// whether it should gate anything is a decision for the team, not for this code.
    /// </para>
    /// </summary>
    public string[]? BlockingLinkTypes { get; init; }

    /// <summary>
    /// Azure DevOps only: which <c>System.State</c> values mean finished. Null or empty means the
    /// default below, which covers all four stock process templates (Agile, Scrum, Basic, CMMI).
    /// <para>
    /// Unlike <see cref="BlockingLinkTypes"/> an empty list is NOT a distinct answer here: "no state
    /// means finished" would say every blocker blocks forever, which nobody wants and which would
    /// look exactly like a bug from the board.
    /// </para>
    /// <para>
    /// Jira needs no equivalent - every Jira status carries a category, so the tracker answers
    /// "is it done?" itself. Azure DevOps' <c>System.State</c> is per-process free text with the
    /// category only reachable through a separate metadata call per work item type, and a list of
    /// state names is both cheaper and something a team can actually read and correct.
    /// </para>
    /// </summary>
    public string[]? DoneStates { get; init; }

    /// <summary>Default finished-states for Azure DevOps. Agile closes at <c>Closed</c>, Scrum and
    /// Basic at <c>Done</c>, and every template can drop work at <c>Removed</c> - which no longer
    /// blocks anything, which is the only question this list is asked.</summary>
    public static readonly string[] DefaultAzureDoneStates = { "Closed", "Done", "Removed" };

    /// <summary>
    /// Only "Blocks" - the one a Jira user actually reaches for, and the one the word blocked means
    /// to them.
    /// </summary>
    public static readonly string[] DefaultJiraBlockingLinkTypes = { "Blocks" };

    /// <summary>
    /// <c>Dependency-Reverse</c> is the Predecessor side: the work item that has to finish first.
    /// The honest best available, and weaker than Jira's - see <see cref="BlockingLinkTypes"/> for
    /// why a team may want to clear it.
    /// </summary>
    public static readonly string[] DefaultAzureBlockingLinkTypes = { "System.LinkTypes.Dependency-Reverse" };

    /// <summary>What actually gates a ticket, with the provider default filled in.</summary>
    public IReadOnlyList<string> EffectiveBlockingLinkTypes => BlockingLinkTypes ?? Kind switch
    {
        TrackerKind.Jira => DefaultJiraBlockingLinkTypes,
        TrackerKind.AzureDevOps => DefaultAzureBlockingLinkTypes,
        _ => Array.Empty<string>(),
    };

    /// <summary>What actually counts as finished, with the default filled in.</summary>
    public IReadOnlyList<string> EffectiveDoneStates =>
        DoneStates is { Length: > 0 } ? DoneStates : DefaultAzureDoneStates;

    /// <summary>Whether this project has enough to talk to its tracker at all.</summary>
    public bool IsConfigured => Kind switch
    {
        TrackerKind.Jira => Has(BaseUrl) && Has(Email) && Has(Token),
        TrackerKind.AzureDevOps => Has(BaseUrl) && Has(Project) && Has(Token),
        _ => false,
    };

    private static bool Has(string s) => !string.IsNullOrWhiteSpace(s);

    // ---- Reading -----------------------------------------------------------

    /// <summary>
    /// Read the tracker block out of a merged project config, falling back to the legacy
    /// <c>jira</c> block. An unreadable or absent block yields <see cref="TrackerKind.None"/>,
    /// which is a working configuration - not an error.
    /// </summary>
    public static TrackerConfig From(IReadOnlyDictionary<string, JsonElement> cfg)
    {
        if (cfg.TryGetValue("tracker", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            var kind = ParseKind(Str(t, "kind"));
            // A tracker block that names no kind but carries Jira's fields is still a Jira block -
            // guessing here beats refusing to read a config a human clearly meant.
            if (kind == TrackerKind.None && (Has(Str(t, "email")) || Has(Str(t, "siteUrl"))))
                kind = TrackerKind.Jira;

            return new TrackerConfig
            {
                Kind = kind,
                // Aliases, because the two vendors' own words for the same field are different and
                // a config that reads naturally is worth four lines here.
                BaseUrl = Trim(First(Str(t, "baseUrl"), Str(t, "siteUrl"), Str(t, "orgUrl"), Str(t, "url"))),
                Email = Trim(Str(t, "email")),
                Token = Trim(First(Str(t, "token"), Str(t, "apiToken"), Str(t, "pat"))),
                Project = Trim(Str(t, "project")),
                BlockingLinkTypes = StrArray(t, "blockingLinkTypes"),
                DoneStates = StrArray(t, "doneStates"),
            };
        }

        if (cfg.TryGetValue("jira", out var j) && j.ValueKind == JsonValueKind.Object)
        {
            return new TrackerConfig
            {
                Kind = TrackerKind.Jira,
                BaseUrl = Trim(First(Str(j, "siteUrl"), Str(j, "baseUrl"))),
                Email = Trim(Str(j, "email")),
                Token = Trim(First(Str(j, "apiToken"), Str(j, "token"))),
            };
        }

        return new TrackerConfig();
    }

    /// <summary>
    /// Parse the <c>kind</c> string generously. The vendor's own name for Azure DevOps has changed
    /// three times (VSTS, TFS, Azure DevOps) and people write it every way there is; refusing a
    /// config over the hyphen in "azure-devops" would be a poor use of anyone's afternoon.
    /// </summary>
    public static TrackerKind ParseKind(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "jira" => TrackerKind.Jira,
        "azuredevops" or "azure-devops" or "azure_devops" or "azure" or "ado" or "devops"
            or "azureboards" or "azure-boards" or "vsts" or "tfs" => TrackerKind.AzureDevOps,
        _ => TrackerKind.None,
    };

    /// <summary>The canonical string for <see cref="ParseKind"/> to read back - what we write out.</summary>
    public static string KindToString(TrackerKind kind) => kind switch
    {
        TrackerKind.Jira => "jira",
        TrackerKind.AzureDevOps => "azureDevOps",
        _ => "none",
    };

    private static string First(params string[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";

    private static string Trim(string s) => s.Trim();

    private static string Str(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Null when the key is absent, which is what lets an empty-but-present list mean something
    /// different from a missing one - see <see cref="BlockingLinkTypes"/>.
    /// </summary>
    private static string[]? StrArray(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
               .Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToArray()
            : null;
}
