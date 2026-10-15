using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// An isolated QA copy of the dashboard: one started from a worktree, on ports of its own, over a
/// throwaway data root, so a ticket on the dashboard itself can be proven against a running app.
/// <para>
/// The live instance and the copy are the same program on the same machine, sharing one user
/// profile, one Claude login, one docker daemon and one set of notification channels. A copy that
/// is merely pointed at an empty folder is still able to start a heartbeat that launches real
/// agents, renew the login the live fleet is spending, post to the owner's phone and pull issues
/// from a tracker. So the copy is told what it is, by <see cref="EnvVar"/>, and refuses everything
/// that would reach beyond its own folder: the heartbeat and the steward, every agent run, the
/// tracker, notifications, the login, docker stacks and anything that pushes.
/// </para>
/// <para>
/// Refused in three layers, because no one of them reaches everything. The route table stops the
/// request at the door with a sentence that says why; the services refuse again in case a route
/// was missed or a new caller appears; and every project the copy may see has to live inside its
/// own data root, so the git work it does allow can only ever touch throwaway repositories.
/// </para>
/// </summary>
public sealed class QaIsolation
{
    /// <summary>Set to <c>1</c> (or <c>true</c>) on the process that is a QA copy.</summary>
    public const string EnvVar = "WHETSTONE_QA_ISOLATION";

    /// <summary>The live instance's port. A copy never listens on it.</summary>
    public const int LivePort = 5338;

    public static readonly QaIsolation Off = new(false);

    public bool On { get; }

    public QaIsolation(bool on) => On = on;

    public static QaIsolation FromEnvironment() => new(IsTruthy(Environment.GetEnvironmentVariable(EnvVar)));

    private static bool IsTruthy(string? v) =>
        v is not null && (v.Trim() == "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The sentence a refusal carries, for a person or an agent reading the response.</summary>
    public static string Refusal(string what) =>
        $"Refused: this is an isolated QA copy ({EnvVar}), and {what} would reach beyond it. " +
        "It runs only on the live instance.";

    // ---- The route table ----------------------------------------------------

    private sealed record Rule(string Method, Regex Path, string What);

    private static Rule R(string method, string pattern, string what) =>
        new(method, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), what);

    /// <summary>
    /// Every route a QA copy refuses. A method of <c>*</c> refuses the path whatever it is asked with.
    /// Reading the board, editing tickets, local git on the copy's own repositories and the worker
    /// MCP door all stay open: those are what a QA run is there to exercise.
    /// </summary>
    private static readonly Rule[] Rules =
    {
        R("POST", @"^/api/heartbeat/(start|stop|stop-after-cycle|cancel-stop|wake)/?$",
            "starting, stopping or signalling the heartbeat"),
        R("*", @"^/api/steward/(due|launching|abandon)/?$", "the steward's run loop"),
        R("*", @"^/mcp/steward(/|$)", "a steward session"),
        R("POST", @"^/api/notifications/test/?$", "sending a notification"),
        R("POST", @"^/api/improvements/apply/?$", "launching an agent to apply improvements"),
        R("POST", @"^/api/classroom/tickets/\d+/generate/?$", "launching a Classroom analysis agent"),
        R("POST", @"^/api/classroom/analyses/\d+/walkthrough/?$", "launching a walkthrough agent"),
        R("POST", @"^/api/unit-reports/\d+/walkthrough/?$", "launching a walkthrough agent"),
        R("POST", @"^/api/steward/plan/units/\d+/report/?$", "launching a unit report agent"),
        R("POST", @"^/api/tickets/\d+/git/(force-push|pr)/?$", "pushing a branch or raising a pull request"),
        R("*", @"^/api/tickets/\d+/git/checkin(/|$)", "a TFVC check-in"),
        R("*", @"^/api/projects/[^/]+/tfvc/sync/?$", "syncing with TFVC"),
        R("POST", @"^/api/projects/[^/]+/stack/(start|stop)/?$", "starting or stopping a docker stack"),
        R("POST", @"^/api/projects/[^/]+/dependencies/refresh/?$", "reading the issue tracker"),
        R("POST", @"^/api/usage/(check|nudge|signin|account)/?$", "reading, renewing or switching the Claude login"),
        R("POST", @"^/api/system/pick-folder/?$", "opening a dialog on the desktop"),
    };

    /// <summary>What a request would do that a QA copy refuses, or null when the copy may serve it.</summary>
    public static string? RefusedRoute(string method, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var rule in Rules)
        {
            if (rule.Method != "*" && !rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Path.IsMatch(path)) return rule.What;
        }
        return null;
    }

    // ---- Starting at all ----------------------------------------------------

    /// <summary>
    /// Why this process may not start as a QA copy, or null when it may.
    /// <para>
    /// A copy has to be told where its data lives, in so many words: left to find it, it walks up to
    /// the nearest checkout's <c>data/</c>, and from the main checkout that is the live board. And it
    /// has to be told its port: the launch profile's is the live one.
    /// </para>
    /// </summary>
    /// <param name="dataRootOverride">DASHBOARD_DATA_ROOT as set, or null.</param>
    /// <param name="urls">The addresses the host was told to listen on.</param>
    public static string? StartupProblem(string? dataRootOverride, string? urls)
    {
        if (string.IsNullOrWhiteSpace(dataRootOverride))
            return $"{EnvVar} is set but DASHBOARD_DATA_ROOT is not. A QA copy runs over a throwaway data folder, named explicitly.";

        var root = Path.GetFullPath(dataRootOverride.Trim());
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj")))
                return $"DASHBOARD_DATA_ROOT ({root}) is inside a checkout ({dir.FullName}). A QA copy's data lives in a throwaway folder outside every checkout.";
        }

        if (string.IsNullOrWhiteSpace(urls))
            return "A QA copy must be given its address (--urls or ASPNETCORE_URLS); the default is the live instance's.";
        if (Regex.IsMatch(urls, $@":{LivePort}(?!\d)"))
            return $"A QA copy may not listen on {LivePort}, which is the live instance's port ({urls}).";
        return null;
    }

    // ---- What a project may point at ---------------------------------------

    /// <summary>
    /// Why this project reaches outside the copy's data root, or null when everything it names is
    /// inside it. A QA copy only ever sees projects that are wholly its own - placeholder repos made
    /// for it - so the reaper, a merge or a checkout in the copy cannot land on a real repository.
    /// </summary>
    public static string? Escapes(ProjectConfig cfg, string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        bool Inside(string path) =>
            (Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
                .StartsWith(root, StringComparison.OrdinalIgnoreCase);

        string? Check(string label, string? path) =>
            string.IsNullOrWhiteSpace(path) || Inside(path) ? null : $"its {label} ({path}) is outside {root}";

        if (string.IsNullOrWhiteSpace(cfg.ProjectPath)) return null;
        return Check("projectPath", cfg.ProjectPath)
            ?? Check("worktree root", cfg.ResolvedWorktreeRoot)
            ?? Check("notes store", cfg.ResolvedMemoryDir)
            ?? (cfg.Stack.IsConfigured ? Check("stack working directory", cfg.Stack.ResolvedWorkingDir(cfg.ProjectPath)) : null);
    }
}

/// <summary>
/// The provider a QA copy hands every agent run: one that will not build a command. A backstop
/// behind the route table, so a caller nobody listed still cannot launch an agent from a copy.
/// </summary>
public sealed class RefusedAgentProvider : AgentProvider
{
    public override AgentProviderKind Kind => AgentProviderKind.Claude;
    public override string Executable => "none";
    public override string DisplayName => "none (isolated QA copy)";

    private static InvalidOperationException No() => new(QaIsolation.Refusal("launching an agent"));

    public override AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir) => throw No();
    public override AgentRun BuildFreeformRun(string workDir, string scratchDir) => throw No();
    public override AgentOutcome ReadAnalysis(AgentRun run, string stdout) => throw No();
}
