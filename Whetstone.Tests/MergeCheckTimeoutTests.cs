using System.Text.Json.Nodes;
using Whetstone.Api;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Settings form edits one thing about a steward merge check, its time limit, and must leave
/// everything else the project's file says about the check exactly as it was.
/// </summary>
public class MergeCheckTimeoutTests
{
    private static JsonObject File() => JsonNode.Parse("""
        { "steward": { "neverMerge": ["infra/"], "mergeChecks": [
            { "name": "unit tests", "run": "run-tests", "timeoutMinutes": 15 },
            { "name": "database tests", "run": "run-db-tests", "whenChanged": ["db/"],
              "machineFault": "database is not running", "timeoutMinutes": 30 },
            { "run": "lint" }
        ] } }
        """)!.AsObject();

    private static JsonArray Checks(JsonObject f) => f["steward"]!["mergeChecks"]!.AsArray();

    [Fact]
    public void Sets_the_named_checks_timeout_and_nothing_else()
    {
        var f = File();
        ConfigEndpoints.SetMergeCheckTimeouts(f, new Dictionary<string, int> { ["database tests"] = 45 });

        Assert.Equal(15, (int)Checks(f)[0]!["timeoutMinutes"]!);
        Assert.Equal(45, (int)Checks(f)[1]!["timeoutMinutes"]!);
        Assert.Equal("database is not running", (string)Checks(f)[1]!["machineFault"]!);
        Assert.Equal("db/", (string)Checks(f)[1]!["whenChanged"]![0]!);
        Assert.Null(Checks(f)[2]!["timeoutMinutes"]);
        Assert.Equal("infra/", (string)f["steward"]!["neverMerge"]![0]!);
    }

    [Fact]
    public void A_check_with_no_name_is_named_by_what_it_runs()
    {
        var f = File();
        ConfigEndpoints.SetMergeCheckTimeouts(f, new Dictionary<string, int> { ["lint"] = 5 });
        Assert.Equal(5, (int)Checks(f)[2]!["timeoutMinutes"]!);
    }

    [Fact]
    public void A_timeout_is_held_to_what_the_steward_reads()
    {
        var f = File();
        ConfigEndpoints.SetMergeCheckTimeouts(f, new Dictionary<string, int> { ["unit tests"] = 500, ["database tests"] = 0 });
        Assert.Equal(120, (int)Checks(f)[0]!["timeoutMinutes"]!);
        Assert.Equal(1, (int)Checks(f)[1]!["timeoutMinutes"]!);
    }

    [Fact]
    public void A_name_it_does_not_have_and_a_file_with_no_steward_change_nothing()
    {
        var f = File();
        var before = f.ToJsonString();
        ConfigEndpoints.SetMergeCheckTimeouts(f, new Dictionary<string, int> { ["no such check"] = 45 });
        Assert.Equal(before, f.ToJsonString());

        var empty = new JsonObject();
        ConfigEndpoints.SetMergeCheckTimeouts(empty, new Dictionary<string, int> { ["unit tests"] = 45 });
        Assert.Equal("{}", empty.ToJsonString());
    }
}
