using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>One rate-limit window as the dashboard shows it: how full it is and when it empties.</summary>
public record UsageLimit(
    string Kind,
    string Group,
    string Label,
    double Percent,
    string Severity,
    DateTimeOffset? ResetsAt,
    bool IsActive);

/// <summary>
/// The last thing we heard back from Anthropic, plus whether we could ask at all. Always safe to
/// render: a failed poll leaves <see cref="Limits"/> holding the previous good answer, so the UI
/// shows a stale number with a warning rather than blanking out on one dropped request.
/// </summary>
public record UsageSnapshot(
    string Status,
    string? Message,
    DateTimeOffset? FetchedAt,
    IReadOnlyList<UsageLimit> Limits)
{
    public static readonly UsageSnapshot Empty =
        new("unknown", null, null, Array.Empty<UsageLimit>());
}

/// <summary>
/// How much of the Claude subscription's rate-limit headroom is gone, and when it comes back.
///
/// <para>
/// This is the number the whole fleet actually runs on. Cycle tokens in <see cref="MetricsService"/>
/// say what the pipeline spent; this says what is left to spend, which is the one that decides
/// whether starting three more agents is a good idea. It is also account-wide, so it counts the
/// user's own interactive sessions alongside the heartbeat's — the dashboard could never derive it
/// from its own logs.
/// </para>
///
/// <para>
/// Prefers a token of the dashboard's own, from <c>claude setup-token</c>, set in the environment or
/// in global config. That is the only source that survives the CLI changing where it keeps its
/// credentials — which it does. Everything else here is the fallback for not having set one up.
/// </para>
///
/// <para>
/// The fallback is Claude Code's credential file, read fresh on every poll and renewed with the
/// refresh grant when the access token has aged out, because the CLI itself only refreshes when a
/// <c>claude</c> process happens to run. That renewal makes this a second writer to a file the CLI
/// owns, so the write is careful rather than avoided: only once the token is genuinely spent, and
/// only if the refresh token on disk is still the one the grant was made with. If the CLI got there
/// first, its file wins untouched and we use what it left.
/// </para>
///
/// <para>
/// Nothing is cached on the instance and no token is ever logged.
/// </para>
/// </summary>
public class ClaudeUsageService
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// Where the refresh grant is redeemed, and the client it is redeemed for. Both are Claude Code's
    /// own — a grant is only honoured for the client the token was issued to, so neither is a value
    /// to guess at: they are <c>TOKEN_URL</c> and <c>CLIENT_ID</c> as the CLI binary carries them.
    /// </summary>
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>
    /// The endpoint rate-limits hard on requests that do not identify as the CLI, so the User-Agent
    /// is not decoration. The version is only cosmetic within it — any claude-code/x.y.z is accepted.
    /// </summary>
    private const string UserAgent = "claude-code/2.1.81";

    /// <summary>
    /// Renew this far ahead of the recorded expiry. A token that dies mid-flight costs a whole poll
    /// cycle, and the clock on the file was written by a different process than the one reading it.
    /// </summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    /// <summary>The longest we will ever wait between renewal attempts, however often they are refused.</summary>
    private static readonly TimeSpan MaxRenewBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// What to say when there is no usable login. Both name the dashboard's own token first, because
    /// signing the CLI in only fixes this until the next time nobody runs it — and the whole point of
    /// this meter is to be right when nobody is at the keyboard.
    /// </summary>
    private const string NoLogin =
        "No Claude login for the dashboard - run `claude setup-token` and paste it into Settings.";
    private const string SignIn =
        "The dashboard's Claude login has expired - run `claude setup-token` and paste it into Settings.";

    /// <summary>
    /// What a failing agent run and the sign-in banner both say. Deliberately not <see cref="SignIn"/>:
    /// that one names a setup-token, which fixes this meter and does nothing whatever for the CLI,
    /// because the CLI reads its own credential file and has never heard of the dashboard's config.
    /// Sending someone to paste a token while their runs are failing is sending them to fix the
    /// wrong thing, in a place where they will believe they succeeded.
    /// </summary>
    private const string CliSignIn =
        "Claude Code's login has expired and could not be renewed - sign in again to start agent runs.";

    /// <summary>
    /// The status for a renewal that failed without saying anything about the login: the token
    /// endpoint rate-limited us, or the request never landed. Split out from <c>error</c> because the
    /// two need opposite things from the user. An <c>error</c> is the usage API misbehaving and there
    /// is nothing to do but wait; this one means the token on disk is spent and the dashboard could
    /// not replace it - which running the CLI once fixes immediately, since the CLI renews through
    /// its own caller identity and knows nothing of the backoff here. Same reason it is still not a
    /// sign-out: nothing about a 429 says the credential is dead. See NudgeAsync.
    /// </summary>
    private const string RenewBlocked = "renew-blocked";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger<ClaudeUsageService> _log;
    private readonly AgentProviderFactory _providers;
    private readonly ProjectRegistry _registry;
    private readonly object _gate = new();

    private UsageSnapshot _snapshot = UsageSnapshot.Empty;
    /// <summary>Set on a 429 so the probe backs off instead of hammering an endpoint that is already angry.</summary>
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    /// <summary>
    /// The same, for the token endpoint, which is the stricter of the two: it rate-limits on rejected
    /// grants specifically, so a refresh token that has genuinely died must not be retried every
    /// minute until the user notices. The reason travels with the deadline because every poll during
    /// the wait has to answer the user, and "rate-limited" and "signed out" ask for different things.
    /// </summary>
    private DateTimeOffset _renewAfter = DateTimeOffset.MinValue;
    private (string Status, string Message) _renewBlocked;
    /// <summary>Consecutive refusals, doubling the wait. A grant refused all morning will be refused at noon.</summary>
    private int _renewRefusals;
    /// <summary>
    /// Serialises the pre-run renewal against itself. Not the timed poll - that is one caller and
    /// races with it are worth at most one wasted request. This is for the queue: see EnsureSignedInAsync.
    /// </summary>
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    /// <summary>
    /// Whether the current backoff window has already been overridden by someone starting a run.
    /// Cleared by <see cref="Backoff"/>, so each new wait is worth exactly one override and a queue
    /// of runs cannot spend the same allowance once per ticket.
    /// </summary>
    private bool _renewOverrideSpent;
    /// <summary>Set when the API rejects a token the file still considers valid - see the 401 branch.</summary>
    private bool _rejected;
    /// <summary>
    /// Why a human has to sign the CLI in by hand, or null while nothing does.
    ///
    /// <para>
    /// Set only where we have concluded the login on disk cannot be repaired from here - a refused
    /// grant, a missing file, a login that came without a refresh token - and cleared the moment
    /// anything works. Deliberately narrower than the meter's status: a rate-limited renewal or a
    /// dropped request is not a sign-out, and a banner that cries wolf on those is one the user
    /// learns to scroll past.
    /// </para>
    /// </summary>
    private string? _signInMessage;

    public ClaudeUsageService(
        ILogger<ClaudeUsageService> log, AgentProviderFactory providers, ProjectRegistry registry)
    {
        _log = log;
        _providers = providers;
        _registry = registry;
    }

    public UsageSnapshot Current
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// The login the fleet is spending, read fresh each time for the same reason the provider is: it
    /// is a switch on the dashboard, and one captured at startup would do nothing until a restart.
    /// </summary>
    private ClaudeAccount Account => ClaudeAccounts.Active(_registry.GetGlobalConfig());

    /// <summary>The account the state on this instance was gathered for; null until the first poll.</summary>
    private string? _account;

    public string AccountName => Account.Name;

    public IReadOnlyList<ClaudeAccount> Accounts => ClaudeAccounts.All(_registry.GetGlobalConfig());

    /// <summary>
    /// Forget everything learned about the previous account the moment another is in force. Its
    /// numbers, its backoffs and its sign-in banner all describe a different subscription, and a
    /// meter showing the old account's 96% over the new account's name is the one wrong answer this
    /// feature could give. Called by everything that reads the login, so a hand-edited config file
    /// is followed as faithfully as the dashboard's own switch.
    /// </summary>
    private void FollowAccount()
    {
        var name = Account.Name;
        if (_account == name) return;
        var first = _account is null;
        _account = name;
        if (first) return;

        _retryAfter = DateTimeOffset.MinValue;
        _renewAfter = DateTimeOffset.MinValue;
        _renewBlocked = default;
        _renewRefusals = 0;
        _renewOverrideSpent = false;
        _rejected = false;
        _signInMessage = null;
        lock (_gate) _snapshot = UsageSnapshot.Empty;
    }

    /// <summary>
    /// Where Claude Code keeps the active account's OAuth token on Windows and Linux (macOS uses the
    /// keychain).
    /// </summary>
    private string CredentialsPath => Path.Combine(ClaudeAccounts.DirOf(Account), ".credentials.json");

    /// <summary>Points a <c>claude</c> we start ourselves at the active account, as the agent runs are.</summary>
    private static void RunAs(ProcessStartInfo psi, ClaudeAccount account)
    {
        if (account.ConfigDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = account.ConfigDir;
    }

    /// <param name="force">
    /// Ignore every backoff and try now. For the Settings panel's own button only — the timed probe
    /// must keep its backoffs or it becomes the reason the endpoint is refusing us.
    /// </param>
    public async Task RefreshAsync(CancellationToken ct = default, bool force = false)
    {
        // This meter measures ONE subscription. Pointed at another provider it would report a number
        // that has nothing to do with what the fleet is actually spending, which is worse than
        // reporting nothing: the chip is there to be trusted at a glance. There is no cross-provider
        // equivalent to fall back to, so the honest answer is to say so and let the UI hide it.
        if (_providers.CurrentKind != AgentProviderKind.Claude)
        {
            Degrade("unsupported", "Rate-limit headroom is only reported for Claude Code.");
            return;
        }

        FollowAccount();

        // A person clicking "test" has new information we do not - usually a credential they just
        // changed - so their click outranks a backoff that was set before it existed.
        if (force)
        {
            _retryAfter = DateTimeOffset.MinValue;
            _renewAfter = DateTimeOffset.MinValue;
            _renewRefusals = 0;
            _renewOverrideSpent = false;
        }

        if (DateTimeOffset.UtcNow < _retryAfter) return;

        var (credentials, credentialError) = ReadCredentials();
        if (credentialError is not null || credentials is null)
        {
            _signInMessage = CliSignIn;
            Degrade("no-credentials", credentialError ?? "No Claude Code login found.");
            return;
        }

        // Only a login that CAME with a refresh token can be renewed. A configured one cannot, and
        // routing it through here on a rejection would answer every other poll with "renewal failed"
        // - an explanation of the wrong problem, for a token the user can only fix by replacing it.
        var renewable = credentials.RefreshToken is not null;
        if (renewable && (IsSpent(credentials.ExpiresAt) || _rejected))
        {
            // Renewed here rather than by letting the call 401, so a real dead end can say what to do.
            _rejected = false;
            var (renewed, status, renewError) = await RenewAsync(credentials, ct);
            if (renewed is null)
            {
                if (status == "expired") _signInMessage = CliSignIn;
                Degrade(status!, renewError!);
                return;
            }
            credentials = renewed;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        // Filtered on OUR token, not on the exception type: HttpClient reports its own timeout as a
        // TaskCanceledException, and letting that through as "cancelled" carried it out of the probe
        // and stopped the host - taking the heartbeat and every worker with it.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Degrade("error", "Could not reach the usage API.");
            _log.LogDebug(ex, "Usage poll failed");
            return;
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                _retryAfter = DateTimeOffset.UtcNow + wait;
                Degrade("rate-limited", $"Usage API is rate-limiting; retrying in {(int)wait.TotalMinutes}m.");
                return;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The recorded expiry said this token was good and the API disagrees, so the clock on
                // the file is not to be trusted this time round. Renew on the next poll regardless of
                // what it claims - the backoff inside the renewal is what stops that becoming a loop.
                _rejected = true;
                Degrade("expired", "The dashboard's Claude login was rejected - sign in with `claude`.");
                return;
            }
            if (res.StatusCode == HttpStatusCode.Forbidden)
            {
                // A real token that is not allowed here. This is what a `claude setup-token` token
                // gets: it authenticates and it runs inference, but this endpoint is not in what it
                // may read, so no amount of re-pasting will change the answer. Worth its own message
                // because "403" reads as a bug and the fix is a different KIND of credential.
                //
                // Backed off hard for the same reason. Nothing about a refusal of this shape changes
                // in a minute, and a poll that cannot succeed until the user swaps the credential is
                // the surest way to get the endpoint to start rate-limiting the polls that could.
                _retryAfter = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
                Degrade("forbidden",
                    "This token cannot read usage - the meter needs Claude Code's own login, not a setup-token one.");
                return;
            }
            if (!res.IsSuccessStatusCode)
            {
                Degrade("error", $"Usage API returned {(int)res.StatusCode}.");
                return;
            }

            var body = await res.Content.ReadAsStringAsync(ct);
            try
            {
                var limits = ParseLimits(body);
                // Whoever repaired it - us, the CLI, or the user at a terminal - the banner comes down.
                _signInMessage = null;
                lock (_gate)
                {
                    _snapshot = new UsageSnapshot("ok", null, DateTimeOffset.UtcNow, limits);
                }
            }
            catch (Exception ex)
            {
                Degrade("error", "Usage API returned something unreadable.");
                _log.LogDebug(ex, "Usage response did not parse");
            }
        }
    }

    /// <summary>
    /// Runs a throwaway <c>claude</c> and then polls again.
    ///
    /// <para>
    /// The credential this meter needs can only be renewed by the CLI. Our own refresh grant is
    /// refused, and a <c>setup-token</c> credential is refused by the usage endpoint, so the one
    /// thing that reliably puts a live token back on disk is a <c>claude</c> process running — which
    /// normally happens constantly here, and stops happening exactly when the fleet goes quiet.
    /// </para>
    ///
    /// <para>
    /// Deliberately manual, on a button, rather than on the poll timer: this spends a little of the
    /// very budget it is measuring, and a meter that quietly bills you to read itself is worse than
    /// one that occasionally asks. The prompt is the smallest thing that still makes the CLI
    /// authenticate — cheapest model, no tools, no session kept.
    /// </para>
    /// </summary>
    public async Task<bool> NudgeAsync(CancellationToken ct = default)
    {
        if (_providers.CurrentKind != AgentProviderKind.Claude) return false;

        var psi = new ProcessStartInfo
        {
            FileName = CliPath.Resolve("claude"),
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        RunAs(psi, Account);
        foreach (var arg in new[]
                 {
                     "-p", "ok",
                     "--model", "haiku",
                     "--tools", "",
                     "--no-session-persistence",
                 })
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Bounded so a CLI sitting on an interactive prompt cannot wedge the request. Output is
            // drained rather than read: we want the side effect on the credential file, not the text.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(90));
            _ = proc.StandardOutput.ReadToEndAsync(limit.Token);
            _ = proc.StandardError.ReadToEndAsync(limit.Token);
            await proc.WaitForExitAsync(limit.Token);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Usage nudge could not run the CLI");
            return false;
        }

        await RefreshAsync(ct, force: true);
        return Current.Status == "ok";
    }

    /// <summary>What to say about the CLI's login, and whether the dashboard can offer to fix it.</summary>
    public record SignInState(bool NeedsSignIn, string? Message, bool CanLaunch);

    /// <summary>
    /// The banner's answer. Reports only the state a person has to clear themselves, so a healthy
    /// dashboard and one whose renewal is merely rate-limited both say nothing at all.
    /// </summary>
    public SignInState InspectSignIn() => new(
        _providers.CurrentKind == AgentProviderKind.Claude && _signInMessage is not null,
        _signInMessage,
        OperatingSystem.IsWindows());

    /// <summary>
    /// Make sure the token on disk will still be alive when a CLI process picks it up, renewing it
    /// first if it will not. Null when the login is usable; a reason when it is not.
    ///
    /// <para>
    /// Called by everything that shells out to the agent, because a CLI handed a spent token does
    /// not fail fast: it retries the 401 for about three minutes and then exits 1 with nothing on
    /// stderr. The common case - a token that aged out while the fleet was asleep - needs no human
    /// at all, and this is where that repair happens instead of on the user's next click.
    /// </para>
    ///
    /// <para>
    /// Ignores the renewal backoff on purpose. That backoff keeps a 60-second timer from hammering
    /// the token endpoint; someone who just asked for a run is information the timer did not have,
    /// and making them sit out a 40-minute wait set by a background poll is the tail wagging the dog.
    /// </para>
    /// </summary>
    public async Task<string?> EnsureSignedInAsync(CancellationToken ct = default)
    {
        // Another provider's runs do not read this credential, so there is nothing here to promise.
        if (_providers.CurrentKind != AgentProviderKind.Claude) return null;

        // One renewal at a time. Every caller here is a run about to start, and runs arrive in
        // queues: without this, ten dequeued tickets are ten simultaneous grants against an endpoint
        // that rate-limits by caller, which is the queue earning the 429 that then blocks the queue.
        await _ensureGate.WaitAsync(ct);
        try
        {
            FollowAccount();

            // Read inside the gate rather than before it. Whoever held it may have just renewed, in
            // which case the file already holds a live token and there is nothing left to do.
            var (credentials, error) = ReadCredentials();
            if (credentials is null || error is not null)
            {
                _signInMessage = CliSignIn;
                return CliSignIn;
            }

            // A token of ours, from `claude setup-token`: no expiry to read and no grant to redeem, so
            // there is nothing to check here and nothing we could fix.
            if (credentials.RefreshToken is null) return null;
            if (!IsSpent(credentials.ExpiresAt) && !_rejected) return null;

            // Someone asking for a run is information the timer did not have, so it outranks a
            // backoff the timer set - but that is true once, not once per ticket. The second item in
            // a queue is not a second opinion, and letting each one clear the wait turns a backoff
            // into a burst. The refusal count is left alone for the same reason: zeroing it pinned
            // the wait at five minutes forever, so the doubling that exists here never happened.
            if (!_renewOverrideSpent)
            {
                _renewOverrideSpent = true;
                _renewAfter = DateTimeOffset.MinValue;
            }
            _rejected = false;

            var (renewed, status, renewError) = await RenewAsync(credentials, ct);
            if (renewed is not null) return null;
            // A rate-limited or dropped renewal is ours to retry and says nothing about the login, so it
            // reports itself and leaves the banner alone. Only a refusal means a person has to act.
            if (status != "expired") return renewError ?? "Could not renew Claude Code's login.";
            _signInMessage = CliSignIn;
            return CliSignIn;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>
    /// Opens a terminal running <c>claude auth login</c> on the machine the dashboard runs on.
    ///
    /// <para>
    /// The last resort, for the one failure nothing above can repair: a refresh grant the token
    /// endpoint will no longer redeem. Signing in is an interactive OAuth flow the CLI drives
    /// itself, so this deliberately does not try to run it headless and scrape it - it hands over a
    /// real console and gets out of the way. That the dashboard is a localhost app on the user's own
    /// desktop is the only reason this is possible; see <see cref="SignInState.CanLaunch"/>.
    /// </para>
    ///
    /// <para>
    /// The window stays open after the flow ends (<c>cmd /k</c>), so a sign-in that failed still has
    /// its reason on screen instead of closing over it.
    /// </para>
    ///
    /// <para>
    /// Signs in to the account in force unless <paramref name="accountName"/> names another - which
    /// is how a second account gets its login in the first place, before anything is switched to it.
    /// Sign in there with that account's own credentials: the terminal cannot know which one is meant.
    /// </para>
    /// </summary>
    public (bool Ok, string Message) LaunchSignIn(string? accountName = null)
    {
        var account = Account;
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            if (ClaudeAccounts.Find(Accounts, accountName) is not { } named)
                return (false, $"No Claude account called '{accountName.Trim()}' - save it in Settings first.");
            account = named;
        }

        if (!OperatingSystem.IsWindows())
            return (false, "Run `claude auth login` in a terminal on the dashboard's machine.");

        try
        {
            // The CLI makes its own default directory and expects to be handed any other.
            if (account.ConfigDir is not null) Directory.CreateDirectory(account.ConfigDir);

            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = false };
            RunAs(psi, account);
            foreach (var arg in new[] { "/c", "start", $"Claude sign-in - {account.Name}", "cmd.exe", "/k", "claude", "auth", "login" })
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            return (true, $"A sign-in terminal for the {account.Name} account has opened on the dashboard's machine - finish there.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a sign-in terminal");
            return (false, "Could not open a terminal. Run `claude auth login` by hand.");
        }
    }

    /// <summary>
    /// Records a failure without throwing away the last good numbers. A 60-second poll across a
    /// laptop's sleeps and wifi drops fails routinely, and a window that is 69% full is still roughly
    /// 69% full a minute later — far better information than an empty chip.
    /// </summary>
    private void Degrade(string status, string message)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { Status = status, Message = message };
        }
    }

    /// <summary>
    /// A login as it was found. Carried by value between the few methods that need it and never
    /// stored on the instance, so the only lasting copies are the two files it can come from.
    /// </summary>
    private record Credentials(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Scopes);

    /// <summary>
    /// True once a token is close enough to its expiry to be worth replacing. An absent expiry means
    /// a token we were handed rather than one we read, and those we take at face value.
    /// </summary>
    private static bool IsSpent(DateTimeOffset? expiresAt) =>
        expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow + ExpirySkew;

    /// <summary>
    /// Where the dashboard's own token lives, for operators who would rather not have this depend on
    /// the CLI at all. Global rather than per-project: it is one subscription's meter.
    /// </summary>
    private string? ConfiguredToken() =>
        _registry.GetGlobalConfig().TryGetValue("claudeOauthToken", out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

    /// <summary>
    /// Finds a token, in the order of how much we can rely on it lasting.
    ///
    /// <para>
    /// A token of our own — from <c>claude setup-token</c>, set in the environment or in the
    /// dashboard's config — comes first, and has no expiry and no refresh token by design. It is the
    /// only source here that cannot be taken away by the CLI changing its mind about where it keeps
    /// things, which it has: the credential file below is a fallback the CLI writes when it feels
    /// like it, and a dashboard whose meter dies whenever that file goes quiet is not a meter.
    /// </para>
    /// </summary>
    private (Credentials? Credentials, string? Error) ReadCredentials()
    {
        // Only for the default account. A token of ours is one subscription's, and answering for a
        // second account with it would put the first account's numbers under the second one's name.
        if (Account.ConfigDir is null)
        {
            var ours = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
            if (string.IsNullOrWhiteSpace(ours)) ours = ConfiguredToken();
            if (!string.IsNullOrWhiteSpace(ours))
                return (new Credentials(ours, null, null, null), null);
        }

        var path = CredentialsPath;
        if (!File.Exists(path))
            // A second account has no token to paste: the fix is to sign the CLI in to its folder.
            return (null, Account.ConfigDir is null
                ? NoLogin
                : $"The {Account.Name} account has not been signed in - use Sign in beside it in Settings.");

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?["claudeAiOauth"];
            var token = root?["accessToken"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token))
                return (null, "Claude Code login found but it has no access token.");

            DateTimeOffset? expires = null;
            var expiresNode = root?["expiresAt"];
            if (expiresNode is not null && long.TryParse(expiresNode.ToString(), out var ms) && ms > 0)
                expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            var refresh = root?["refreshToken"]?.GetValue<string>();
            // Carried through to the renewal: the grant asks for the scopes it already holds, and
            // asking for a different set is how you get a token that cannot read what you wanted.
            var scopes = root?["scopes"] switch
            {
                JsonArray many => string.Join(' ', many.Select(s => s?.GetValue<string>()).Where(s => s is not null)),
                JsonValue one => one.GetValue<string>(),
                _ => null,
            };

            return (new Credentials(
                token,
                string.IsNullOrWhiteSpace(refresh) ? null : refresh,
                expires,
                string.IsNullOrWhiteSpace(scopes) ? null : scopes), null);
        }
        catch (Exception)
        {
            return (null, "Claude Code's credential file could not be read.");
        }
    }

    /// <summary>
    /// Trades a spent access token for a fresh one.
    ///
    /// <para>
    /// The CLI only does this when a <c>claude</c> process happens to run, which is why the meter
    /// goes dark overnight. Doing it here keeps the number alive, at the cost of being a second writer
    /// to a file the CLI owns — so every exit from this method leaves that file holding a login that
    /// works, whether the winner was ours or theirs.
    /// </para>
    /// </summary>
    private async Task<(Credentials? Credentials, string? Status, string? Error)> RenewAsync(
        Credentials current, CancellationToken ct)
    {
        if (current.RefreshToken is null) return (null, "expired", SignIn);

        // Still inside a backoff. Re-read the file first anyway: the user may well have fixed this by
        // running the CLI, and that costs a file read rather than a request to the endpoint that is
        // already refusing us. Failing that, repeat the reason we backed off - a rate-limited renewal
        // is not a signed-out one, and telling someone to sign in over it sends them to fix a
        // credential that works.
        if (DateTimeOffset.UtcNow < _renewAfter)
            return (FromDisk(current.RefreshToken), _renewBlocked.Status, _renewBlocked.Message);

        var payload = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = ClientId,
        };
        if (current.Scopes is not null) payload["scope"] = current.Scopes;

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        JsonNode? granted;
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                // The body names which half was refused - the grant or the client - and neither is a
                // secret. Without it a rejection is indistinguishable from a wrong request.
                _log.LogDebug("Token refresh rejected with {Status}: {Body}",
                    (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));

                // This endpoint rate-limits by caller, not by grant, so a 429 says nothing at all
                // about the login. Telling the user to sign in again over one would send them to fix
                // something that is not broken - and signing in is itself a request to the same place.
                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = Backoff(res.Headers.RetryAfter?.Delta);
                    return Block(FromDisk(current.RefreshToken), RenewBlocked,
                        $"Renewing Claude's login is rate-limited; retrying in {Minutes(wait)}.");
                }

                // Either the grant is dead or the CLI already spent it on a rotation of its own. Both
                // are answered by the file: if a working token is sitting there, take it and say nothing.
                Backoff(TimeSpan.FromMinutes(15));
                return Block(FromDisk(current.RefreshToken), "expired", SignIn);
            }
            granted = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A dropped request is not a dead login, so no backoff and no telling the user to sign in
            // - the next poll simply tries again.
            _log.LogDebug(ex, "Token refresh could not be sent");
            return (null, RenewBlocked, "Could not renew Claude Code's login.");
        }

        var access = granted?["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(access)) return Block(null, "expired", SignIn);

        var seconds = granted?["expires_in"]?.GetValue<long>() ?? 0;
        var rotated = granted?["refresh_token"]?.GetValue<string>();
        var renewed = new Credentials(
            access,
            string.IsNullOrWhiteSpace(rotated) ? current.RefreshToken : rotated,
            seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null,
            current.Scopes);

        _renewRefusals = 0;
        _signInMessage = null;
        return (Persist(renewed, spent: current.RefreshToken), null, null);
    }

    /// <summary>
    /// Holds off the next renewal, doubling the wait each time we are refused in a row. Something the
    /// endpoint refuses at nine will still be refused at five past, and a minute-by-minute retry of a
    /// credential problem the user has to fix by hand is just noise aimed at someone else's server.
    /// </summary>
    private TimeSpan Backoff(TimeSpan? requested)
    {
        var doubled = TimeSpan.FromMinutes(5 * Math.Pow(2, Math.Min(_renewRefusals, 4)));
        var wait = requested is not null && requested > doubled ? requested.Value : doubled;
        if (wait > MaxRenewBackoff) wait = MaxRenewBackoff;

        _renewRefusals++;
        _renewAfter = DateTimeOffset.UtcNow + wait;
        // A fresh wait is a fresh allowance: the next person to ask for a run may skip it once.
        _renewOverrideSpent = false;
        return wait;
    }

    /// <summary>
    /// Records why renewal is on hold, so that every poll inside the wait repeats this answer instead
    /// of inventing one, and hands the same answer back to this caller.
    /// </summary>
    private (Credentials?, string, string) Block(Credentials? fallback, string status, string message)
    {
        _renewBlocked = (status, message);
        return (fallback, status, message);
    }

    private static string Minutes(TimeSpan wait) =>
        wait < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)wait.TotalMinutes)}m" : $"{(int)wait.TotalHours}h";

    /// <summary>
    /// Re-reads the file in the hope that someone else renewed while we were failing to. Only counts
    /// if what is there is both usable and actually newer than what we started with.
    /// </summary>
    private Credentials? FromDisk(string spentRefreshToken)
    {
        var (disk, error) = ReadCredentials();
        if (error is not null || disk is null) return null;
        if (disk.RefreshToken == spentRefreshToken || IsSpent(disk.ExpiresAt)) return null;
        return disk;
    }

    /// <summary>
    /// Writes the renewed login back where the CLI will find it, but only if the CLI has not moved
    /// first. <paramref name="spent"/> is the refresh token the grant was made with: if the file no
    /// longer holds it, another process rotated the credential while our request was in flight, and
    /// its newer pair is the one the CLI will use. Overwriting that would log the user out of a
    /// working session to install a token only this dashboard knows about, so we yield and take theirs.
    /// </summary>
    private Credentials Persist(Credentials renewed, string spent)
    {
        var path = CredentialsPath;
        try
        {
            var merged = MergeCredentials(File.ReadAllText(path), renewed.AccessToken,
                renewed.RefreshToken, renewed.ExpiresAt, spent);

            if (merged is null)
            {
                var (disk, error) = ReadCredentials();
                return error is null && disk is not null && !IsSpent(disk.ExpiresAt) ? disk : renewed;
            }

            WriteAtomically(path, merged);
        }
        catch (Exception ex)
        {
            // The token is good even if the file is not; this poll succeeds and the next one renews
            // again. Worth a line in the log because it means every poll is now buying a new token.
            _log.LogWarning(ex, "Renewed Claude Code's token but could not write it back to {Path}", path);
        }

        return renewed;
    }

    /// <summary>
    /// Folds a renewed login into the credential file's own JSON, returning the text to write - or
    /// <c>null</c> to mean "leave the file alone", either because <paramref name="spent"/> is no
    /// longer the refresh token on disk or because the file is not shaped the way we expect.
    ///
    /// <para>
    /// Field-by-field rather than a fresh object on purpose: the file also carries the subscription
    /// tier, the granted scopes, and a sibling block of MCP logins that are none of our business. The
    /// CLI reads all of it and we know the meaning of three keys.
    /// </para>
    /// </summary>
    public static string? MergeCredentials(
        string fileText, string accessToken, string? refreshToken, DateTimeOffset? expiresAt, string spent)
    {
        var root = JsonNode.Parse(fileText);
        var oauth = root?["claudeAiOauth"];
        if (oauth is null) return null;
        if (oauth["refreshToken"]?.GetValue<string>() != spent) return null;

        oauth["accessToken"] = accessToken;
        oauth["refreshToken"] = refreshToken;
        if (expiresAt is not null)
            oauth["expiresAt"] = expiresAt.Value.ToUnixTimeMilliseconds();

        return root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Replaces the file in one step. A half-written credential file is a logout for the CLI as well
    /// as for us, and this one is read by a process we do not control at a moment we cannot predict.
    /// </summary>
    private static void WriteAtomically(string path, string contents)
    {
        var temp = path + ".whetstone.tmp";
        File.WriteAllText(temp, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the normalised <c>limits[]</c> array the API returns. Preferred over the older
    /// <c>five_hour</c> / <c>seven_day_*</c> keys because those sit alongside a long tail of
    /// codenamed buckets that come and go; <c>limits[]</c> is already the "what to show a user" view,
    /// with a percent, a severity and a reset on each entry.
    /// </summary>
    private static List<UsageLimit> ParseLimits(string body)
    {
        var limits = new List<UsageLimit>();
        var arr = JsonNode.Parse(body)?["limits"]?.AsArray();
        if (arr is null) return limits;

        foreach (var node in arr)
        {
            if (node is null) continue;
            var kind = node["kind"]?.GetValue<string>() ?? "";
            if (kind.Length == 0) continue;

            limits.Add(new UsageLimit(
                Kind: kind,
                Group: node["group"]?.GetValue<string>() ?? kind,
                Label: LabelFor(kind, node["scope"]),
                Percent: node["percent"]?.GetValue<double>() ?? 0,
                Severity: node["severity"]?.GetValue<string>() ?? "normal",
                ResetsAt: Reset(node["resets_at"]),
                IsActive: node["is_active"]?.GetValue<bool>() ?? false));
        }

        // Session first, then the weekly windows - the order the user cares about them in.
        return limits
            .OrderBy(l => l.Group == "session" ? 0 : 1)
            .ThenByDescending(l => l.Percent)
            .ToList();
    }

    private static DateTimeOffset? Reset(JsonNode? node)
    {
        var raw = node?.GetValue<string>();
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// A short human name per window. Scoped weekly limits carry the model they apply to, which is
    /// the only thing that distinguishes two otherwise identical "weekly" rows.
    /// </summary>
    private static string LabelFor(string kind, JsonNode? scope)
    {
        var model = scope?["model"]?["display_name"]?.GetValue<string>();
        return kind switch
        {
            "session" => "Session",
            "weekly_all" => "Weekly",
            "weekly_scoped" => string.IsNullOrWhiteSpace(model) ? "Weekly (scoped)" : $"Weekly · {model}",
            _ => kind.Replace('_', ' '),
        };
    }
}
