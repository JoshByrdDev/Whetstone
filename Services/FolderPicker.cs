using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Whetstone.Services;

/// <summary>
/// Native Windows folder picker (the modern <c>IFileOpenDialog</c>) via COM interop.
/// Deliberately avoids a WinForms/WPF dependency — it only calls shell32/user32, which
/// are present on every Windows install, so there is no extra runtime to install and no
/// change to the project's target framework.
///
/// The dialog opens on the machine running the server, so this is only meaningful for the
/// local, interactively-launched dashboard (which is how it's always run).
/// </summary>
public static class FolderPicker
{
    /// <summary>Show the picker and return the chosen absolute path, or null if cancelled/unavailable.</summary>
    public static string? Pick(string? initialPath = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        string? selected = null;
        // IFileOpenDialog requires an STA thread; request threads are MTA.
        var thread = new Thread(() =>
        {
            try { if (OperatingSystem.IsWindows()) selected = ShowDialog(initialPath); }
            catch { selected = null; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return selected;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowDialog(string? initialPath)
    {
        var dialog = (IFileDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS.PickFolders | FOS.ForceFileSystem | FOS.PathMustExist);

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, iid, out var start) == 0 && start is not null)
                    dialog.SetFolder(start);
            }

            // Non-zero HRESULT = cancelled (ERROR_CANCELLED 0x800704C7) or failure.
            var hr = (uint)dialog.Show(GetForegroundWindow());
            if (hr != 0) return null;

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.FileSysPath, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    // ---- Win32 / COM interop ----------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw { }

    // Combined IModalWindow + IFileDialog (QI'd via IID_IFileDialog). Method order is the
    // vtable order — do not reorder. Only the members we use are typed precisely; unused
    // ones keep the correct slot/arg count.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);                                   // IModalWindow
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum FOS : uint
    {
        ForceFileSystem = 0x00000040, // FOS_FORCEFILESYSTEM
        PickFolders = 0x00000020,     // FOS_PICKFOLDERS
        PathMustExist = 0x00000800,   // FOS_PATHMUSTEXIST
    }

    private enum SIGDN : uint
    {
        FileSysPath = 0x80058000, // SIGDN_FILESYSPATH
    }
}
