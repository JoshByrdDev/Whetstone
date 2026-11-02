using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;

namespace Whetstone.Services;

/// <summary>
/// The Metrics page's analytics: the marts dbt builds over DuckDB (docs/analytics.md), read here,
/// and the refresh that rebuilds them.
/// <para>
/// A reader, deliberately. Every number is computed in dbt under test and published as parquet,
/// pre-cut by window, project and track, so this class only ever filters and sorts - it never
/// aggregates. And it never holds the build database: a parquet file is opened for one query and
/// let go, which is what lets a refresh swap new marts in while the page is being read.
/// </para>
/// </summary>
public sealed class AnalyticsService
{
    /// <summary>The marts refresh.py publishes. A name not here is refused, so a route can take one as a parameter.</summary>
    public static readonly IReadOnlyList<string> Marts =
    [
        "fct_runs", "fct_tool_calls", "fct_tickets",
        "mart_phase_breakdown", "mart_time_split", "mart_rework", "mart_tool_time",
        "mart_start_rebase", "mart_diff_size", "mart_first_pass_qa", "mart_version_trend", "mart_daily",
        "fct_bug_links", "mart_bugs", "fct_experiment_tickets", "mart_experiment_arms", "mart_experiments",
    ];

    /// <summary>Marts older than this are refreshed when someone opens the page.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>The windows the marts are cut by. 0 is all history.</summary>
    public static readonly IReadOnlyList<int> Windows = [7, 30, 90, 0];

    /// <summary>
    /// What a run list can be filtered by, as the page names it, and the fct_runs column it means.
    /// Allowlisted, because the name arrives in a query string and becomes part of the SQL.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RunFilters = new Dictionary<string, string>
    {
        ["phase"] = "phase", ["provider"] = "provider", ["model"] = "model", ["effort"] = "effort",
        ["skill_version"] = "skill_version", ["prompt_version"] = "prompt_version",
        ["entry_reason"] = "entry_reason", ["rework_class"] = "rework_class", ["pass"] = "pass_bucket",
        ["cli_version"] = "cli_version", ["experiment_arm"] = "experiment_arm", ["size_bucket"] = "size_bucket",
        ["start_rebase"] = "start_rebase", ["outcome"] = "outcome",
    };

    private readonly DashboardPaths _paths;
    private readonly QaIsolation _isolation;
    private readonly ILogger<AnalyticsService> _log;
    private readonly Func<string, string?> _findOnPath;
    private readonly object _gate = new();
    private Process? _refresh;

    public AnalyticsService(DashboardPaths paths, QaIsolation isolation, ILogger<AnalyticsService> log)
        : this(paths, isolation, log, name => SetupCheckService.FindOnPath(name, Environment.GetEnvironmentVariable("PATH")))
    { }

    public AnalyticsService(DashboardPaths paths, QaIsolation isolation, ILogger<AnalyticsService> log,
        Func<string, string?> findOnPath) =>
        (_paths, _isolation, _log, _findOnPath) = (paths, isolation, log, findOnPath);

    public string OutDir => Path.Combine(_paths.DataRoot, "analytics");
    public string MartsDir => Path.Combine(OutDir, "marts");
    private string StatusFile => Path.Combine(OutDir, "refresh.json");
    private string LockFile => Path.Combine(OutDir, "refresh.lock");
    private string LogFile => Path.Combine(OutDir, "refresh.log");

    /// <summary>
    /// The dbt project: <c>analytics/</c> in the checkout the app runs from. Found by walking up to
    /// Whetstone.csproj, as DashboardPaths finds the data root - from a Release build's bin folder
    /// that is the checkout itself.
    /// </summary>
    public static string? ProjectDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj"))) continue;
                var analytics = Path.Combine(dir.FullName, "analytics");
                return File.Exists(Path.Combine(analytics, "refresh.py")) ? analytics : null;
            }
        }
        return null;
    }

    // ---- status and refresh ---------------------------------------------------------

    /// <param name="Available">Whether a refresh can run here at all; <paramref name="Unavailable"/> says why not.</param>
    /// <param name="AsOf">When the published marts were built. Null before the first good refresh.</param>
    /// <param name="Transcripts">Whether refreshes here read the worker transcripts (not on a QA copy).</param>
    public record Status(
        bool Available, string? Unavailable, bool Running, bool HasMarts, DateTimeOffset? AsOf, bool Stale,
        bool? LastOk, string? LastError, double? LastSeconds, DateTimeOffset? LastStartedAt,
        bool Transcripts, bool Isolated);

    public Status GetStatus()
    {
        var unavailable = Unavailable();
        var hasMarts = Marts.All(m => File.Exists(MartPath(m)));
        bool? lastOk = null; string? lastError = null; double? seconds = null;
        DateTimeOffset? asOf = null, startedAt = null;
        try
        {
            if (File.Exists(StatusFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
                var root = doc.RootElement;
                lastOk = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                lastError = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
                seconds = root.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
                asOf = ReadTime(root, "asOf");
                startedAt = ReadTime(root, "startedAt");
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            lastError = $"refresh.json could not be read: {e.Message}";
        }

        var stale = !hasMarts || asOf is null || DateTimeOffset.UtcNow - asOf.Value > StaleAfter;
        return new Status(unavailable is null, unavailable, IsRunning(), hasMarts, hasMarts ? asOf : null, stale,
            lastOk, lastError, seconds, startedAt, !_isolation.On, _isolation.On);
    }

    private static DateTimeOffset? ReadTime(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : null;

    /// <summary>Why a refresh cannot run here, or null when it can.</summary>
    public string? Unavailable()
    {
        if (ProjectDir() is null) return "The analytics project (analytics/refresh.py) is not in this checkout.";
        if (_findOnPath("uv") is null) return "uv is not on PATH. Install it (winget install astral-sh.uv) and restart Whetstone.";
        return null;
    }

    /// <summary>A refresh this process started, or one started by hand whose lock is fresh.</summary>
    public bool IsRunning()
    {
        lock (_gate)
        {
            if (_refresh is { HasExited: false }) return true;
        }
        try
        {
            return File.Exists(LockFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(LockFile) < TimeSpan.FromHours(1);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Start a refresh in the background. Returns why not when one is running or none can run.
    /// <para>
    /// On a QA copy the transcripts are left out: they are the live fleet's, under the user profile,
    /// which is outside the copy's folder. Everything else the refresh reads is the copy's own data
    /// root and this checkout's git history.
    /// </para>
    /// </summary>
    public (bool Started, string Message) StartRefresh()
    {
        if (Unavailable() is { } why) return (false, why);
        lock (_gate)
        {
            if (IsRunning()) return (false, "A refresh is already running.");
            Directory.CreateDirectory(OutDir);

            var project = ProjectDir()!;
            var psi = new ProcessStartInfo(_findOnPath("uv")!)
            {
                WorkingDirectory = project,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "run", "--project", project, "python", Path.Combine(project, "refresh.py"),
                                      "--data-root", _paths.DataRoot })
                psi.ArgumentList.Add(a);
            if (_isolation.On) psi.ArgumentList.Add("--no-transcripts");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["DO_NOT_TRACK"] = "1";

            var log = new StreamWriter(LogFile, append: false, Encoding.UTF8) { AutoFlush = true };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            proc.Exited += (_, _) =>
            {
                lock (log) log.Dispose();
                _log.LogInformation("Analytics refresh exited with {Code}", SafeExitCode(proc));
            };
            try { proc.Start(); }
            catch (Exception e)
            {
                log.Dispose();
                return (false, $"Could not start uv: {e.Message}");
            }
            // The refresh dies with the app rather than outliving it holding the lock.
            KillOnCloseJob.Assign(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _refresh = proc;
            return (true, _isolation.On ? "Refreshing (QA copy: without transcripts)." : "Refreshing.");
        }
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    /// <summary>The last refresh's own log, for a failure the page wants to show.</summary>
    public string? LogTail(int lines = 40)
    {
        try
        {
            if (!File.Exists(LogFile)) return null;
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(lines)).TrimEnd();
        }
        catch (IOException) { return null; }
    }

    // ---- reading the marts ------------------------------------------------------------

    public string MartPath(string mart) => Path.Combine(MartsDir, mart + ".parquet");

    /// <summary>One scope of an aggregate mart: a window, a project (or all) and a track (or all).</summary>
    public record Scope(int WindowDays, string Project, string Track)
    {
        public static Scope Of(int? days, string? project, string? track) => new(
            days is { } d && Windows.Contains(d) ? d : 30,
            string.IsNullOrWhiteSpace(project) ? "*" : project.Trim(),
            string.IsNullOrWhiteSpace(track) ? "Delivery" : track.Trim());
    }

    /// <summary>
    /// The rows of one mart in one scope, optionally narrowed by equality on further columns.
    /// Empty when the mart has not been published.
    /// </summary>
    public List<Dictionary<string, object?>> Mart(string mart, Scope scope,
        IReadOnlyDictionary<string, string?>? where = null, string? orderBy = null)
    {
        if (!Marts.Contains(mart)) throw new ArgumentException($"No mart named {mart}.");
        if (where?.Keys.FirstOrDefault(k => !IsIdentifier(k)) is { } bad) throw new ArgumentException($"Not a column: {bad}");
        var file = MartPath(mart);
        if (!File.Exists(file)) return [];

        var clauses = new List<string> { "window_days = $window", "project_scope = $project", "track_scope = $track" };
        var args = new Dictionary<string, object?>
        {
            ["window"] = scope.WindowDays, ["project"] = scope.Project, ["track"] = scope.Track,
        };
        var i = 0;
        foreach (var (col, value) in where ?? new Dictionary<string, string?>())
        {
            if (value is null) continue;
            if (!IsIdentifier(col)) throw new ArgumentException($"Not a column: {col}");
            clauses.Add($"\"{col}\" = $w{i}");
            args[$"w{i}"] = value;
            i++;
        }
        var order = orderBy is null ? "" : $" ORDER BY {orderBy}";
        return Query($"SELECT * EXCLUDE (window_days, project_scope, track_scope) FROM {Parquet(file)} WHERE {string.Join(" AND ", clauses)}{order}", args);
    }

    /// <summary>What the run list may be asked for.</summary>
    public record RunQuery(int? Days, string? Project, string? Track, IReadOnlyDictionary<string, string> Filters,
        string? Sort, int Limit);

    /// <summary>Runs in a window, newest (or slowest) first, for the drill-down list.</summary>
    public List<Dictionary<string, object?>> Runs(RunQuery q)
    {
        var file = MartPath("fct_runs");
        if (!File.Exists(file)) return [];
        var scope = Scope.Of(q.Days, q.Project, q.Track);
        var clauses = new List<string>();
        var args = new Dictionary<string, object?>();
        if (scope.WindowDays > 0)
        {
            clauses.Add("started_at >= $since");
            args["since"] = DateTime.UtcNow.AddDays(-scope.WindowDays);
        }
        if (scope.Project != "*") { clauses.Add("project_key = $project"); args["project"] = scope.Project; }
        if (scope.Track != "*") { clauses.Add("track = $track"); args["track"] = scope.Track; }
        var i = 0;
        foreach (var (name, value) in q.Filters)
        {
            if (!RunFilters.TryGetValue(name, out var col) || string.IsNullOrEmpty(value)) continue;
            // 'unknown' is how the marts name a missing value; the run row holds the NULL itself.
            clauses.Add($"coalesce(cast(\"{col}\" as varchar), 'unknown') = $f{i}");
            args[$"f{i}"] = value;
            i++;
        }
        var order = q.Sort switch
        {
            "slowest" => "minutes DESC",
            "tokens" => "tokens DESC",
            _ => "started_at DESC",
        };
        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
        return Query(
            "SELECT run_id, ticket_id, ticket_title, project_key, phase, model, provider, effort, started_at, outcome, " +
            "phase_after, entry_reason, rework_class, pass, minutes, tokens, num_turns, tool_calls, tool_errors, " +
            "split_source, lines_changed, skill_version, prompt_version " +
            $"FROM {Parquet(file)}{where} ORDER BY {order} LIMIT {Math.Clamp(q.Limit, 1, 500)}", args);
    }

    /// <summary>One run with every column, and its tool calls in order. Null when there is no such run.</summary>
    public (Dictionary<string, object?> Run, List<Dictionary<string, object?>> Calls)? Run(int runId)
    {
        var runs = MartPath("fct_runs");
        if (!File.Exists(runs)) return null;
        var row = Query($"SELECT * FROM {Parquet(runs)} WHERE run_id = $id", new() { ["id"] = runId }).FirstOrDefault();
        if (row is null) return null;
        var callsFile = MartPath("fct_tool_calls");
        var calls = File.Exists(callsFile)
            ? Query($"SELECT seq, tool_name, tool_short, tool_family, tool_detail, input_summary, offset_s, seconds, " +
                    $"is_error, result_chars, is_sidechain FROM {Parquet(callsFile)} WHERE run_id = $id ORDER BY seq",
                    new() { ["id"] = runId })
            : [];
        return (row, calls);
    }

    /// <summary>
    /// The bugs traced in a window, newest first: each cause suggested for each bug, confirmed or not,
    /// with the run that most likely wrote it. The window is on when the bug was filed.
    /// </summary>
    public List<Dictionary<string, object?>> BugLinks(Scope scope, int limit = 60)
    {
        var file = MartPath("fct_bug_links");
        if (!File.Exists(file)) return [];
        var clauses = new List<string>();
        var args = new Dictionary<string, object?>();
        if (scope.WindowDays > 0)
        {
            clauses.Add("bug_created_at >= $since");
            args["since"] = DateTime.UtcNow.AddDays(-scope.WindowDays);
        }
        if (scope.Project != "*") { clauses.Add("project_key = $project"); args["project"] = scope.Project; }
        if (scope.Track != "*") { clauses.Add("track = $track"); args["track"] = scope.Track; }
        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
        return Query(
            "SELECT link_id, bug_ticket_id, bug_title, bug_created_at, project_key, source_ticket_id, source_title, " +
            "status, origin, share, escaped, found_by, missed_by, days_to_find, written_by_run_id, run_tier, " +
            "written_phase, written_model, written_effort, written_skill_version, attributed_fix_minutes " +
            $"FROM {Parquet(file)}{where} ORDER BY bug_created_at DESC, share DESC LIMIT {Math.Clamp(limit, 1, 500)}", args);
    }

    /// <summary>
    /// Every experiment (one project's, or all), newest first, each with its arms. Not cut by a window:
    /// an experiment is read whole, from its first enrolment to its verdict.
    /// </summary>
    public List<Dictionary<string, object?>> Experiments(string? project)
    {
        var heads = MartPath("mart_experiments");
        if (!File.Exists(heads)) return [];
        var args = new Dictionary<string, object?>();
        var where = "";
        if (!string.IsNullOrWhiteSpace(project) && project != "*") { where = " WHERE project_key = $project"; args["project"] = project; }
        var list = Query($"SELECT * FROM {Parquet(heads)}{where} ORDER BY started_at DESC", args);
        var armsFile = MartPath("mart_experiment_arms");
        var arms = File.Exists(armsFile)
            ? Query($"SELECT * FROM {Parquet(armsFile)}{where} ORDER BY experiment_id, arm_key", args)
            : [];
        foreach (var e in list)
            e["arms"] = arms.Where(a => Equals(Convert.ToInt64(a["experimentId"]), Convert.ToInt64(e["experimentId"]))).ToList();
        return list;
    }

    private static bool IsIdentifier(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A parquet file as a table. The path is this app's own, never a caller's; quotes are still escaped.</summary>
    private static string Parquet(string file) => $"read_parquet('{file.Replace('\\', '/').Replace("'", "''")}')";

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object?> args)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.Add(new DuckDBParameter(name, value));
        using var reader = cmd.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(i => Camel(reader.GetName(i))).ToArray();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(names.Length);
            for (var c = 0; c < names.Length; c++)
                row[names[c]] = reader.IsDBNull(c) ? null : Plain(reader.GetValue(c));
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A DuckDB value as something System.Text.Json writes plainly.</summary>
    private static object? Plain(object v) => v switch
    {
        BigInteger b => (double)b,
        decimal d => (double)d,
        double x when double.IsNaN(x) || double.IsInfinity(x) => null,
        float f when float.IsNaN(f) || float.IsInfinity(f) => null,
        DateTime t => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v,
    };

    /// <summary>snake_case to camelCase, so the marts read like every other route's JSON.</summary>
    public static string Camel(string snake)
    {
        var sb = new StringBuilder(snake.Length);
        var upper = false;
        foreach (var ch in snake)
        {
            if (ch == '_') { upper = sb.Length > 0; continue; }
            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }
        return sb.ToString();
    }
}
