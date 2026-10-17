using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Whetstone.Api;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// What a runner is told when it checks in. A configured host learns the board host's commit, which
/// is how it knows to bring itself onto it once idle; a name the config does not have learns nothing
/// but that it is not one.
/// </summary>
public class WorkerHostCheckinTests
{
    private const string Board = "1111111111111111111111111111111111111111";

    private static (int Status, JsonElement Body) Read(IResult r)
    {
        var status = ((IStatusCodeHttpResult)r).StatusCode ?? 0;
        var value = ((IValueHttpResult)r).Value;
        return (status, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void A_known_host_is_told_the_board_hosts_commit()
    {
        var (status, body) = Read(WorkerHostEndpoints.CheckinResult("bravo-host",
            new[] { new HostOrder("kill", 5, 9, null) }, new[] { 3 }, Board));
        Assert.Equal(200, status);
        Assert.Equal(Board, body.GetProperty("boardCommit").GetString());
        Assert.Equal(1, body.GetProperty("orders").GetArrayLength());
        Assert.Equal(3, body.GetProperty("revoked")[0].GetInt32());
    }

    [Fact]
    public void A_board_that_cannot_read_its_commit_says_so_as_null()
    {
        var (_, body) = Read(WorkerHostEndpoints.CheckinResult("bravo-host", Array.Empty<HostOrder>(), Array.Empty<int>(), null));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("boardCommit").ValueKind);
    }

    [Fact]
    public void An_unknown_host_gets_404_and_no_commit()
    {
        var (status, body) = Read(WorkerHostEndpoints.CheckinResult("charlie", null, Array.Empty<int>(), Board));
        Assert.Equal(404, status);
        Assert.False(body.TryGetProperty("boardCommit", out _));
        Assert.Contains("charlie", body.GetProperty("error").GetString());
    }
}
