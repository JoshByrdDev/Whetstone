using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Whetstone.Services;

/// <summary>
/// Which of a checkout's files another program is holding open in a way that stops git replacing
/// them, and which program that is.
///
/// <para>
/// Git rewrites a file on checkout by unlinking it and writing a new one. Windows refuses the unlink
/// while any other process has the file open without <c>FILE_SHARE_DELETE</c> - an editor, an IDE, a
/// dev server, an indexer - and Git for Windows then asks "Should I try again?". The retry answerer
/// (<c>TicketGitService.RetryAnswerer</c>) says yes, which is right for a reader that lets go in a
/// moment and exactly wrong for one that does not: the merge then sits, silent, for as long as the
/// file stays open, and is killed mid-checkout by its timeout. Ticket 15's merge waited on this.
/// Asking first turns that wait into a refusal that names the file and its holder.
/// </para>
/// </summary>
public static class HeldFiles
{
    public record Held(string Path, IReadOnlyList<string> Holders);

    /// <summary>
    /// The files among <paramref name="relativePaths"/> that cannot be deleted right now and are still
    /// held after a short second look - so a status poll that has one open for a millisecond is not
    /// mistaken for an editor. Missing files are skipped. Empty off Windows.
    /// </summary>
    public static async Task<IReadOnlyList<Held>> FindAsync(string root, IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<Held>();

        var suspects = relativePaths
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => IsHeld(Full(root, p)))
            .ToList();

        for (var i = 0; i < 3 && suspects.Count > 0; i++)
        {
            await Task.Delay(500, ct);
            suspects = suspects.Where(p => IsHeld(Full(root, p))).ToList();
        }

        return suspects.Select(p => new Held(p, HoldersOf(Full(root, p)))).ToList();
    }

    private static string Full(string root, string rel) =>
        Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Would a delete be refused? Asks for exactly the access an unlink needs and nothing else.</summary>
    private static bool IsHeld(string path)
    {
        if (!File.Exists(path)) return false;
        using var h = CreateFileW(path, DELETE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return false;
        var err = Marshal.GetLastWin32Error();
        return err is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;
    }

    /// <summary>The processes holding a file, by the Restart Manager - the same lookup Explorer's "file in use" uses.</summary>
    private static IReadOnlyList<string> HoldersOf(string path)
    {
        try
        {
            if (RmStartSession(out var session, 0, new StringBuilder(64)) != 0) return Array.Empty<string>();
            try
            {
                if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0)
                    return Array.Empty<string>();

                uint count = 0, reasons = 0;
                var rc = RmGetList(session, out var needed, ref count, null, ref reasons);
                if (rc != ERROR_MORE_DATA || needed == 0) return Array.Empty<string>();

                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out _, ref count, infos, ref reasons) != 0) return Array.Empty<string>();

                return infos.Take((int)count).Select(Describe).Distinct().ToList();
            }
            finally { RmEndSession(session); }
        }
        catch { return Array.Empty<string>(); }
    }

    private static string Describe(RM_PROCESS_INFO p)
    {
        var pid = p.Process.dwProcessId;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return $"{proc.ProcessName}.exe (pid {pid})";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(p.strAppName) ? $"pid {pid}" : $"{p.strAppName} (pid {pid})";
        }
    }

    // ---- interop ------------------------------------------------------------

    private const uint DELETE = 0x00010000;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
