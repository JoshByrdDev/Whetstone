using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Whetstone.Services;

/// <summary>
/// Where Claude Code keeps a login's OAuth token, and the one way the dashboard reads and writes it.
/// <para>
/// On Windows and Linux it is <c>.credentials.json</c> in the login's folder. On macOS the CLI keeps
/// it in the login keychain, as a generic password holding the same JSON, and writes the file only
/// when the keychain refuses it. So a Mac is read keychain first and file second, and a write goes
/// back to wherever the read came from - the CLI reads the keychain first too, and a renewal written
/// to the file it is not reading is a renewal it never sees.
/// </para>
/// <para>
/// The keychain item's name is the CLI's, copied: <c>Claude Code-credentials</c>, plus
/// <c>-</c> and the first eight hex digits of the SHA-256 of the config folder when
/// <c>CLAUDE_CONFIG_DIR</c> is set - which it is for every login of <c>claudeAccounts</c>, because
/// that is how a run is pointed at one. The account is the user name. Written twice and the two must
/// agree: here, and <c>Get-ClaudeKeychainService</c> in <c>data/heartbeat-common.ps1</c>.
/// </para>
/// </summary>
public static class ClaudeCredentialStore
{
    /// <summary>A login as found: its JSON, and where to write it back.</summary>
    public sealed record Found(string Text, string? KeychainService, string? FilePath)
    {
        public string Where => KeychainService is not null ? $"the keychain ({KeychainService})" : FilePath!;
    }

    /// <summary>Runs <c>security</c> with these arguments and this stdin; the exit code and stdout.</summary>
    public delegate (int Exit, string Out) SecurityRunner(IReadOnlyList<string> args, string? stdin);

    private const string ServiceBase = "Claude Code-credentials";

    /// <summary>
    /// The CLI's keychain service for a login. <paramref name="configDir"/> is the
    /// <c>CLAUDE_CONFIG_DIR</c> the CLI runs with - the account's folder, or the board's own
    /// environment for the default login - and <paramref name="secureStorageDir"/> the CLI's
    /// override of it, <c>CLAUDE_SECURESTORAGE_CONFIG_DIR</c>, where an empty value means no suffix.
    /// </summary>
    public static string KeychainService(string? configDir, string? secureStorageDir, string defaultDir)
    {
        var plain = secureStorageDir is not null ? secureStorageDir.Length == 0 : string.IsNullOrEmpty(configDir);
        if (plain) return ServiceBase;
        var hashed = (secureStorageDir ?? configDir ?? defaultDir).Normalize(NormalizationForm.FormC);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashed))).ToLowerInvariant();
        return $"{ServiceBase}-{hex[..8]}";
    }

    /// <summary>The keychain account the CLI files the item under: the user name, if it is a plain one.</summary>
    public static string KeychainAccount(string? user) =>
        !string.IsNullOrEmpty(user) && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? user
            : "claude-code-user";

    public static Found? Read(ClaudeAccount account) =>
        Read(ClaudeAccounts.DirOf(account), ServiceFor(account), OperatingSystem.IsMacOS(), RunSecurity);

    /// <summary>Whether the CLI has signed in there. Says nothing about whether the login still works.</summary>
    public static bool Exists(ClaudeAccount account) => Read(account) is not null;

    /// <summary>The seam: where to look, whether there is a keychain, and how to ask it.</summary>
    public static Found? Read(string dir, string service, bool keychain, SecurityRunner security)
    {
        if (keychain)
        {
            var (exit, text) = security(
                ["find-generic-password", "-a", KeychainAccount(User()), "-s", service, "-w"], null);
            if (exit == 0 && !string.IsNullOrWhiteSpace(text)) return new Found(text.Trim(), service, null);
        }

        var path = Path.Combine(dir, ".credentials.json");
        return File.Exists(path) ? new Found(File.ReadAllText(path), null, path) : null;
    }

    /// <summary>
    /// Replaces the login where it was found. A file is swapped in one step, because a half-written
    /// one is a logout for the CLI as well. A keychain item is updated in place (<c>-U</c>), its
    /// JSON handed over hex-encoded on stdin as the CLI does - never in an argument, which any
    /// process on the machine can read while it runs - unless it is too long for one stdin line.
    /// </summary>
    public static void Write(Found where, string text) => Write(where, text, RunSecurity);

    public static void Write(Found where, string text, SecurityRunner security)
    {
        if (where.KeychainService is { } service)
        {
            var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(text));
            var account = KeychainAccount(User());
            var line = $"add-generic-password -U -a \"{account}\" -s \"{service}\" -X \"{hex}\"\n";
            var (exit, _) = line.Length <= StdinLimit
                ? security(["-i"], line)
                : security(["add-generic-password", "-U", "-a", account, "-s", service, "-X", hex], null);
            if (exit != 0) throw new IOException($"security could not update {service} (exit {exit}).");
            return;
        }

        var path = where.FilePath!;
        var temp = path + ".whetstone.tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The longest line <c>security -i</c> reads whole; the CLI keeps under the same bound.</summary>
    private const int StdinLimit = 4032;

    private static string ServiceFor(ClaudeAccount account) =>
        KeychainService(
            account.ConfigDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("CLAUDE_SECURESTORAGE_CONFIG_DIR"),
            ClaudeAccounts.DirOf(account));

    private static string? User()
    {
        var user = Environment.GetEnvironmentVariable("USER");
        if (!string.IsNullOrEmpty(user)) return user;
        try { return Environment.UserName; } catch { return null; }
    }

    private static (int Exit, string Out) RunSecurity(IReadOnlyList<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            // A locked keychain can sit on a prompt nobody is there to answer. Two seconds is the
            // CLI's own bound for the same call.
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(); } catch { /* already gone */ }
                return (-1, "");
            }
            return (p.ExitCode, stdout.Result);
        }
        catch { return (-1, ""); }
    }
}
