using System.Text.Json;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The board's one LAN address for a worker host. The API has no authentication, so the firewall
/// rule for this exact address is what keeps every other machine out - which makes every way this
/// could listen wider than asked a test.
/// </summary>
public class LanBindingTests
{
    private static IReadOnlyDictionary<string, JsonElement> Global(string? lanUrl) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            lanUrl is null ? "{}" : $$"""{ "lanUrl": {{JsonSerializer.Serialize(lanUrl)}} }""")!;

    [Fact]
    public void Nothing_changes_unless_it_is_set()
    {
        Assert.Null(LanBinding.Urls("http://localhost:5338", Global(null), false, out var why));
        Assert.Null(why);
        Assert.Null(LanBinding.Urls("http://localhost:5338", Global("  "), false, out why));
        Assert.Null(why);
    }

    [Fact]
    public void The_lan_address_is_added_beside_the_existing_one()
    {
        var urls = LanBinding.Urls("http://localhost:5338", Global("http://192.168.1.20:5338/"), false, out _);
        Assert.Equal(new[] { "http://localhost:5338", "http://192.168.1.20:5338" }, urls);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5338")]
    [InlineData("http://*:5338")]
    [InlineData("http://+:5338")]
    [InlineData("http://[::]:5338")]
    public void A_wildcard_is_refused(string lan)
    {
        Assert.Null(LanBinding.Urls("http://localhost:5338", Global(lan), false, out var why));
        Assert.NotNull(why);
    }

    [Theory]
    [InlineData("192.168.1.20:5338")]
    [InlineData("https://192.168.1.20:5338")]
    [InlineData("http://192.168.1.20")]
    [InlineData("http://192.168.1.20:5338/api")]
    public void Anything_but_an_explicit_http_address_and_port_is_refused(string lan)
    {
        Assert.Null(LanBinding.Urls("http://localhost:5338", Global(lan), false, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void A_QA_copy_never_listens_off_the_machine()
    {
        Assert.Null(LanBinding.Urls("http://localhost:5340", Global("http://192.168.1.20:5340"), true, out var why));
        Assert.Contains("QA copy", why);
    }

    [Fact]
    public void With_no_address_of_its_own_it_adds_nothing()
    {
        Assert.Null(LanBinding.Urls(null, Global("http://192.168.1.20:5338"), false, out var why));
        Assert.NotNull(why);
    }
}
