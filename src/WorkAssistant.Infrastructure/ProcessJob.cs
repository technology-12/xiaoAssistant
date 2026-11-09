using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkAssistant.Infrastructure
{
    // Windows 7 job object: kill worker on desktop exit; cap committed memory.
    internal sealed class ProcessJob : IDisposable
    {
        private IntPtr _handle;
        [StructLayout(LayoutKind.Sequential)] private struct Basic
        { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct Counters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct Extended
        { public Basic Basic; public Counters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref Extended info, uint length);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        public ProcessJob(Process process, ulong bytes, uint maximumProcesses = 2)
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            var info = new Extended { Basic = new Basic { Flags = 0x2000 | 0x200 | 0x8, ActiveProcesses = maximumProcesses }, JobMemory = new UIntPtr(bytes) };
            if (_handle == IntPtr.Zero || !SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf(typeof(Extended))) || !AssignProcessToJobObject(_handle, process.Handle))
            {
                int error = Marshal.GetLastWin32Error(); Dispose();
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new Win32Exception(error, "无法建立任务进程资源边界。");
            }
        }
        public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
    }
}
