using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamerKit;

/// <summary>
/// A job object so child servers can never outlive us. (WinUI handles the title bar itself,
/// so the dark-mode DWM call the WPF build needed is gone.)
/// </summary>
internal static class Native
{
    // ---- Job object: kill children when the launcher dies (even on a crash) ----

    private static readonly IntPtr Job = CreateKillOnCloseJob();

    public static void AdoptChild(Process process)
    {
        if (Job != IntPtr.Zero)
            AssignProcessToJobObject(Job, process.Handle);
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    // ---- Cursor position, for the mouse-region trigger ----

    /// <summary>
    /// Where the cursor is, in virtual-screen pixels.
    ///
    /// This is deliberately a plain P/Invoke rather than a helper DLL: GetCursorPos is one
    /// short syscall, and a native wrapper would call the same function behind an extra
    /// marshalling boundary. The cheaper-looking alternative, a WH_MOUSE_LL hook, is worse -
    /// it puts a callback of ours in the path of every mouse event on the machine, and
    /// Windows unhooks it if it ever runs slow.
    /// </summary>
    public static (int X, int Y) CursorPosition()
        => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    // ---- Key and mouse-button state, for the hotkey and click triggers ----

    /// <summary>
    /// Is this virtual key held down right now? Mouse buttons are virtual keys too
    /// (<see cref="VirtualKey"/>), so one call answers for both.
    ///
    /// Polled from the same timer as the cursor, and for the same reason: a WH_KEYBOARD_LL
    /// hook would put a callback of ours in the path of every keystroke on the machine, and
    /// Windows silently unhooks it if it ever runs slow. This reads state instead of
    /// intercepting it, so nothing can be swallowed or delayed.
    ///
    /// Only the high bit is used. GetAsyncKeyState's low "pressed since last call" bit is
    /// shared across everyone who calls it, so it is unreliable - the engine works out its
    /// own edges from consecutive polls.
    /// </summary>
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>The virtual-key codes the input triggers need by name.</summary>
    public static class VirtualKey
    {
        public const int LeftButton = 0x01;
        public const int RightButton = 0x02;
        public const int MiddleButton = 0x04;
        public const int XButton1 = 0x05;
        public const int XButton2 = 0x06;

        public const int Shift = 0x10;
        public const int Control = 0x11;
        public const int Alt = 0x12;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    // ---- File dialogs ----
    //
    // The WinRT FileSavePicker / FileOpenPicker return null without ever showing a window in
    // this unpackaged app, so the plain comdlg32 dialogs are used instead. They need no
    // package identity, and they are what Explorer itself puts on screen.

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    public static Task<string?> SaveFileDialog(IntPtr owner, string title, string suggestedName)
        => OnStaThread(() => ShowFileDialog(owner, title, suggestedName, save: true));

    public static Task<string?> OpenFileDialog(IntPtr owner, string title)
        => OnStaThread(() => ShowFileDialog(owner, title, "", save: false));

    /// <summary>
    /// Runs the dialog on a thread of its own.
    ///
    /// Two reasons. The Explorer-style common dialog needs a thread that has been
    /// OleInitialized, which .NET does for an STA thread but which the WinUI UI thread is
    /// not - called from there it simply returns false and never appears. And because the
    /// call blocks until the user answers, keeping it off the UI thread is what stops the
    /// window behind it freezing.
    /// </summary>
    private static Task<string?> OnStaThread(Func<string?> work)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { answer.SetResult(work()); }
            catch (Exception ex) { answer.SetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return answer.Task;
    }

    /// <summary>Returns the chosen path, or null if the user cancelled.</summary>
    private static string? ShowFileDialog(IntPtr owner, string title, string suggestedName, bool save)
    {
        const int MaxPath = 1024;

        // Both of these hold embedded nulls or get written to by the dialog, so neither can
        // be marshalled as a plain string.
        var fileBuffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        var filterBuffer = Marshal.StringToHGlobalUni("JSON files\0*.json\0All files\0*.*\0\0");

        try
        {
            for (var i = 0; i < MaxPath; i++)
                Marshal.WriteInt16(fileBuffer, i * sizeof(char), i < suggestedName.Length ? suggestedName[i] : '\0');

            var options = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = owner,
                lpstrFilter = filterBuffer,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = MaxPath,
                lpstrTitle = title,
                lpstrDefExt = "json",
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST
                      | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST)
            };

            var picked = save ? GetSaveFileName(ref options) : GetOpenFileName(ref options);
            if (picked) return Marshal.PtrToStringUni(fileBuffer);

            // False means cancelled *or* failed, and they are not the same thing: a silent
            // no-op on failure is exactly what makes this kind of bug invisible.
            var error = CommDlgExtendedError();
            if (error != 0)
                throw new InvalidOperationException($"the file dialog could not open (comdlg error 0x{error:X4})");

            return null;   // genuinely cancelled
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OPENFILENAME options);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OPENFILENAME options);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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
}
