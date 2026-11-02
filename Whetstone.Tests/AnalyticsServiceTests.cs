using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Metrics page's reader over the published marts. The marts themselves are tested by dbt
/// (analytics/models/**/_*.yml); these cover what the board does with them: one scope out of the
/// pre-cut rows, the run drill-down, and what it refuses.
/// </summary>
public sealed class AnalyticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-analytics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AnalyticsService Service(bool isolated = false, bool uv = true) =>
        new(new DashboardPaths(_root), new QaIsolation(isolated), NullLogger<AnalyticsService>.Instance,
            name => uv && name == "uv" ? @"C:\bin\uv.exe" : null);

    /// <summary>Write a mart the way refresh.py publishes one: parquet, from SQL.</summary>
    private void Publish(AnalyticsService a, string mart, string select)
    {
        Directory.CreateDirectory(a.MartsDir);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"COPY ({select}) TO '{a.MartPath(mart).Replace('\\', '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_mart_is_read_for_one_scope_with_camel_case_names()
    {
        var a = Service();
        Publish(a, "mart_time_split", """
            SELECT * FROM (VALUES
                (30, '*', 'Delivery', 'Implementing', 10, 120.5::DOUBLE),
                (30, 'alpha', 'Delivery', 'Implementing', 4, 50.0::DOUBLE),
                (7, '*', 'Delivery', 'Implementing', 2, 9.0::DOUBLE),
                (30, '*', '*', 'Implementing', 12, 140.0::DOUBLE))
            AS t(window_days, project_scope, track_scope, phase, runs, total_minutes)
            """);

        var rows = a.Mart("mart_time_split", AnalyticsService.Scope.Of(30, null, null));

        var row = Assert.Single(rows);
        Assert.Equal(10, Convert.ToInt32(row["runs"]));
        Assert.Equal(120.5, Convert.ToDouble(row["totalMinutes"]));
        // The scope columns are what was asked for, so they are not sent back.
        Assert.False(row.ContainsKey("windowDays"));

        var alpha = Assert.Single(a.Mart("mart_time_split", AnalyticsService.Scope.Of(30, "alpha", "Delivery")));
        Assert.Equal(4, Convert.ToInt32(alpha["runs"]));
        Assert.Single(a.Mart("mart_time_split", AnalyticsService.Scope.Of(30, null, "*")));
    }

    [Fact]
    public void A_window_the_marts_are_not_cut_by_falls_back_to_30_days()
    {
        Assert.Equal(30, AnalyticsService.Scope.Of(45, null, null).WindowDays);
        Assert.Equal(0, AnalyticsService.Scope.Of(0, null, null).WindowDays);
        Assert.Equal("Delivery", AnalyticsService.Scope.Of(7, "", "").Track);
    }

    [Fact]
    public void An_unpublished_mart_reads_as_empty_and_an_unknown_one_is_refused()
    {
        var a = Service();
        Assert.Empty(a.Mart("mart_rework", AnalyticsService.Scope.Of(30, null, null)));
        Assert.Throws<ArgumentException>(() => a.Mart("dashboard", AnalyticsService.Scope.Of(30, null, null)));
        Assert.Throws<ArgumentException>(() => a.Mart("mart_rework", AnalyticsService.Scope.Of(30, null, null),
            new Dictionary<string, string?> { ["phase; drop table x"] = "y" }));
    }

    [Fact]
    public void A_run_comes_back_with_its_tool_calls_in_order()
    {
        var a = Service();
        Publish(a, "fct_runs", "SELECT 7 AS run_id, 3 AS ticket_id, 'QaTesting' AS phase, 12.0 AS minutes");
        Publish(a, "fct_tool_calls", """
            SELECT * FROM (VALUES
                (7, 2, 'Read', 'Read', 'files', NULL, 'a.cs', 5.0, 0.4, false, 100, false),
                (7, 1, 'Bash', 'Bash', 'shell', 'dotnet build', 'dotnet build -c Release', 1.0, 40.0, false, 900, false),
                (8, 1, 'Bash', 'Bash', 'shell', 'git status', 'git status', 1.0, 3.0, false, 10, false))
            AS t(run_id, seq, tool_name, tool_short, tool_family, tool_detail, input_summary, offset_s, seconds,
                 is_error, result_chars, is_sidechain)
            """);

        var r = a.Run(7);

        Assert.NotNull(r);
        Assert.Equal("QaTesting", r!.Value.Run["phase"]);
        Assert.Equal(new[] { "dotnet build", null }, r.Value.Calls.Select(c => c["toolDetail"]).ToArray());
        Assert.Null(a.Run(99));
    }

    [Fact]
    public void The_run_list_takes_only_the_filters_it_knows()
    {
        var a = Service();
        Publish(a, "fct_runs", """
            SELECT * FROM (VALUES
                (1, 1, 't', 'alpha', 'Delivery', 'QaTesting', 'qa-failed', NULL, now()::TIMESTAMP, 5.0),
                (2, 1, 't', 'alpha', 'Delivery', 'QaTesting', 'forward', 'abc', now()::TIMESTAMP, 9.0),
                (3, 2, 't', 'bravo', 'Delivery', 'Implementing', 'forward', 'abc', now()::TIMESTAMP - INTERVAL 40 DAY, 2.0))
            AS t(run_id, ticket_id, ticket_title, project_key, track, phase, entry_reason, skill_version, started_at, minutes)
            """.Replace("SELECT * FROM", "SELECT *, 'm' AS model, 'claude' AS provider, 'high' AS effort, 'Completed' AS outcome, " +
                "NULL AS phase_after, 'rework' AS rework_class, 1 AS pass, 10 AS tokens, 3 AS num_turns, 2 AS tool_calls, " +
                "0 AS tool_errors, 'transcript' AS split_source, 0 AS lines_changed, NULL AS prompt_version FROM"));

        List<int> Ids(Dictionary<string, string> f, int days = 30, string? sort = null) =>
            a.Runs(new AnalyticsService.RunQuery(days, null, null, f, sort, 50))
             .Select(r => Convert.ToInt32(r["runId"])).ToList();

        Assert.Equal(new[] { 1, 2 }, Ids(new()).Order().ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, Ids(new(), days: 0).Order().ToArray());
        Assert.Equal(new[] { 1 }, Ids(new() { ["entry_reason"] = "qa-failed" }));
        // A missing value is asked for by the name the marts give it.
        Assert.Equal(new[] { 1 }, Ids(new() { ["skill_version"] = "unknown" }));
        // A name that is not a filter is ignored rather than spliced into the SQL.
        Assert.Equal(2, Ids(new() { ["minutes) OR (1"] = "1" }).Count);
        Assert.Equal(new[] { 2, 1 }, Ids(new(), sort: "slowest"));
    }

    [Fact]
    public void A_refresh_needs_uv()
    {
        var a = Service(uv: false);
        var (started, message) = a.StartRefresh();
        Assert.False(started);
        Assert.Contains("uv", message);
        Assert.False(a.GetStatus().Available);
    }

    [Fact]
    public void A_QA_copy_refreshes_without_the_live_fleets_transcripts()
    {
        Assert.False(Service(isolated: true).GetStatus().Transcripts);
        Assert.True(Service().GetStatus().Transcripts);
    }

    [Fact]
    public void Marts_are_stale_until_a_refresh_has_published_them()
    {
        var a = Service();
        var s = a.GetStatus();
        Assert.False(s.HasMarts);
        Assert.True(s.Stale);
        Assert.Null(s.AsOf);
    }

    [Fact]
    public void Snake_case_columns_become_camel_case()
    {
        Assert.Equal("medianPrepS", AnalyticsService.Camel("median_prep_s"));
        Assert.Equal("p90Minutes", AnalyticsService.Camel("p90_minutes"));
        Assert.Equal("runId", AnalyticsService.Camel("run_id"));
    }
}
