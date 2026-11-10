using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Which PowerShell the board starts the fleet's scripts on. 7 where it is installed, 5.1 where it
/// is not, and <c>psHost: powershell</c> as the way back - the same rule as Get-PsHost in
/// heartbeat-common.ps1, which runner.tests.ps1 pins for the scripts' half.
/// </summary>
public class PowerShellHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-pshost-" + Guid.NewGuid().ToString("N")[..8]);

    public PowerShellHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Seven = @"C:\Program Files\PowerShell\7\pwsh.exe";

    [Theory]
    [InlineData(null, Seven, true, Seven)]
    [InlineData("auto", Seven, true, Seven)]
    [InlineData(null, null, true, "powershell")]
    [InlineData(" Powershell ", Seven, true, "powershell")]
    [InlineData("pwsh", null, true, "pwsh")]
    [InlineData(null, null, false, "pwsh")]
    [InlineData("anything else", null, true, "powershell")]
    public void Chooses_seven_where_installed_and_the_setting_pins_it(string? setting, string? pwsh, bool windows, string want) =>
        Assert.Equal(want, PowerShellHost.Choose(setting, pwsh, windows));

    [Fact]
    public void Finds_seven_on_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(_root, "pwsh.exe");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, PowerShellHost.FindPwsh(_root, Path.Combine(_root, "none")));
    }

    [Fact]
    public void Finds_seven_in_its_install_folder_when_path_is_stale()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(_root, "PowerShell", "7", "pwsh.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        Assert.Equal(exe, PowerShellHost.FindPwsh(Path.Combine(_root, "empty"), _root));
    }

    [Fact]
    public void No_seven_anywhere_is_none()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(PowerShellHost.FindPwsh(Path.Combine(_root, "empty"), Path.Combine(_root, "none")));
    }
}
