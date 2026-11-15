using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Metrics page's Discovery section, read from the Discovery marts (docs/discovery-track-plan.md,
/// "What is measured"). The numbers are dbt's, tested there on seeded rows
/// (analytics/models/marts/_discovery.yml); these cover what the board does with them: one scope,
/// always the Discovery track, one cut at a time, versions in order, and "not enough verdicts" passed
/// through as the mart says it rather than turned into a zero.
/// </summary>
public sealed class AnalyticsDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-discovery-marts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AnalyticsService Service() =>
        new(new DashboardPaths(_root), new QaIsolation(false), NullLogger<AnalyticsService>.Instance, _ => null);

    private static void Publish(AnalyticsService a, string mart, string select)
    {
        Directory.CreateDirectory(a.MartsDir);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"COPY ({select}) TO '{a.MartPath(mart).Replace('\\', '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Funnel rows: scope, cut, and the handful of columns the page leads with.</summary>
    private static void PublishFunnel(AnalyticsService a) => Publish(a, "mart_discovery_funnel", """
        SELECT * FROM (VALUES
            (30, '*', 'Discovery', 'all', 'all', NULL, 3, 14, 7, 0.5::DOUBLE, true),
            (30, '*', '*', 'all', 'all', NULL, 3, 14, 7, 0.5::DOUBLE, true),
            (30, 'alpha', 'Discovery', 'all', 'all', NULL, 1, 4, 3, NULL::DOUBLE, false),
            (30, '*', 'Discovery', 'week', '2026-11-09', NULL, 1, 4, 1, NULL::DOUBLE, false),
            (30, '*', 'Discovery', 'week', '2026-11-02', NULL, 2, 10, 6, 0.6::DOUBLE, true),
            (30, '*', 'Discovery', 'model', 'opus', NULL, 2, 12, 6, 0.5::DOUBLE, true),
            (30, '*', 'Discovery', 'model', 'fable', NULL, 1, 2, 1, NULL::DOUBLE, false),
            (30, '*', 'Discovery', 'direction', '41', 'Plan your month', 1, 9, 5, NULL::DOUBLE, false),
            (0, '*', 'Discovery', 'all', 'all', NULL, 5, 30, 12, 0.4::DOUBLE, true))
        AS t(window_days, project_scope, track_scope, dimension, dim_value, dim_label, discoveries, closed, approved,
             approval_rate, enough_verdicts)
        """);

    [Fact]
    public void The_section_reads_the_Discovery_track_whatever_the_page_is_on()
    {
        var a = Service();
        PublishFunnel(a);

        var d = a.Discovery(30, null, null);

        Assert.Equal("Discovery", d.Scope.Track);
        Assert.NotNull(d.Summary);
        Assert.Equal(14, Convert.ToInt32(d.Summary!["closed"]));
        Assert.Equal(0.5, Convert.ToDouble(d.Summary["approvalRate"]));
        // The week is the default cut, oldest first, so it reads as a trend.
        Assert.Equal("week", d.By);
        Assert.Equal(["2026-11-02", "2026-11-09"], d.Split.Select(r => (string)r["dimValue"]!));
        Assert.Equal(0.4, Convert.ToDouble(a.Discovery(0, null, null).Summary!["approvalRate"]));
    }

    [Fact]
    public void Below_the_floor_a_rate_stays_null_and_says_so()
    {
        var a = Service();
        PublishFunnel(a);

        var alpha = a.Discovery(30, "alpha", null).Summary!;

        Assert.Equal(false, alpha["enoughVerdicts"]);
        Assert.Null(alpha["approvalRate"]);
        Assert.Equal(3, Convert.ToInt32(alpha["approved"]));
    }

    [Fact]
    public void Any_other_cut_comes_with_the_most_verdicts_first_and_an_unknown_one_falls_back_to_the_week()
    {
        var a = Service();
        PublishFunnel(a);

        var byModel = a.Discovery(30, null, "model");
        Assert.Equal("model", byModel.By);
        Assert.Equal(["opus", "fable"], byModel.Split.Select(r => (string)r["dimValue"]!));

        var direction = Assert.Single(a.Discovery(30, null, "direction").Split);
        Assert.Equal("Plan your month", direction["dimLabel"]);

        var odd = a.Discovery(30, null, "phase; drop table x");
        Assert.Equal("week", odd.By);
        Assert.Equal(2, odd.Split.Count);
    }

    [Fact]
    public void Rejections_come_by_reason_and_versions_in_the_order_they_came()
    {
        var a = Service();
        Publish(a, "mart_discovery_rejections", """
            SELECT * FROM (VALUES
                (30, '*', 'Discovery', 'all', 'all', 'Wrong', 1, 0.25::DOUBLE),
                (30, '*', 'Discovery', 'all', 'all', 'Generic', 3, 0.75::DOUBLE),
                (30, '*', 'Discovery', 'week', '2026-11-02', 'Generic', 3, 1.0::DOUBLE))
            AS t(window_days, project_scope, track_scope, dimension, dim_value, reason, rejections, share_of_rejections)
            """);
        Publish(a, "mart_discovery_versions", """
            SELECT * FROM (VALUES
                (30, '*', 'Discovery', 'taste', 'v1', 2, 0.75::DOUBLE),
                (30, '*', 'Discovery', 'taste', 'no file', 1, 0.25::DOUBLE),
                (30, '*', 'Discovery', 'sift_skill', 'abc123', 1, NULL::DOUBLE))
            AS t(window_days, project_scope, track_scope, kind, version, version_seq, approval_rate)
            """);

        var d = a.Discovery(30, null, null);

        Assert.Equal(["Generic", "Wrong"], d.Rejections.Select(r => (string)r["reason"]!));
        Assert.Equal(1.0, Convert.ToDouble(Assert.Single(d.RejectionsSplit)["shareOfRejections"]));
        Assert.Equal(["sift_skill:abc123", "taste:no file", "taste:v1"],
            d.Versions.Select(r => $"{r["kind"]}:{r["version"]}"));
        // Nothing published for the funnel: no summary, and no rows, rather than a row of zeros.
        Assert.Null(d.Summary);
        Assert.Empty(d.Split);
    }

    [Fact]
    public void The_board_and_the_refresh_publish_the_same_marts()
    {
        // refresh.py's PUBLISHED and AnalyticsService.Marts are a pair: a mart only one of them
        // names is either never read, or read as missing forever (HasMarts stays false).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "analytics", "refresh.py"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var py = File.ReadAllText(Path.Combine(dir!.FullName, "analytics", "refresh.py"));
        var block = Regex.Match(py, @"PUBLISHED\s*=\s*\[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        var published = Regex.Matches(block, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(published.Order(), AnalyticsService.Marts.Order());
        Assert.Contains("fct_discovery_proposals", published);
    }
}
