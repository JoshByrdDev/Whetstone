using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A test of something that only ever happens on Windows - reading what TFVC's tf.exe prints, say,
/// which is Windows paths because tf.exe only runs there. It runs on Windows and is reported as
/// skipped, with the reason, anywhere else. Not for code that runs on a Mac board host too: that is
/// tested there.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows()) Skip = $"Windows only: {because}";
    }
}
