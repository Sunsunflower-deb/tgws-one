using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TgwsOne.Core;

// NOTE: compiled on every platform but exercised only on Windows (the SP5 Windows 10 VM run).
public static partial class DetachedLauncher
{
    // CreateProcessW with file stdio: stdin = NUL, stdout and stderr = one inheritable handle to the console log.
    // CREATE_BREAKAWAY_FROM_JOB keeps the engine alive when the starter's job object is closed (e.g. a launcher
    // or terminal that kills its job); it is refused with ERROR_ACCESS_DENIED when the job forbids breakaway,
    // in which case we start without it.
    [SupportedOSPlatform("windows")]
    private static Process StartWindows(string exe, IReadOnlyList<string> args, string consoleLog, string workDir)
    {
        IntPtr hIn = Native.InvalidHandle, hOut = Native.InvalidHandle;
        try
        {
            var sa = new Native.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                bInheritHandle = true,
            };
            hIn = Native.CreateFileW("NUL", Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                ref sa, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hIn == Native.InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось открыть NUL");
            hOut = Native.CreateFileW(consoleLog, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                ref sa, Native.CREATE_ALWAYS, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hOut == Native.InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось открыть " + consoleLog);

            string commandLine = BuildWindowsCommandLine(exe, args);
            uint flags = Native.CREATE_NO_WINDOW | Native.CREATE_NEW_PROCESS_GROUP | Native.CREATE_UNICODE_ENVIRONMENT;

            bool TryCreate(uint creationFlags, out Native.PROCESS_INFORMATION info, out int error)
            {
                var si = new Native.STARTUPINFO
                {
                    cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                    dwFlags = Native.STARTF_USESTDHANDLES,
                    hStdInput = hIn,
                    hStdOutput = hOut,
                    hStdError = hOut,
                };
                // CreateProcessW may modify the command line buffer, hence a StringBuilder rather than a string.
                bool ok = Native.CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: true, creationFlags, IntPtr.Zero, workDir, ref si, out info);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }

            bool created = TryCreate(flags | Native.CREATE_BREAKAWAY_FROM_JOB, out Native.PROCESS_INFORMATION pi, out int err);
            if (!created && err == Native.ERROR_ACCESS_DENIED)
                created = TryCreate(flags, out pi, out err);
            if (!created) throw new Win32Exception(err, "не удалось запустить " + exe);

            try
            {
                // Take the Process object before hProcess is closed; the PID stays valid while we hold a reference.
                return Process.GetProcessById(pi.dwProcessId);
            }
            finally
            {
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            // The child has its own inherited copies; ours are no longer needed.
            if (hIn != Native.InvalidHandle) Native.CloseHandle(hIn);
            if (hOut != Native.InvalidHandle) Native.CloseHandle(hOut);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public static readonly IntPtr InvalidHandle = new(-1);

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_ALWAYS = 2;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const int ERROR_ACCESS_DENIED = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
