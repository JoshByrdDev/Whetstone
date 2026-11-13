using System.Text;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Where the dashboard finds Claude Code's login: the file on Windows and Linux, the keychain first
/// on a Mac. The keychain's names are the CLI's, so they are pinned to what the CLI computes.
/// </summary>
public class ClaudeCredentialStoreTests
{
    private const string Login = """{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1}}""";

    [Fact]
    public void The_default_login_is_the_plain_item_and_a_login_folder_is_suffixed_by_its_hash()
    {
        Assert.Equal("Claude Code-credentials", ClaudeCredentialStore.KeychainService(null, null, "/home/u/.claude"));
        Assert.Equal("Claude Code-credentials", ClaudeCredentialStore.KeychainService("", null, "/home/u/.claude"));
        // sha256("/tmp/x") starts 2e56aa36 - the same digits `shasum -a 256` and the CLI give.
        Assert.Equal("Claude Code-credentials-2e56aa36", ClaudeCredentialStore.KeychainService("/tmp/x", null, "/tmp/x"));
    }

    [Fact]
    public void The_clis_own_override_of_the_folder_wins_and_empty_means_no_suffix()
    {
        Assert.Equal("Claude Code-credentials", ClaudeCredentialStore.KeychainService("/tmp/y", "", "/tmp/y"));
        Assert.Equal("Claude Code-credentials-2e56aa36", ClaudeCredentialStore.KeychainService(null, "/tmp/x", "/home/u/.claude"));
    }

    [Fact]
    public void A_user_name_the_cli_would_not_use_is_its_stand_in()
    {
        Assert.Equal("josh.b-1_x", ClaudeCredentialStore.KeychainAccount("josh.b-1_x"));
        Assert.Equal("claude-code-user", ClaudeCredentialStore.KeychainAccount("josh b"));
        Assert.Equal("claude-code-user", ClaudeCredentialStore.KeychainAccount(null));
    }

    [Fact]
    public void Without_a_keychain_only_the_file_is_read_and_security_is_never_run()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".credentials.json"), Login);

        var found = ClaudeCredentialStore.Read(dir.Path, "svc", keychain: false,
            (_, _) => throw new InvalidOperationException("no keychain off a Mac"));

        Assert.NotNull(found);
        Assert.Equal(Login, found!.Text);
        Assert.Null(found.KeychainService);
        Assert.Equal(Path.Combine(dir.Path, ".credentials.json"), found.FilePath);
    }

    [Fact]
    public void On_a_mac_the_keychain_comes_first()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".credentials.json"), "{\"stale\":true}");
        IReadOnlyList<string>? asked = null;

        var found = ClaudeCredentialStore.Read(dir.Path, "svc", keychain: true, (args, _) =>
        {
            asked = args;
            return (0, Login + "\n");
        });

        Assert.Equal(Login, found!.Text);
        Assert.Equal("svc", found.KeychainService);
        Assert.Null(found.FilePath);
        Assert.Contains("-w", asked!);
        Assert.Equal("svc", asked![asked.ToList().IndexOf("-s") + 1]);
    }

    [Fact]
    public void On_a_mac_with_no_keychain_item_the_clis_plaintext_fallback_is_read()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".credentials.json"), Login);

        var found = ClaudeCredentialStore.Read(dir.Path, "svc", keychain: true, (_, _) => (44, ""));

        Assert.Equal(Login, found!.Text);
        Assert.NotNull(found.FilePath);
    }

    [Fact]
    public void Nowhere_is_no_login()
    {
        using var dir = new TempDir();
        Assert.Null(ClaudeCredentialStore.Read(dir.Path, "svc", keychain: true, (_, _) => (44, "")));
        Assert.Null(ClaudeCredentialStore.Read(dir.Path, "svc", keychain: false, (_, _) => (0, Login)));
    }

    [Fact]
    public void A_keychain_login_is_written_back_on_stdin_never_in_an_argument()
    {
        IReadOnlyList<string>? args = null;
        string? stdin = null;

        ClaudeCredentialStore.Write(new ClaudeCredentialStore.Found(Login, "svc", null), Login, (a, s) =>
        {
            (args, stdin) = (a, s);
            return (0, "");
        });

        Assert.Equal(new[] { "-i" }, args);
        Assert.DoesNotContain("accessToken", stdin!);
        Assert.Contains("add-generic-password -U", stdin!);
        Assert.Contains("-s \"svc\"", stdin!);
        var hex = stdin!.Split("-X \"")[1].Split('"')[0];
        Assert.Equal(Login, Encoding.UTF8.GetString(Convert.FromHexString(hex)));
    }

    [Fact]
    public void A_keychain_login_too_long_for_one_stdin_line_goes_as_hex_in_arguments()
    {
        var big = "{\"claudeAiOauth\":{\"accessToken\":\"" + new string('a', 4000) + "\"}}";
        IReadOnlyList<string>? args = null;

        ClaudeCredentialStore.Write(new ClaudeCredentialStore.Found(big, "svc", null), big, (a, _) =>
        {
            args = a;
            return (0, "");
        });

        Assert.Equal("add-generic-password", args![0]);
        Assert.Equal(big, Encoding.UTF8.GetString(Convert.FromHexString(args[^1])));
    }

    [Fact]
    public void A_refused_keychain_write_is_an_error_the_caller_logs()
    {
        Assert.Throws<IOException>(() => ClaudeCredentialStore.Write(
            new ClaudeCredentialStore.Found(Login, "svc", null), Login, (_, _) => (1, "")));
    }

    [Fact]
    public void A_file_login_is_replaced_in_place()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, ".credentials.json");
        File.WriteAllText(path, "{}");

        ClaudeCredentialStore.Write(new ClaudeCredentialStore.Found("{}", null, path), Login,
            (_, _) => throw new InvalidOperationException("a file login never asks the keychain"));

        Assert.Equal(Login, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".whetstone.tmp"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cred-store-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
