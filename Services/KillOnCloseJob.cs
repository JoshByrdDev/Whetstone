using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Whetstone.Services;

/// <summary>
/// A Windows job object that ends everything in it when the dashboard's process ends, however it
/// ends - a clean stop, a crash, or a kill from outside.
/// <para>
/// This is what binds the heartbeat to the dashboard. The heartbeat and its workers must never run
/// without the dashboard: the workers' <c>tickets</c> MCP server IS the dashboard, so a fleet that
/// outlives it works blind. Before this, the two only died together by accident, through the job
/// <c>dotnet run</c> keeps its app in, and a dashboard launched any other way (or dying with the
/// <c>dotnet run</c> host still up, as a StopHost did on 2026-09-18) left the heartbeat running on.
/// </para>
/// <para>
/// The handle is opened once and never closed. Closing it is the trigger, so the only thing that
/// may close it is the OS tearing down this process. Children inherit the job, so every worker and
/// every tool a worker starts is in it too.
/// </para>
/// </summary>
public static class KillOnCloseJob
{
    private static readonly object Gate = new();
    private static IntPtr _job;

    /// <summary>
    /// Put <paramref name="process"/> in the job. Returns null on success, or why it could not be.
    /// Call it straight after <c>Start</c>: a child the process launches before it is assigned is
    /// not in the job, and a powershell host takes far longer to reach its first child than this
    /// takes to run.
    /// </summary>
    public static string? Assign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return "job objects are Windows-only";

        lock (Gate)
        {
            if (_job == IntPtr.Zero)
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return $"CreateJobObject failed ({Marshal.GetLastWin32Error()})";

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    {
                        var err = Marshal.GetLastWin32Error();
                        CloseHandle(job);
                        return $"SetInformationJobObject failed ({err})";
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                _job = job;
            }

            return AssignProcessToJobObject(_job, process.Handle)
                ? null
                : $"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})";
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
